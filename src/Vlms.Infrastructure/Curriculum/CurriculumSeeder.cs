using System.Linq.Expressions;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Vlms.Domain;

namespace Vlms.Infrastructure.Curriculum;

/// <summary>
/// Outcome of a <see cref="CurriculumSeeder.SeedAsync"/> run. When <see cref="Errors"/> is non-empty
/// nothing was written (no database rows, no blobs) and the counters are all zero.
/// <see cref="Warnings"/> never block a run: they report lessons/ranks in the database that the
/// manifest no longer mentions (left untouched, as lessons may have completions).
/// </summary>
public sealed record CurriculumSeedResult(
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings,
    int RanksCreated,
    int RanksUpdated,
    int LessonsCreated,
    int LessonsUpdated,
    int FilesUploaded)
{
    public bool Succeeded => Errors.Count == 0;
}

/// <summary>
/// Idempotent seeder for the rank ladder and curriculum (STATE.md "Seed rank ladder + curriculum";
/// docs/requirements/functional.md "Curriculum management"), driven by the cleansed manifest
/// <c>seed/curriculum.json</c> plus a content root holding the lesson folders (not in git). Wrapped
/// by the <c>seed-curriculum</c> command of the <c>Vlms.Jobs</c> host; the logic lives here so it is
/// testable.
///
/// <b>Properties every change must keep:</b>
/// <list type="bullet">
/// <item><b>Validate first, write nothing on error.</b> Every lesson folder must exist, every tag must
/// map to a <see cref="LessonTag"/>, codes must be unique. All errors are collected, not just the first.</item>
/// <item><b>Idempotent.</b> Ranks and lessons are matched on <c>Code</c>; rows that already match are
/// untouched, so a re-run changes nothing and Ids and blob keys are stable. Blob keys are deterministic
/// (<c>lessons/{lessonCode}/{relative path}</c>) and uploads overwrite, so a changed file is applied.</item>
/// <item><b>Never deletes.</b> Lessons absent from the manifest are reported as warnings only.</item>
/// <item><b>Application-assigned Ids</b> (max + 1), as elsewhere in the codebase.</item>
/// </list>
///
/// Uploads happen before the single <c>SaveChanges</c>: a failed upload leaves the database untouched,
/// and re-running simply overwrites the same keys.
/// </summary>
public sealed class CurriculumSeeder
{
    /// <summary>Name of the per-lesson subfolder whose files follow the top-level documents.</summary>
    public const string ImagesFolderName = "Images";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private readonly VlmsDbContext _db;
    private readonly IBlobStorage _blobStorage;

    public CurriculumSeeder(VlmsDbContext db, IBlobStorage blobStorage)
    {
        _db = db;
        _blobStorage = blobStorage;
    }

