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
/// <see cref="Warnings"/> never block a run: they report lessons/ranks in the database that the
/// manifest no longer mentions (left untouched, as lessons may have completions).
/// <see cref="LessonTextsUpdated"/> counts lessons whose text (lesson body or Teacher's Information) or
/// pictures changed; <see cref="PicturesUploaded"/> counts resized pictures written to storage.
/// <see cref="HandoutsUploaded"/> counts handouts written to storage (docs/adr/0016); <see cref="FilesRemoved"/> counts a
/// lesson's current files removed because they are not in its <c>handouts/</c> folder, and <see cref="BlobsDeleted"/> the
/// stored files then deleted (those no version, suggestion or other lesson still refers to).
/// </summary>
public sealed record CurriculumSeedResult(
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings,
    int RanksCreated,
    int RanksUpdated,
    int LessonsCreated,
    int LessonsUpdated,
    int HandoutsUploaded,
    int LessonTextsUpdated = 0,
    int PicturesUploaded = 0,
    int FilesRemoved = 0,
    int BlobsDeleted = 0)
{
    public bool Succeeded => Errors.Count == 0;
}

/// <summary>
/// Idempotent seeder for the rank ladder and curriculum (STATE.md "Seed rank ladder + curriculum";
/// docs/requirements/functional.md "Curriculum management"), driven by the cleansed manifest
/// <c>seed/curriculum.json</c> plus a content root holding the lesson folders (not in git). Wrapped
/// by the <c>seed-curriculum</c> command of the <c>Vlms.Jobs</c> host; the logic lives here so it is
/// testable. Since docs/adr/0016 everything it reads is in git (<c>seed/</c>): the original rank folders are no longer
/// read, and the manifest's <c>source</c> fields play no part.
///
/// <b>Properties every change must keep:</b>
/// <list type="bullet">
/// <item><b>Validate first, write nothing on error.</b> Every tag must map to a <see cref="LessonTag"/>, codes must be
/// unique, every picture and handout must be readable and of an allowed type. All errors are collected, not just the first.</item>
/// <item><b>Idempotent.</b> Ranks and lessons are matched on <c>Code</c>; rows that already match are
/// untouched, so a re-run changes nothing and Ids and blob keys are stable.</item>
/// <item><b>Never deletes a lesson.</b> Lessons absent from the manifest are reported as warnings only.</item>
/// <item><b>Application-assigned Ids</b> (max + 1), as elsewhere in the codebase.</item>
/// </list>
///
/// <b>Handouts</b> (docs/adr/0016): a lesson's handouts are the files in <c>lessons/{lesson code}/handouts/</c> (none
/// when there is no folder), in name order, each a PDF, Word document (.docx), JPEG or PNG checked by content, at most
/// 20 MB (<see cref="LessonHandoutFiles"/>). The display name is the file name without its extension. Each is stored under a
/// key naming its content (<c>lesson-handouts/{code}/{hash}{extension}</c>), so it is never overwritten. Existing rows are
/// matched on that key, then on content, then on file name, so a remaining handout keeps its id (saved copies on devices
/// refer to it). <b>Pruning:</b> for a lesson the seeder still owns, a current file not in <c>handouts/</c> is removed from
/// the lesson, and its stored file deleted unless a lesson version, a suggestion or another lesson still refers to it;
/// the run reports how many (<see cref="CurriculumSeedResult.FilesRemoved"/>). With handouts, a converted lesson's content
/// version covers them too.
///
/// <b>App-hosted lessons</b> (docs/adr/0012-app-hosted-lessons.md): the folder <c>lessons</c> under the content root
/// (<c>seed/lessons</c>, in git) holds the converted lessons: <c>{lesson code}.md</c> is the lesson text, its pictures in
/// the folder <c>{lesson code}/</c>, referred to by bare file name. Every picture
/// the text refers to must exist and be a JPEG or PNG (an error otherwise); each is stored resized
/// (<see cref="LessonPictureResizer"/>) as a <see cref="LessonPicture"/>, matched on file name so Ids stay stable. The lesson's <see cref="Lesson.ContentVersion"/> is a hash of its text and
/// its original pictures, so it changes exactly when either does, and a lesson's pictures are uploaded only when it
/// changes or a picture is missing from storage. A lesson with text in the database but no file keeps it
/// (a warning): nothing is cleared by a missing file.
///
/// <b>Lesson versions</b> (docs/adr/0013-lesson-suggestions-review-and-history.md decisions 4 and 9): each lesson the
/// seeder creates or changes is recorded as its next <see cref="LessonVersion"/> of kind
/// <see cref="LessonVersionKind.Converted"/>. A lesson whose latest version did not come from the seeder (it was changed
/// through lesson suggestions) is never written again: it is skipped, with a warning, handouts and pictures included. Lesson
/// pictures are stored under a key naming their content (<c>lesson-pictures/{code}/{hash}/{name}</c>), so a changed
/// picture never overwrites one an earlier version shows.
///
/// <b>Teacher's Information</b> (docs/adr/0015 and its amendment): a lesson's Teacher's Information comes from
/// <c>{lesson code}.teachers-information.md</c> (none when there is no file), pictures in the lesson's folder under the
/// same rules; it is part of the lesson's content version and its versions, and a picture only it uses is marked
/// <see cref="LessonPicture.TeachersInformationOnly"/>. There is no rank-wide Teacher's Information: a leftover
/// <c>{lesson code}.notes.md</c> or <c>R{n}-TI.md</c> is a validation error, so a stale file is never silently ignored.
///
/// Running it: <c>Vlms.Jobs seed-curriculum --manifest seed/curriculum.json --content-root seed</c>.
///
/// Uploads happen before the single <c>SaveChanges</c>: a failed upload leaves the database untouched,
/// and re-running simply overwrites the same keys. Stored files are deleted only after it succeeded.
/// </summary>
public sealed class CurriculumSeeder
{
    /// <summary>Name of a lesson's handouts folder: <c>lessons/{lesson code}/handouts/</c> (adr/0016).</summary>
    public const string HandoutsFolderName = "handouts";

