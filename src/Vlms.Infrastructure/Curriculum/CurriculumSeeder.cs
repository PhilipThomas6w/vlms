using System.Linq.Expressions;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Vlms.Domain;

namespace Vlms.Infrastructure.Curriculum;

/// <summary>
/// Outcome of a <see cref="CurriculumSeeder.SeedAsync"/> run. When <see cref="Errors"/> is non-empty
/// nothing was written (no database rows, no blobs) and the counters are all zero.
/// <see cref="FileNamesAdded"/> counts new entries written to the file-name list; <see cref="FilesRenamed"/>
/// counts existing file rows renamed in place. <see cref="Warnings"/> never block a run: they report lessons/ranks in the database that the
/// manifest no longer mentions (left untouched, as lessons may have completions).
/// <see cref="LessonTextsUpdated"/> counts lessons and ranks whose text (lesson body or Teacher's Information) or
/// pictures changed; <see cref="PicturesUploaded"/> counts resized pictures written to storage.
/// </summary>
public sealed record CurriculumSeedResult(
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings,
    int RanksCreated,
    int RanksUpdated,
    int LessonsCreated,
    int LessonsUpdated,
    int FilesUploaded,
    int FileNamesAdded = 0,
    int FilesRenamed = 0,
    int LessonTextsUpdated = 0,
    int PicturesUploaded = 0)
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
/// <item><b>File Ids are stable.</b> When only a lesson's file names differ (same order, same blob keys)
/// the rows are renamed in place, because saved offline lessons and links refer to the Ids; the set is
/// replaced only when the order or the blob keys change.</item>
/// <item><b>Hand-corrected names win.</b> <c>seed/file-names.json</c> (keyed by blob key: original name
/// and clean name) only ever gains entries for new files, made by <see cref="LessonFileNames.CleanName"/>;
/// existing entries are never changed or removed, so a hand edit survives every run. An entry for a
/// file no longer present is a warning, not an error. The stored name is the clean name plus the
/// original extension (FR-006); only seeded files are named this way.</item>
/// </list>
///
/// <b>App-hosted lessons</b> (docs/adr/0012-app-hosted-lessons.md): the folder <c>lessons</c> under the content root
/// (<c>seed/lessons</c>, in git) holds the converted lessons: <c>{lesson code}.md</c> is the lesson text, its pictures in
/// the folder <c>{lesson code}/</c>, referred to by bare file name. <c>R{rank order}-TI.md</c> (or
/// <c>{rank code}-TI.md</c>) is a rank's Teacher's Information, pictures in the folder of the same name. Every picture
/// the text refers to must exist and be a JPEG or PNG (an error otherwise); each is stored resized
/// (<see cref="LessonPictureResizer"/>) as a <see cref="LessonPicture"/> or <see cref="TeachersInformationPicture"/>,
/// matched on file name so Ids stay stable. The lesson's <see cref="Lesson.ContentVersion"/> is a hash of its text and
/// its original pictures, so it changes exactly when either does, and a lesson's pictures are uploaded only when it
/// changes or a picture is missing from storage. A lesson or rank with text in the database but no file keeps it
/// (a warning): nothing is cleared by a missing file.
///
/// <b>Lesson versions</b> (docs/adr/0013-lesson-suggestions-review-and-history.md decisions 4 and 9): each lesson the
/// seeder creates or changes is recorded as its next <see cref="LessonVersion"/> of kind
/// <see cref="LessonVersionKind.Converted"/>. A lesson whose latest version did not come from the seeder (it was changed
/// through lesson suggestions) is never written again: it is skipped, with a warning, files and pictures included. Lesson
/// pictures are stored under a key naming their content (<c>lesson-pictures/{code}/{hash}/{name}</c>), so a changed
/// picture never overwrites one an earlier version shows. The rank's Teacher's Information stays seeder-managed.
///
/// Running it: <c>Vlms.Jobs seed-curriculum --manifest seed/curriculum.json --content-root seed
/// --file-names seed/file-names.json</c>. Add <c>--names-only</c> to write the list and stop (no
/// uploads, no database), to review or hand-correct it first; run again without it to apply. With no
/// <c>--file-names</c> the names are left exactly as the files are called (existing behaviour).
///
/// Uploads happen before the single <c>SaveChanges</c>: a failed upload leaves the database untouched,
/// and re-running simply overwrites the same keys.
/// </summary>
public sealed class CurriculumSeeder
{
    /// <summary>Name of the per-lesson subfolder whose files follow the top-level documents.</summary>
    public const string ImagesFolderName = "Images";