    public async Task<CurriculumSeedResult> SeedAsync(string manifestPath, string contentRoot, CancellationToken ct = default)
    {
        var errors = new List<string>();
        var manifest = ReadManifest(manifestPath, errors);
        if (manifest is null)
        {
            return Failed(errors);
        }

        var plan = Validate(manifest, contentRoot, errors);
        if (errors.Count > 0)
        {
            return Failed(errors);
        }

        // Uploads (deterministic keys, overwrite) come before any database write.
        var filesUploaded = 0;
        foreach (var file in plan.SelectMany(r => r.Lessons).SelectMany(l => l.Files))
        {
            ct.ThrowIfCancellationRequested();
            await _blobStorage.UploadAsync(file.BlobKey, await File.ReadAllBytesAsync(file.FullPath, ct), ct);
            filesUploaded++;
        }

        var ranksCreated = 0;
        var ranksUpdated = 0;
        var lessonsCreated = 0;
        var lessonsUpdated = 0;

        var ranks = await _db.Ranks.ToDictionaryAsync(r => r.Code, ct);
        var lessons = await _db.Lessons.ToDictionaryAsync(l => l.Code, ct);
        var tags = (await _db.LessonTagAssignments.ToListAsync(ct)).ToLookup(t => t.LessonId);
        var files = (await _db.LessonFiles.ToListAsync(ct)).ToLookup(f => f.LessonId);

        var nextRankId = await NextIdAsync(_db.Ranks.Select(r => r.Id), ct);
        var nextLessonId = await NextIdAsync(_db.Lessons.Select(l => l.Id), ct);
        var nextFileId = await NextIdAsync(_db.LessonFiles.Select(f => f.Id), ct);

        foreach (var planRank in plan)
        {
            if (ranks.TryGetValue(planRank.Code, out var rank))
            {
                if (rank.Order != planRank.Order || rank.Name != planRank.Name)
                {
                    SetValue(rank, r => r.Order, planRank.Order);
                    SetValue(rank, r => r.Name, planRank.Name);
                    ranksUpdated++;
                }
            }
            else
            {
                rank = new Rank { Id = nextRankId++, Order = planRank.Order, Code = planRank.Code, Name = planRank.Name };
                _db.Ranks.Add(rank);
                ranks[rank.Code] = rank;
                ranksCreated++;
            }

            foreach (var planLesson in planRank.Lessons)
            {
                var isNew = !lessons.TryGetValue(planLesson.Code, out var lesson);
                var changed = false;
                if (isNew)
                {
                    lesson = new Lesson
                    {
                        Id = nextLessonId++,
                        RankId = rank.Id,
                        Code = planLesson.Code,
                        Title = planLesson.Title,
                        Order = planLesson.Order,
                        IsActive = true
                    };
                    _db.Lessons.Add(lesson);
                    lessons[lesson.Code] = lesson;
                    lessonsCreated++;
                }
                else if (lesson!.RankId != rank.Id || lesson.Title != planLesson.Title
                    || lesson.Order != planLesson.Order || !lesson.IsActive)
                {
                    SetValue(lesson, l => l.RankId, rank.Id);
                    SetValue(lesson, l => l.Title, planLesson.Title);
                    SetValue(lesson, l => l.Order, planLesson.Order);
                    SetValue(lesson, l => l.IsActive, true);
                    changed = true;
                }

                // Tags: apply the difference only.
                var existingTags = tags[lesson!.Id].ToList();
                foreach (var stale in existingTags.Where(t => !planLesson.Tags.Contains(t.Tag)))
                {
                    _db.LessonTagAssignments.Remove(stale);
                    changed = true;
                }
                foreach (var tag in planLesson.Tags.Where(t => existingTags.All(e => e.Tag != t)))
                {
                    _db.LessonTagAssignments.Add(new LessonTagAssignment { LessonId = lesson.Id, Tag = tag });
                    changed = true;
                }

                // Files: an identical sequence leaves the rows (and Ids) alone; otherwise replace the set.
                var existingFiles = files[lesson.Id].OrderBy(f => f.Order).ToList();
                var sameFiles = existingFiles.Count == planLesson.Files.Count
                    && existingFiles.Zip(planLesson.Files).All(p =>
                        p.First.Order == p.Second.Order && p.First.FileName == p.Second.FileName && p.First.BlobKey == p.Second.BlobKey);
                if (!sameFiles)
                {
                    _db.LessonFiles.RemoveRange(existingFiles);
                    foreach (var file in planLesson.Files)
                    {
                        _db.LessonFiles.Add(new LessonFile
                        {
                            Id = nextFileId++,
                            LessonId = lesson.Id,
                            Order = file.Order,
                            FileName = file.FileName,
                            BlobKey = file.BlobKey
                        });
                    }
                    changed = true;
                }

                if (changed && !isNew)
                {
                    lessonsUpdated++;
                }
            }
        }

        var manifestRankCodes = plan.Select(r => r.Code).ToHashSet(StringComparer.Ordinal);
        var manifestLessonCodes = plan.SelectMany(r => r.Lessons).Select(l => l.Code).ToHashSet(StringComparer.Ordinal);
        var warnings = new List<string>();
        warnings.AddRange(lessons.Keys.Where(c => !manifestLessonCodes.Contains(c)).Order(StringComparer.Ordinal)
            .Select(c => $"Lesson '{c}' is in the database but not in the manifest; left untouched."));
        warnings.AddRange(ranks.Keys.Where(c => !manifestRankCodes.Contains(c)).Order(StringComparer.Ordinal)
            .Select(c => $"Rank '{c}' is in the database but not in the manifest; left untouched."));

        await _db.SaveChangesAsync(ct);

        return new CurriculumSeedResult(errors, warnings, ranksCreated, ranksUpdated, lessonsCreated, lessonsUpdated, filesUploaded);
    }