    /// <summary>Folder under the content root holding the converted lessons (adr/0012).</summary>
    public const string LessonsFolderName = "lessons";

    /// <summary>Suffix of a lesson's Teacher's Information file: <c>R5-01.teachers-information.md</c> (adr/0015 amendment).</summary>
    public const string TeachersInformationSuffix = ".teachers-information";

    // The files an earlier design read; a leftover one is an error now (adr/0015 amendment).
    private const string LegacyNotesSuffix = ".notes";
    private const string LegacyRankInformationSuffix = "-TI";

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
        var warnings = new List<string>();
        // Lesson text and handouts are read even when the manifest has errors, so one run reports every problem.
        ReadLessonTexts(plan, Path.Combine(contentRoot, LessonsFolderName), errors, warnings);
        ReadHandouts(plan, Path.Combine(contentRoot, LessonsFolderName), errors, warnings);
        if (errors.Count > 0)
        {
            return Failed(errors);
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

        // Handouts (content-addressed keys, so one already stored is the same file) come before any database write.
        var handoutsUploaded = 0;
        foreach (var handout in plan.SelectMany(r => r.Lessons).Where(l => !suggestionOwned.Contains(l.Code)).SelectMany(l => l.Handouts))
        {
            ct.ThrowIfCancellationRequested();
            if (await _blobStorage.ExistsAsync(handout.BlobKey, ct))
            {
                continue;
            }
            await _blobStorage.UploadAsync(handout.BlobKey, handout.Content, ct);
            handoutsUploaded++;
        }

        // Pictures: a lesson's only when its text or pictures changed (or one is missing from storage). Still before any
        // database write.
        var picturesUploaded = 0;
        var storedVersions = await _db.Lessons.AsNoTracking().ToDictionaryAsync(l => l.Code, l => l.ContentVersion, ct);
        var texts = plan.SelectMany(r => r.Lessons).Where(l => l.Text is not null && !suggestionOwned.Contains(l.Code))
            .Select(l => (Text: l.Text!, Unchanged: storedVersions.GetValueOrDefault(l.Code) == l.Text!.Version));
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
        var filesRemoved = 0;
        var textsUpdated = 0;
        // Stored files a lesson no longer uses; deleted after the save, if nothing else refers to them.
        var releasedKeys = new HashSet<string>(StringComparer.Ordinal);

        var ranks = await _db.Ranks.ToDictionaryAsync(r => r.Code, ct);
        var lessons = await _db.Lessons.ToDictionaryAsync(l => l.Code, ct);
        var tags = (await _db.LessonTagAssignments.ToListAsync(ct)).ToLookup(t => t.LessonId);
        var files = (await _db.LessonFiles.ToListAsync(ct)).ToLookup(f => f.LessonId);
        var lessonPictures = (await _db.LessonPictures.ToListAsync(ct)).ToLookup(p => p.LessonId);

        var nextRankId = await NextIdAsync(_db.Ranks.Select(r => r.Id), ct);
        var nextLessonId = await NextIdAsync(_db.Lessons.Select(l => l.Id), ct);
        var nextFileId = await NextIdAsync(_db.LessonFiles.Select(f => f.Id), ct);
        var nextLessonPictureId = await NextIdAsync(_db.LessonPictures.Select(p => p.Id), ct);
        var nextVersionId = await NextIdAsync(_db.LessonVersions.Select(v => v.Id), ct);
        var nextVersionPictureId = await NextIdAsync(_db.LessonVersionPictures.Select(p => p.Id), ct);
        var nextVersionHandoutId = await NextIdAsync(_db.LessonVersionHandouts.Select(h => h.Id), ct);

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

                // Handouts (adr/0016): matched on key, then content, then file name, so a remaining handout keeps its id;
                // a current file not in handouts/ is removed (pruned).
                var existingFiles = files[lesson.Id].OrderBy(f => f.Order).ThenBy(f => f.Id).ToList();
                var matches = await MatchHandoutsAsync(existingFiles, planLesson.Handouts, ct);
                foreach (var gone in existingFiles.Where(f => !matches.ContainsKey(f)))
                {
                    _db.LessonFiles.Remove(gone);
                    releasedKeys.Add(gone.BlobKey);
                    filesRemoved++;
                    changed = true;
                }
                foreach (var planned in planLesson.Handouts)
                {
                    var existing = matches.FirstOrDefault(m => m.Value == planned).Key;
                    if (existing is null)
                    {
                        _db.LessonFiles.Add(new LessonFile
                        {
                            Id = nextFileId++, LessonId = lesson.Id, Order = planned.Order, FileName = planned.FileName,
                            DisplayName = planned.DisplayName, BlobKey = planned.BlobKey
                        });
                        changed = true;
                    }
                    else if (existing.Order != planned.Order || existing.FileName != planned.FileName
                        || existing.DisplayName != planned.DisplayName || existing.BlobKey != planned.BlobKey)
                    {
                        if (existing.BlobKey != planned.BlobKey)
                        {
                            releasedKeys.Add(existing.BlobKey);
                        }
                        SetValue(existing, f => f.Order, planned.Order);
                        SetValue(existing, f => f.FileName, planned.FileName);
                        SetValue(existing, f => f.DisplayName, planned.DisplayName);
                        SetValue(existing, f => f.BlobKey, planned.BlobKey);
                        changed = true;
                    }
                }

                // Lesson text and pictures (adr/0012).
                if (planLesson.Text is { } text)
                {
                    var lessonId = lesson.Id;
                    var existingPictures = lessonPictures[lessonId].ToList();
                    var pictureChanged = SyncPictures(existingPictures, text.Pictures, _db.LessonPictures,
                        p => p.FileName, p => p.Order, p => p.BlobKey,
                        planned => new LessonPicture
                        {
                            Id = nextLessonPictureId++, LessonId = lessonId, Order = planned.Order, FileName = planned.FileName, BlobKey = planned.BlobKey,
                            TeachersInformationOnly = planned.InformationOnly
                        });
                    // A picture only the Teacher's Information use is served to staff only (adr/0015).
                    foreach (var existing in existingPictures)
                    {
                        var plannedPicture = text.Pictures.FirstOrDefault(p => p.FileName == existing.FileName);
                        if (plannedPicture is not null && existing.TeachersInformationOnly != plannedPicture.InformationOnly)
                        {
                            SetValue(existing, p => p.TeachersInformationOnly, plannedPicture.InformationOnly);
                            pictureChanged = true;
                        }
                    }
                    // A picture only moved to a new storage key (same content, same version) is not a new lesson version.
                    textChanged = lesson.Body != text.Body || lesson.TeachersInformation != text.TeachersInformation || lesson.ContentVersion != text.Version;
                    if (textChanged || pictureChanged)
                    {
                        SetValue(lesson, l => l.Body, text.Body);
                        SetValue(lesson, l => l.TeachersInformation, text.TeachersInformation);
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
                        TeachersInformation = planLesson.Text is { } plannedText ? plannedText.TeachersInformation : lesson.TeachersInformation,
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
                    foreach (var handout in planLesson.Handouts)
                    {
                        _db.LessonVersionHandouts.Add(new LessonVersionHandout
                        {
                            Id = nextVersionHandoutId++, LessonVersionId = versionId, Order = handout.Order, FileName = handout.FileName,
                            DisplayName = handout.DisplayName, BlobKey = handout.BlobKey
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

        // Stored files no longer used by any lesson, version or suggestion are deleted, now the database no longer needs them.
        var blobsDeleted = 0;
        if (releasedKeys.Count > 0)
        {
            var keys = releasedKeys.ToList();
            var stillUsed = (await _db.LessonFiles.AsNoTracking().Where(f => keys.Contains(f.BlobKey)).Select(f => f.BlobKey).ToListAsync(ct))
                .Concat(await _db.LessonVersionHandouts.AsNoTracking().Where(h => keys.Contains(h.BlobKey)).Select(h => h.BlobKey).ToListAsync(ct))
                .Concat(await _db.LessonSuggestionHandouts.AsNoTracking().Where(h => keys.Contains(h.BlobKey)).Select(h => h.BlobKey).ToListAsync(ct))
                .ToHashSet(StringComparer.Ordinal);
            foreach (var key in keys.Where(k => !stillUsed.Contains(k)).Order(StringComparer.Ordinal))
            {
                await _blobStorage.DeleteAsync(key, ct);
                blobsDeleted++;
            }
        }

        return new CurriculumSeedResult(errors, warnings, ranksCreated, ranksUpdated, lessonsCreated, lessonsUpdated, handoutsUploaded,
            textsUpdated, picturesUploaded, filesRemoved, blobsDeleted);
    }

    /// <summary>
    /// Pairs a lesson's current files with its planned handouts: first on the same blob key, then on the same content (the
    /// stored file is read), then on the same file name ignoring case. Each is paired at most once.
    /// </summary>
    private async Task<Dictionary<LessonFile, PlannedHandout>> MatchHandoutsAsync(List<LessonFile> existing, List<PlannedHandout> planned, CancellationToken ct)
    {
        var matches = new Dictionary<LessonFile, PlannedHandout>();
        void Pair(Func<LessonFile, PlannedHandout, bool> same)
        {
            foreach (var handout in planned.Where(p => !matches.ContainsValue(p)))
            {
                var file = existing.FirstOrDefault(f => !matches.ContainsKey(f) && same(f, handout));
                if (file is not null)
                {
                    matches[file] = handout;
                }
            }
        }

        Pair((f, p) => f.BlobKey == p.BlobKey);
        if (matches.Count < Math.Min(existing.Count, planned.Count))
        {
            var hashes = new Dictionary<LessonFile, string?>();
            foreach (var file in existing.Where(f => !matches.ContainsKey(f)))
            {
                var content = await _blobStorage.ReadAsync(file.BlobKey, ct);
                hashes[file] = content is null ? null : Convert.ToHexStringLower(SHA256.HashData(content));
            }
            Pair((f, p) => hashes.TryGetValue(f, out var hash) && hash == p.Hash);
            Pair((f, p) => string.Equals(f.FileName, p.FileName, StringComparison.OrdinalIgnoreCase));
        }
        return matches;
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
    /// Reads each lesson's Markdown file, if any, and its pictures (resized here, so an unreadable picture is
    /// a validation error before anything is written). Errors: a picture reference that is not a bare file name, not a
    /// JPEG/PNG, missing, or unreadable; a leftover <c>.notes.md</c> or <c>-TI.md</c> file. Warnings: a Markdown file that
    /// matches no lesson; a picture in a folder that the text never uses.
    /// </summary>
    private static void ReadLessonTexts(List<PlannedRank> plan, string lessonsRoot, List<string> errors, List<string> warnings)
    {
        if (!Directory.Exists(lessonsRoot))
        {
            return;
        }

        // The files an earlier design read are errors now, so a stale one cannot be silently ignored (adr/0015 amendment).
        foreach (var stem in Directory.EnumerateFiles(lessonsRoot, "*.md").Select(p => Path.GetFileNameWithoutExtension(p)).Order(StringComparer.Ordinal))
        {
            if (stem.EndsWith(LegacyNotesSuffix, StringComparison.Ordinal))
            {
                errors.Add($"'{LessonsFolderName}/{stem}.md' is no longer read: a lesson's Teacher's Information is '{stem[..^LegacyNotesSuffix.Length]}{TeachersInformationSuffix}.md'. Rename or remove it.");
            }
            else if (stem.EndsWith(LegacyRankInformationSuffix, StringComparison.Ordinal))
            {
                errors.Add($"'{LessonsFolderName}/{stem}.md' is no longer read: there is no rank-wide Teacher's Information; move each section into its lesson's '<lesson code>{TeachersInformationSuffix}.md'. Remove it.");
            }
        }

        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rank in plan)
        {
            foreach (var lesson in rank.Lessons)
            {
                var hasInformation = File.Exists(Path.Combine(lessonsRoot, lesson.Code + TeachersInformationSuffix + ".md"));
                if (hasInformation)
                {
                    used.Add(lesson.Code + TeachersInformationSuffix);
                }
                if (File.Exists(Path.Combine(lessonsRoot, lesson.Code + ".md")))
                {
                    used.Add(lesson.Code);
                    lesson.Text = ReadText(lessonsRoot, lesson.Code, $"lesson-pictures/{lesson.Code}", $"Lesson '{lesson.Code}'", errors, warnings,
                        contentAddressed: true, withInformation: hasInformation);
                }
                else if (hasInformation)
                {
                    warnings.Add($"Lesson '{lesson.Code}': '{LessonsFolderName}/{lesson.Code}{TeachersInformationSuffix}.md' has no lesson text '{LessonsFolderName}/{lesson.Code}.md'; not imported.");
                }
            }
        }

        foreach (var stem in Directory.EnumerateFiles(lessonsRoot, "*.md").Select(p => Path.GetFileNameWithoutExtension(p))
                     .Where(stem => !used.Contains(stem)).Order(StringComparer.Ordinal))
        {
            warnings.Add($"'{LessonsFolderName}/{stem}.md' matches no lesson in the manifest; not imported.");
        }
    }

    /// <summary>
    /// Reads <c>{stem}.md</c> and, with <paramref name="withInformation"/>, the lesson's Teacher's Information <c>{stem}.teachers-information.md</c>
    /// (adr/0015 amendment), whose pictures share the folder <c>{stem}/</c> and the same rules. The version hash covers the
    /// Teacher's Information and its pictures only when there is such a file, so a lesson without one keeps the hash it always had.
    /// </summary>
    private static PlannedText? ReadText(string lessonsRoot, string stem, string blobPrefix, string label, List<string> errors, List<string> warnings,
        bool contentAddressed = false, bool withInformation = false)
    {
        var body = File.ReadAllText(Path.Combine(lessonsRoot, stem + ".md")).ReplaceLineEndings("\n");
        var information = withInformation ? File.ReadAllText(Path.Combine(lessonsRoot, stem + TeachersInformationSuffix + ".md")).ReplaceLineEndings("\n") : null;
        var folder = Path.Combine(lessonsRoot, stem);
        var available = Directory.Exists(folder)
            ? Directory.EnumerateFiles(folder).Select(p => Path.GetFileName(p)).Where(n => !IsJunk(n)).ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);

        var pictures = new List<PlannedPicture>();
        var errorCount = errors.Count;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes(body));

        void ReadPictures(string markdown, string what, bool informationOnly)
        {
            foreach (var reference in LessonMarkdown.PictureReferences(markdown).Distinct(StringComparer.Ordinal))
            {
                if (pictures.Any(p => p.FileName == reference))
                {
                    continue; // already read for the lesson text
                }
                if (!LessonMarkdown.IsBareFileName(reference))
                {
                    errors.Add($"{label}{what}: picture '{reference}' must be a file name in '{LessonsFolderName}/{stem}/'.");
                    continue;
                }
                if (!LessonPictureResizer.SupportedExtensions.Contains(Path.GetExtension(reference)))
                {
                    errors.Add($"{label}{what}: picture '{reference}' is not a JPEG or PNG.");
                    continue;
                }
                if (!available.Contains(reference))
                {
                    errors.Add($"{label}{what}: picture '{reference}' not found in '{LessonsFolderName}/{stem}/'.");
                    continue;
                }

                var original = File.ReadAllBytes(Path.Combine(folder, reference));
                var resized = LessonPictureResizer.Resize(original);
                if (resized is null)
                {
                    errors.Add($"{label}{what}: picture '{reference}' could not be read as a picture.");
                    continue;
                }

                var originalHash = SHA256.HashData(original);
                hash.AppendData(Encoding.UTF8.GetBytes("\0" + reference + "\0"));
                hash.AppendData(originalHash);
                // A lesson picture's key names its content, so a changed picture never overwrites one an earlier lesson
                // version still shows (adr/0013 decision 4).
                var key = contentAddressed
                    ? $"{blobPrefix}/{Convert.ToHexStringLower(originalHash)[..16]}/{reference}"
                    : $"{blobPrefix}/{reference}";
                pictures.Add(new PlannedPicture(pictures.Count + 1, reference, key, resized, informationOnly));
            }
        }

        ReadPictures(body, "", informationOnly: false);
        if (information is not null)
        {
            hash.AppendData(Encoding.UTF8.GetBytes("\0Teacher's Information\0" + information));
            ReadPictures(information, " Teacher's Information", informationOnly: true);
        }

        foreach (var unused in available.Where(a => pictures.All(p => p.FileName != a)).Order(StringComparer.Ordinal))
        {
            warnings.Add($"{label}: '{LessonsFolderName}/{stem}/{unused}' is not used in the text; not imported.");
        }

        return errors.Count > errorCount
            ? null
            : new PlannedText(body, Convert.ToHexStringLower(hash.GetHashAndReset()), pictures) { TeachersInformation = InformationOrNull(information) };
    }

    private static string? InformationOrNull(string? information) => string.IsNullOrWhiteSpace(information) ? null : information;

    // Entities are init-only, so existing rows are changed through the change tracker.
    private void SetValue<TEntity, TValue>(TEntity entity, Expression<Func<TEntity, TValue>> property, TValue value)
        where TEntity : class
        => _db.Entry(entity).Property(property).CurrentValue = value;

    private static async Task<int> NextIdAsync(IQueryable<int> ids, CancellationToken ct)
        => (await ids.Select(i => (int?)i).MaxAsync(ct) ?? 0) + 1;

    // --- Handouts (adr/0016) -------------------------------------------------------------------

    /// <summary>
    /// Reads each lesson's <c>handouts/</c> folder. Errors: a file that is not a PDF, Word document (.docx), JPEG or PNG by its
    /// content and extension, one over the size limit, two with the same content. Warnings: a folder inside it (not read), a
    /// handouts folder of no lesson in the manifest. With handouts, a converted lesson's content version covers them too
    /// (a lesson without any keeps the version it always had).
    /// </summary>
    private static void ReadHandouts(List<PlannedRank> plan, string lessonsRoot, List<string> errors, List<string> warnings)
    {
        if (!Directory.Exists(lessonsRoot))
        {
            return;
        }

        var codes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var lesson in plan.SelectMany(r => r.Lessons))
        {
            codes.Add(lesson.Code);
            var folder = Path.Combine(lessonsRoot, lesson.Code, HandoutsFolderName);
            if (!Directory.Exists(folder))
            {
                continue;
            }

            var where = $"{LessonsFolderName}/{lesson.Code}/{HandoutsFolderName}";
            foreach (var inner in Directory.EnumerateDirectories(folder).Order(StringComparer.Ordinal))
            {
                warnings.Add($"Lesson '{lesson.Code}': '{where}/{Path.GetFileName(inner)}/' is a folder; handouts are only the files directly in '{where}/'. Not read.");
            }
            foreach (var path in Directory.EnumerateFiles(folder).Where(p => !IsJunk(p)).OrderBy(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase))
            {
                var name = Path.GetFileName(path);
                if (new FileInfo(path).Length > LessonHandoutFiles.MaxBytes)
                {
                    errors.Add($"Lesson '{lesson.Code}': handout '{where}/{name}' is larger than {LessonHandoutFiles.MaxBytes / (1024 * 1024)} MB.");
                    continue;
                }
                var content = File.ReadAllBytes(path);
                string extension;
                try
                {
                    extension = LessonHandoutFiles.CheckedExtension(name, content);
                }
                catch (InvalidOperationException)
                {
                    errors.Add($"Lesson '{lesson.Code}': handout '{where}/{name}' is not a PDF, Word document (.docx), JPEG or PNG.");
                    continue;
                }
                var hash = Convert.ToHexStringLower(SHA256.HashData(content));
                if (lesson.Handouts.FirstOrDefault(h => h.Hash == hash) is { } same)
                {
                    errors.Add($"Lesson '{lesson.Code}': handouts '{where}/{same.SourceName}' and '{where}/{name}' are the same file.");
                    continue;
                }
                lesson.Handouts.Add(new PlannedHandout(lesson.Handouts.Count + 1, LessonHandoutFiles.SafeFileName(name, extension),
                    LessonHandoutFiles.DefaultDisplayName(name), LessonHandoutFiles.BlobKey(lesson.Code, content, extension), content, hash, name));
            }

            if (lesson.Text is { } text && lesson.Handouts.Count > 0)
            {
                using var combined = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                combined.AppendData(Encoding.UTF8.GetBytes(text.Version));
                foreach (var handout in lesson.Handouts)
                {
                    combined.AppendData(Encoding.UTF8.GetBytes($"\0handout\0{handout.DisplayName}\0{handout.FileName}\0{handout.BlobKey}"));
                }
                lesson.Text = text with { Version = Convert.ToHexStringLower(combined.GetHashAndReset()) };
            }
        }

        foreach (var folder in Directory.EnumerateDirectories(lessonsRoot).Order(StringComparer.Ordinal))
        {
            var code = Path.GetFileName(folder);
            if (!codes.Contains(code) && Directory.Exists(Path.Combine(folder, HandoutsFolderName)))
            {
                warnings.Add($"'{LessonsFolderName}/{code}/{HandoutsFolderName}/' matches no lesson in the manifest; not imported.");
            }
        }
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

                plannedLessons.Add(new PlannedLesson(lesson.Code, lesson.Title, lesson.Order, lessonTags));
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

    private static bool IsJunk(string path)
    {
        var name = Path.GetFileName(path);
        return name.StartsWith("~$", StringComparison.Ordinal)
            || name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Thumbs.db", StringComparison.OrdinalIgnoreCase)
            || name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase);
    }

    private sealed record PlannedRank(string Code, string Name, int Order, List<PlannedLesson> Lessons);

    private sealed record PlannedLesson(string Code, string Title, int Order, HashSet<LessonTag> Tags)
    {
        public PlannedText? Text { get; set; }

        /// <summary>The files of <c>lessons/{code}/handouts/</c>, in order (adr/0016).</summary>
        public List<PlannedHandout> Handouts { get; } = [];
    }

    private sealed record PlannedText(string Body, string Version, List<PlannedPicture> Pictures)
    {
        /// <summary>The lesson's Teacher's Information (<c>{code}.teachers-information.md</c>, adr/0015); null when there is no file.</summary>
        public string? TeachersInformation { get; init; }
    }

    /// <param name="InformationOnly">Only the Teacher's Information use it (adr/0015), so it is served to staff only.</param>
    private sealed record PlannedPicture(int Order, string FileName, string BlobKey, byte[] Content, bool InformationOnly = false);
    /// <param name="SourceName">The name of the file in the handouts folder, for messages.</param>
    private sealed record PlannedHandout(int Order, string FileName, string DisplayName, string BlobKey, byte[] Content, string Hash, string SourceName);

    private sealed class ManifestDto
    {
        public List<RankDto>? Ranks { get; set; }
    }

    private sealed class RankDto
    {
        public int Order { get; set; }
        public string? Code { get; set; }
        public string? Name { get; set; }
        public List<LessonDto>? Lessons { get; set; }
    }

    private sealed class LessonDto
    {
        public int Order { get; set; }
        public string? Code { get; set; }
        public string? Title { get; set; }
        public List<string>? Tags { get; set; }
    }
}