    /// <summary>Folder under the content root holding the converted lessons (adr/0012).</summary>
    public const string LessonsFolderName = "lessons";

    /// <summary>Suffix of a rank's Teacher's Information file: <c>R5-TI.md</c>.</summary>
    public const string TeachersInformationSuffix = "-TI";

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

    public async Task<CurriculumSeedResult> SeedAsync(string manifestPath, string contentRoot, CancellationToken ct = default,
        string? fileNamesPath = null, bool namesOnly = false)
    {
        var errors = new List<string>();
        var manifest = ReadManifest(manifestPath, errors);
        if (manifest is null)
        {
            return Failed(errors);
        }

        var plan = Validate(manifest, contentRoot, errors);
        var warnings = new List<string>();
        var namesAdded = 0;
        // Lesson text is read even when the manifest has errors, so one run reports every problem.
        ReadLessonTexts(plan, Path.Combine(contentRoot, LessonsFolderName), errors, warnings);
        if (errors.Count > 0)
        {
            return Failed(errors);
        }
        if (fileNamesPath is not null)
        {
            namesAdded = ApplyFileNames(plan, fileNamesPath, errors, warnings);
        }
        if (errors.Count > 0)
        {
            return Failed(errors);
        }

        if (namesOnly)
        {
            return new CurriculumSeedResult(errors, warnings, 0, 0, 0, 0, 0, namesAdded);
        }

        // Lessons changed through lesson suggestions since they were converted belong to the suggestions now (adr/0013
        // decision 9): the seeder never writes them again, and says so.
        var latestKinds = (await _db.LessonVersions.AsNoTracking().Select(v => new { v.LessonId, v.Number, v.Kind }).ToListAsync(ct))
            .GroupBy(v => v.LessonId).ToDictionary(g => g.Key, g => g.MaxBy(v => v.Number)!);
        var lessonIds = await _db.Lessons.AsNoTracking().ToDictionaryAsync(l => l.Code, l => l.Id, ct);
        var suggestionOwned = plan.SelectMany(r => r.Lessons)
            .Where(l => lessonIds.TryGetValue(l.Code, out var id) && latestKinds.TryGetValue(id, out var latest) && latest.Kind != LessonVersionKind.Converted)
            .Select(l => l.Code).ToHashSet(StringComparer.Ordinal);
        foreach (var code in suggestionOwned.Order(StringComparer.Ordinal))
        {
            warnings.Add($"Lesson '{code}' has been changed through lesson suggestions since it was converted; left untouched.");
        }

        // Uploads (deterministic keys, overwrite) come before any database write.
        var filesUploaded = 0;
        foreach (var file in plan.SelectMany(r => r.Lessons).Where(l => !suggestionOwned.Contains(l.Code)).SelectMany(l => l.Files))
        {
            ct.ThrowIfCancellationRequested();
            await _blobStorage.UploadAsync(file.BlobKey, await File.ReadAllBytesAsync(file.FullPath, ct), ct);
            filesUploaded++;
        }

        // Pictures: a lesson's only when its text or pictures changed (or one is missing from storage); the Teacher's
        // Information pictures every run (few, and they carry no version). Still before any database write.
        var picturesUploaded = 0;
        var storedVersions = await _db.Lessons.AsNoTracking().ToDictionaryAsync(l => l.Code, l => l.ContentVersion, ct);
        var texts = plan.SelectMany(r => r.Lessons).Where(l => l.Text is not null && !suggestionOwned.Contains(l.Code))
            .Select(l => (Text: l.Text!, Unchanged: storedVersions.GetValueOrDefault(l.Code) == l.Text!.Version))
            .Concat(plan.Where(r => r.TeachersInformation is not null).Select(r => (Text: r.TeachersInformation!, Unchanged: false)));
        foreach (var (text, unchanged) in texts)
        {
            foreach (var picture in text.Pictures)
            {
                ct.ThrowIfCancellationRequested();
                if (unchanged && await _blobStorage.ExistsAsync(picture.BlobKey, ct))
                {
                    continue;
                }
                await _blobStorage.UploadAsync(picture.BlobKey, picture.Content, ct);
                picturesUploaded++;
            }
        }

        var ranksCreated = 0;
        var ranksUpdated = 0;
        var lessonsCreated = 0;
        var lessonsUpdated = 0;
        var filesRenamed = 0;
        var textsUpdated = 0;

        var ranks = await _db.Ranks.ToDictionaryAsync(r => r.Code, ct);
        var lessons = await _db.Lessons.ToDictionaryAsync(l => l.Code, ct);
        var tags = (await _db.LessonTagAssignments.ToListAsync(ct)).ToLookup(t => t.LessonId);
        var files = (await _db.LessonFiles.ToListAsync(ct)).ToLookup(f => f.LessonId);
        var lessonPictures = (await _db.LessonPictures.ToListAsync(ct)).ToLookup(p => p.LessonId);
        var rankPictures = (await _db.TeachersInformationPictures.ToListAsync(ct)).ToLookup(p => p.RankId);

        var nextRankId = await NextIdAsync(_db.Ranks.Select(r => r.Id), ct);
        var nextLessonId = await NextIdAsync(_db.Lessons.Select(l => l.Id), ct);
        var nextFileId = await NextIdAsync(_db.LessonFiles.Select(f => f.Id), ct);
        var nextLessonPictureId = await NextIdAsync(_db.LessonPictures.Select(p => p.Id), ct);
        var nextRankPictureId = await NextIdAsync(_db.TeachersInformationPictures.Select(p => p.Id), ct);
        var nextVersionId = await NextIdAsync(_db.LessonVersions.Select(v => v.Id), ct);
        var nextVersionPictureId = await NextIdAsync(_db.LessonVersionPictures.Select(p => p.Id), ct);

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

            if (planRank.TeachersInformation is { } information)
            {
                var rankId = rank.Id;
                var pictureChanged = SyncPictures(rankPictures[rankId].ToList(), information.Pictures, _db.TeachersInformationPictures,
                    p => p.FileName, p => p.Order, p => p.BlobKey,
                    planned => new TeachersInformationPicture
                    {
                        Id = nextRankPictureId++, RankId = rankId, Order = planned.Order, FileName = planned.FileName, BlobKey = planned.BlobKey
                    });
                if (rank.TeachersInformation != information.Body || pictureChanged)
                {
                    SetValue(rank, r => r.TeachersInformation, information.Body);
                    textsUpdated++;
                }
            }
            else if (rank.TeachersInformation is not null)
            {
                warnings.Add($"Rank '{rank.Code}' has Teacher's Information but no file in '{LessonsFolderName}'; left untouched.");
            }

            foreach (var planLesson in planRank.Lessons)
            {
                if (suggestionOwned.Contains(planLesson.Code))
                {
                    continue;
                }

                var isNew = !lessons.TryGetValue(planLesson.Code, out var lesson);
                var changed = false;
                var textChanged = false;
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

                // Files: an identical sequence leaves the rows (and Ids) alone; the same order and blob keys
                // with different names rename the rows in place (Ids stay stable); otherwise replace the set.
                var existingFiles = files[lesson.Id].OrderBy(f => f.Order).ToList();
                var sameKeys = existingFiles.Count == planLesson.Files.Count
                    && existingFiles.Zip(planLesson.Files).All(p => p.First.Order == p.Second.Order && p.First.BlobKey == p.Second.BlobKey);
                if (sameKeys)
                {
                    foreach (var (existing, planned) in existingFiles.Zip(planLesson.Files).Where(p => p.First.FileName != p.Second.FileName))
                    {
                        SetValue(existing, f => f.FileName, planned.FileName);
                        filesRenamed++;
                        changed = true;
                    }
                }
                else
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

                // Lesson text and pictures (adr/0012).
                if (planLesson.Text is { } text)
                {
                    var lessonId = lesson.Id;
                    var pictureChanged = SyncPictures(lessonPictures[lessonId].ToList(), text.Pictures, _db.LessonPictures,
                        p => p.FileName, p => p.Order, p => p.BlobKey,
                        planned => new LessonPicture
                        {
                            Id = nextLessonPictureId++, LessonId = lessonId, Order = planned.Order, FileName = planned.FileName, BlobKey = planned.BlobKey
                        });
                    // A picture only moved to a new storage key (same content, same version) is not a new lesson version.
                    textChanged = lesson.Body != text.Body || lesson.ContentVersion != text.Version;
                    if (textChanged || pictureChanged)
                    {
                        SetValue(lesson, l => l.Body, text.Body);
                        SetValue(lesson, l => l.ContentVersion, text.Version);
                        textsUpdated++;
                    }
                }
                else if (lesson.Body is not null)
                {
                    warnings.Add($"Lesson '{lesson.Code}' has lesson text but no file in '{LessonsFolderName}'; left untouched.");
                }

                if (changed && !isNew)
                {
                    lessonsUpdated++;
                }

                // Each lesson the seeder writes is recorded as a converted version (adr/0013 decisions 4 and 9).
                if (isNew || changed || textChanged)
                {
                    var versionId = nextVersionId++;
                    var number = (latestKinds.TryGetValue(lesson.Id, out var latest) ? latest.Number : 0) + 1;
                    _db.LessonVersions.Add(new LessonVersion
                    {
                        Id = versionId,
                        LessonId = lesson.Id,
                        Number = number,
                        Kind = LessonVersionKind.Converted,
                        Title = planLesson.Title,
                        Order = planLesson.Order,
                        IsActive = true,
                        Body = planLesson.Text?.Body ?? lesson.Body,
                        ContentVersion = planLesson.Text?.Version ?? lesson.ContentVersion,
                        PublishedAt = DateTime.UtcNow
                    });
                    foreach (var tag in planLesson.Tags)
                    {
                        _db.LessonVersionTags.Add(new LessonVersionTag { LessonVersionId = versionId, Tag = tag });
                    }
                    var versionPictures = planLesson.Text is { } planned
                        ? planned.Pictures.Select(p => (p.Order, p.FileName, p.BlobKey))
                        : lessonPictures[lesson.Id].Select(p => (p.Order, p.FileName, p.BlobKey));
                    foreach (var (order, fileName, blobKey) in versionPictures)
                    {
                        _db.LessonVersionPictures.Add(new LessonVersionPicture
                        {
                            Id = nextVersionPictureId++, LessonVersionId = versionId, Order = order, FileName = fileName, BlobKey = blobKey
                        });
                    }
                }
            }
        }