    // Entities are init-only, so existing rows are changed through the change tracker.
    private void SetValue<TEntity, TValue>(TEntity entity, Expression<Func<TEntity, TValue>> property, TValue value)
        where TEntity : class
        => _db.Entry(entity).Property(property).CurrentValue = value;

    private static async Task<int> NextIdAsync(IQueryable<int> ids, CancellationToken ct)
        => (await ids.Select(i => (int?)i).MaxAsync(ct) ?? 0) + 1;

    private static CurriculumSeedResult Failed(List<string> errors) => new(errors, [], 0, 0, 0, 0, 0);

    // --- Manifest parsing and validation ------------------------------------------------------

    private static ManifestDto? ReadManifest(string manifestPath, List<string> errors)
    {
        if (!File.Exists(manifestPath))
        {
            errors.Add($"Manifest not found: {manifestPath}");
            return null;
        }

        try
        {
            var manifest = JsonSerializer.Deserialize<ManifestDto>(File.ReadAllText(manifestPath), JsonOptions);
            if (manifest?.Ranks is null)
            {
                errors.Add("Manifest has no 'ranks' array.");
                return null;
            }
            return manifest;
        }
        catch (JsonException ex)
        {
            errors.Add($"Manifest is not valid JSON: {ex.Message}");
            return null;
        }
    }

    private static List<PlannedRank> Validate(ManifestDto manifest, string contentRoot, List<string> errors)
    {
        var plan = new List<PlannedRank>();
        var rootExists = Directory.Exists(contentRoot);
        if (!rootExists)
        {
            errors.Add($"Content root not found: {contentRoot}");
        }

        var rankCodes = new HashSet<string>(StringComparer.Ordinal);
        var lessonCodes = new HashSet<string>(StringComparer.Ordinal);
        var rankOrders = new HashSet<int>();

        foreach (var rank in manifest.Ranks!)
        {
            var rankLabel = $"Rank '{rank.Code}'";
            if (string.IsNullOrWhiteSpace(rank.Code) || string.IsNullOrWhiteSpace(rank.Name))
            {
                errors.Add($"A rank (order {rank.Order}) is missing its code or name.");
                continue;
            }
            if (!rankCodes.Add(rank.Code)) errors.Add($"{rankLabel}: duplicate rank code.");
            if (!rankOrders.Add(rank.Order)) errors.Add($"{rankLabel}: duplicate rank order {rank.Order}.");

            var plannedLessons = new List<PlannedLesson>();
            var lessonOrders = new HashSet<int>();
            foreach (var lesson in rank.Lessons ?? [])
            {
                var label = $"Lesson '{lesson.Code}'";
                if (string.IsNullOrWhiteSpace(lesson.Code) || string.IsNullOrWhiteSpace(lesson.Title))
                {
                    errors.Add($"{rankLabel}: a lesson (order {lesson.Order}) is missing its code or title.");
                    continue;
                }
                if (!lessonCodes.Add(lesson.Code)) errors.Add($"{label}: duplicate lesson code.");
                if (!lessonOrders.Add(lesson.Order)) errors.Add($"{label}: duplicate order {lesson.Order} within {rankLabel}.");

                var lessonTags = new HashSet<LessonTag>();
                foreach (var name in lesson.Tags ?? [])
                {
                    if (TryParseTag(name, out var tag)) lessonTags.Add(tag);
                    else errors.Add($"{label}: unknown tag '{name}'.");
                }

                var files = new List<PlannedFile>();
                if (string.IsNullOrWhiteSpace(rank.Source) || string.IsNullOrWhiteSpace(lesson.Source))
                {
                    errors.Add($"{label}: the rank or lesson has no 'source' folder.");
                }
                else if (rootExists)
                {
                    var folder = Path.Combine(contentRoot, rank.Source, lesson.Source);
                    if (!Directory.Exists(folder))
                    {
                        errors.Add($"{label}: folder not found: {folder}");
                    }
                    else
                    {
                        files = ListFiles(folder, lesson.Code);
                    }
                }

                plannedLessons.Add(new PlannedLesson(lesson.Code, lesson.Title, lesson.Order, lessonTags, files));
            }

            plan.Add(new PlannedRank(rank.Code, rank.Name, rank.Order, plannedLessons));
        }

        return plan;
    }

    /// <summary>Display names ("Church Life") map to enum members ("ChurchLife") ignoring case, spaces and hyphens.</summary>
    private static bool TryParseTag(string? name, out LessonTag tag)
    {
        tag = default;
        if (string.IsNullOrWhiteSpace(name)) return false;
        var compact = new string(name.Where(char.IsLetter).ToArray());
        foreach (var candidate in Enum.GetValues<LessonTag>())
        {
            if (string.Equals(candidate.ToString(), compact, StringComparison.OrdinalIgnoreCase))
            {
                tag = candidate;
                return true;
            }
        }
        return false;
    }

    /// <summary>Top-level documents (alphabetical) first, then the Images subfolder's files (alphabetical).</summary>
    private static List<PlannedFile> ListFiles(string folder, string lessonCode)
    {
        var result = new List<PlannedFile>();

        foreach (var path in Directory.EnumerateFiles(folder).Where(p => !IsJunk(p))
                     .OrderBy(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase))
        {
            result.Add(Planned(lessonCode, Path.GetFileName(path), path, result.Count + 1));
        }

        var images = Directory.EnumerateDirectories(folder)
            .FirstOrDefault(d => string.Equals(Path.GetFileName(d), ImagesFolderName, StringComparison.OrdinalIgnoreCase));
        if (images is not null)
        {
            foreach (var path in Directory.EnumerateFiles(images, "*", SearchOption.AllDirectories).Where(p => !IsJunk(p))
                         .OrderBy(p => Path.GetRelativePath(folder, p).Replace('\\', '/'), StringComparer.OrdinalIgnoreCase))
            {
                result.Add(Planned(lessonCode, Path.GetRelativePath(folder, path).Replace('\\', '/'), path, result.Count + 1));
            }
        }

        return result;
    }

    private static PlannedFile Planned(string lessonCode, string relativePath, string fullPath, int order)
        => new(order, Path.GetFileName(fullPath), $"lessons/{lessonCode}/{relativePath}", fullPath);

    private static bool IsJunk(string path)
    {
        var name = Path.GetFileName(path);
        return name.StartsWith("~$", StringComparison.Ordinal)
            || name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Thumbs.db", StringComparison.OrdinalIgnoreCase)
            || name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase);
    }

    private sealed record PlannedRank(string Code, string Name, int Order, List<PlannedLesson> Lessons);
    private sealed record PlannedLesson(string Code, string Title, int Order, HashSet<LessonTag> Tags, List<PlannedFile> Files);
    private sealed record PlannedFile(int Order, string FileName, string BlobKey, string FullPath);

    private sealed class ManifestDto
    {
        public List<RankDto>? Ranks { get; set; }
    }

    private sealed class RankDto
    {
        public int Order { get; set; }
        public string? Code { get; set; }
        public string? Name { get; set; }
        public string? Source { get; set; }
        public List<LessonDto>? Lessons { get; set; }
    }

    private sealed class LessonDto
    {
        public int Order { get; set; }
        public string? Code { get; set; }
        public string? Title { get; set; }
        public List<string>? Tags { get; set; }
        public string? Source { get; set; }
    }
}