        var manifestRankCodes = plan.Select(r => r.Code).ToHashSet(StringComparer.Ordinal);
        var manifestLessonCodes = plan.SelectMany(r => r.Lessons).Select(l => l.Code).ToHashSet(StringComparer.Ordinal);
        warnings.AddRange(lessons.Keys.Where(c => !manifestLessonCodes.Contains(c)).Order(StringComparer.Ordinal)
            .Select(c => $"Lesson '{c}' is in the database but not in the manifest; left untouched."));
        warnings.AddRange(ranks.Keys.Where(c => !manifestRankCodes.Contains(c)).Order(StringComparer.Ordinal)
            .Select(c => $"Rank '{c}' is in the database but not in the manifest; left untouched."));

        await _db.SaveChangesAsync(ct);

        return new CurriculumSeedResult(errors, warnings, ranksCreated, ranksUpdated, lessonsCreated, lessonsUpdated, filesUploaded, namesAdded,
            filesRenamed, textsUpdated, picturesUploaded);
    }

    /// <summary>
    /// Brings a set of stored pictures in line with the planned ones, matched on file name so Ids stay stable: a gone
    /// picture is removed, a new one added, a moved or re-keyed one changed in place. Returns whether anything changed.
    /// </summary>
    private bool SyncPictures<TPicture>(List<TPicture> existing, List<PlannedPicture> planned, DbSet<TPicture> set,
        Func<TPicture, string> fileName, Expression<Func<TPicture, int>> order, Expression<Func<TPicture, string>> blobKey,
        Func<PlannedPicture, TPicture> create)
        where TPicture : class
    {
        var changed = false;
        var orderOf = order.Compile();
        var keyOf = blobKey.Compile();
        foreach (var gone in existing.Where(e => planned.All(p => p.FileName != fileName(e))))
        {
            set.Remove(gone);
            changed = true;
        }
        foreach (var picture in planned)
        {
            var match = existing.FirstOrDefault(e => fileName(e) == picture.FileName);
            if (match is null)
            {
                set.Add(create(picture));
                changed = true;
            }
            else if (orderOf(match) != picture.Order || keyOf(match) != picture.BlobKey)
            {
                SetValue(match, order, picture.Order);
                SetValue(match, blobKey, picture.BlobKey);
                changed = true;
            }
        }
        return changed;
    }

    // --- Lesson text (adr/0012) -----------------------------------------------------------------

    /// <summary>
    /// Reads each lesson's and rank's Markdown file, if any, and its pictures (resized here, so an unreadable picture is
    /// a validation error before anything is written). Errors: a picture reference that is not a bare file name, not a
    /// JPEG/PNG, missing, or unreadable; two Teacher's Information files for one rank. Warnings: a Markdown file that
    /// matches no lesson or rank; a picture in a folder that the text never uses.
    /// </summary>
    private static void ReadLessonTexts(List<PlannedRank> plan, string lessonsRoot, List<string> errors, List<string> warnings)
    {
        if (!Directory.Exists(lessonsRoot))
        {
            return;
        }

        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rank in plan)
        {
            var candidates = new[] { $"R{rank.Order}{TeachersInformationSuffix}", $"{rank.Code}{TeachersInformationSuffix}" }
                .Distinct(StringComparer.Ordinal)
                .Where(stem => File.Exists(Path.Combine(lessonsRoot, stem + ".md")))
                .ToList();
            if (candidates.Count > 1)
            {
                errors.Add($"Rank '{rank.Code}': two Teacher's Information files ({string.Join(", ", candidates.Select(c => c + ".md"))}); keep one.");
            }
            else if (candidates.Count == 1)
            {
                used.Add(candidates[0]);
                rank.TeachersInformation = ReadText(lessonsRoot, candidates[0], $"teachers-information/{rank.Code}",
                    $"Rank '{rank.Code}' Teacher's Information", errors, warnings);
            }

            foreach (var lesson in rank.Lessons)
            {
                if (File.Exists(Path.Combine(lessonsRoot, lesson.Code + ".md")))
                {
                    used.Add(lesson.Code);
                    lesson.Text = ReadText(lessonsRoot, lesson.Code, $"lesson-pictures/{lesson.Code}", $"Lesson '{lesson.Code}'", errors, warnings,
                        contentAddressed: true);
                }
            }
        }

        foreach (var stem in Directory.EnumerateFiles(lessonsRoot, "*.md").Select(p => Path.GetFileNameWithoutExtension(p))
                     .Where(stem => !used.Contains(stem)).Order(StringComparer.Ordinal))
        {
            warnings.Add($"'{LessonsFolderName}/{stem}.md' matches no lesson or rank in the manifest; not imported.");
        }
    }

    private static PlannedText? ReadText(string lessonsRoot, string stem, string blobPrefix, string label, List<string> errors, List<string> warnings,
        bool contentAddressed = false)
    {
        var body = File.ReadAllText(Path.Combine(lessonsRoot, stem + ".md")).ReplaceLineEndings("\n");
        var folder = Path.Combine(lessonsRoot, stem);
        var available = Directory.Exists(folder)
            ? Directory.EnumerateFiles(folder).Select(p => Path.GetFileName(p)).Where(n => !IsJunk(n)).ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);

        var pictures = new List<PlannedPicture>();
        var errorCount = errors.Count;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes(body));

        foreach (var reference in LessonMarkdown.PictureReferences(body).Distinct(StringComparer.Ordinal))
        {
            if (!LessonMarkdown.IsBareFileName(reference))
            {
                errors.Add($"{label}: picture '{reference}' must be a file name in '{LessonsFolderName}/{stem}/'.");
                continue;
            }
            if (!LessonPictureResizer.SupportedExtensions.Contains(Path.GetExtension(reference)))
            {
                errors.Add($"{label}: picture '{reference}' is not a JPEG or PNG.");
                continue;
            }
            if (!available.Contains(reference))
            {
                errors.Add($"{label}: picture '{reference}' not found in '{LessonsFolderName}/{stem}/'.");
                continue;
            }

            var original = File.ReadAllBytes(Path.Combine(folder, reference));
            var resized = LessonPictureResizer.Resize(original);
            if (resized is null)
            {
                errors.Add($"{label}: picture '{reference}' could not be read as a picture.");
                continue;
            }

            var originalHash = SHA256.HashData(original);
            hash.AppendData(Encoding.UTF8.GetBytes("\0" + reference + "\0"));
            hash.AppendData(originalHash);
            // A lesson picture's key names its content, so a changed picture never overwrites one an earlier lesson version
            // still shows (adr/0013 decision 4).
            var key = contentAddressed
                ? $"{blobPrefix}/{Convert.ToHexStringLower(originalHash)[..16]}/{reference}"
                : $"{blobPrefix}/{reference}";
            pictures.Add(new PlannedPicture(pictures.Count + 1, reference, key, resized));
        }

        foreach (var unused in available.Where(a => pictures.All(p => p.FileName != a)).Order(StringComparer.Ordinal))
        {
            warnings.Add($"{label}: '{LessonsFolderName}/{stem}/{unused}' is not used in the text; not imported.");
        }

        return errors.Count > errorCount ? null : new PlannedText(body, Convert.ToHexStringLower(hash.GetHashAndReset()), pictures);
    }

    // Entities are init-only, so existing rows are changed through the change tracker.
    private void SetValue<TEntity, TValue>(TEntity entity, Expression<Func<TEntity, TValue>> property, TValue value)
        where TEntity : class
        => _db.Entry(entity).Property(property).CurrentValue = value;

    private static async Task<int> NextIdAsync(IQueryable<int> ids, CancellationToken ct)
        => (await ids.Select(i => (int?)i).MaxAsync(ct) ?? 0) + 1;

    /// <summary>
    /// Reads the file-name list, adds entries for new files, saves it when it gained any, and gives every
    /// planned file its listed clean name (plus its original extension). Returns the entries added.
    /// Writes only the list, never the database or blobs, so it is safe before validation passes elsewhere.
    /// </summary>
    private static int ApplyFileNames(List<PlannedRank> plan, string fileNamesPath, List<string> errors, List<string> warnings)
    {
        var list = LessonFileNames.LoadList(fileNamesPath, errors);
        if (errors.Count > 0)
        {
            return 0;
        }

        var planned = plan.SelectMany(r => r.Lessons).Select(l => l.Files).ToList();
        var added = LessonFileNames.MergeList(list,
            planned.SelectMany(f => f).Select(f => (f.BlobKey, f.FileName)), warnings);
        if (added > 0)
        {
            LessonFileNames.SaveList(fileNamesPath, list);
        }

        foreach (var files in planned)
        {
            for (var i = 0; i < files.Count; i++)
            {
                files[i] = files[i] with { FileName = LessonFileNames.StoredName(list[files[i].BlobKey].Name, files[i].FileName) };
            }

            foreach (var duplicate in files.GroupBy(f => f.FileName, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            {
                warnings.Add($"Two files of one lesson are both named '{duplicate.Key}' ({string.Join(", ", duplicate.Select(f => f.BlobKey))}); correct one in the file-name list.");
            }
        }

        return added;
    }

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

    private sealed record PlannedRank(string Code, string Name, int Order, List<PlannedLesson> Lessons)
    {
        public PlannedText? TeachersInformation { get; set; }
    }

    private sealed record PlannedLesson(string Code, string Title, int Order, HashSet<LessonTag> Tags, List<PlannedFile> Files)
    {
        public PlannedText? Text { get; set; }
    }

    private sealed record PlannedText(string Body, string Version, List<PlannedPicture> Pictures);

    private sealed record PlannedPicture(int Order, string FileName, string BlobKey, byte[] Content);
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
