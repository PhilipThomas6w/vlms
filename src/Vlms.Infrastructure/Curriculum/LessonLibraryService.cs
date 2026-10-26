using Microsoft.EntityFrameworkCore;
using Vlms.Domain;

namespace Vlms.Infrastructure.Curriculum;

/// <summary>
/// Read side of the lesson library (docs/requirements/functional.md FR-006): every active lesson in
/// every rank, in delivery order, with its tags and files, and the bytes of a lesson file by its
/// <see cref="LessonFile.Id"/>. Open to Teacher, Approver, Admin, Student and Parent; refused to
/// everyone else (a Safeguarding Officer who holds none of those roles included).
///
/// Authorization: does its own role check (<see cref="RequireLibraryRole"/>) rather than relying
/// on the caller having passed the "RequireLessonLibrary" policy — the same defence-in-depth as
/// <see cref="LessonProposalService"/>. Files are looked up by id and served only for active
/// lessons; the blob key never comes from the caller. Lessons carry no sensitive-data query filter.
/// </summary>
public sealed class LessonLibraryService
{
    /// <summary>
    /// The roles that may browse and download lesson material (FR-006). The curriculum is national,
    /// so the National Administrator (who manages the rank badges) reads it too (docs/adr/0010-chapters.md).
    /// </summary>
    public static readonly IReadOnlyList<Role> LibraryRoles =
        [Role.Teacher, Role.Approver, Role.Admin, Role.Student, Role.Parent, Role.NationalAdministrator];

    private readonly VlmsDbContext _db;
    private readonly IBlobStorage _blobStorage;
    private readonly ICurrentUserContext _currentUser;

    public LessonLibraryService(VlmsDbContext db, IBlobStorage blobStorage, ICurrentUserContext currentUser)
    {
        _db = db;
        _blobStorage = blobStorage;
        _currentUser = currentUser;
    }

    /// <summary>
    /// The roles that see a rank's Teacher's Information (docs/adr/0012-app-hosted-lessons.md decision 5): Teachers and
    /// Admins, and the National Administrator who looks after the national curriculum. Never Students or Parents, nor an
    /// Approver who is not also one of these.
    /// </summary>
    public static readonly IReadOnlyList<Role> TeachersInformationRoles = [Role.Teacher, Role.Admin, Role.NationalAdministrator];

    /// <summary>Every rank in <see cref="Rank.Order"/>, each with its active lessons in <see cref="Lesson.Order"/>.</summary>
    public async Task<IReadOnlyList<LibraryRank>> GetLibraryAsync(CancellationToken ct = default)
    {
        RequireLibraryRole();

        var ranks = await _db.Ranks.AsNoTracking().OrderBy(r => r.Order).Select(r => new { r.Id, r.Code, r.Name }).ToListAsync(ct);
        // Projected, so the lesson text itself is not read for the list.
        var lessons = await _db.Lessons.AsNoTracking()
            .Where(l => l.IsActive)
            .Select(l => new
            {
                l.Id,
                l.RankId,
                l.Code,
                l.Title,
                l.Order,
                l.ContentVersion,
                HasPage = l.Body != null,
                Tags = l.Tags.Select(t => t.Tag).ToList(),
                Files = l.Files.Select(f => new { f.Id, f.Order, f.FileName }).ToList(),
                Pictures = l.Pictures.Select(p => new { p.Id, p.Order }).ToList()
            })
            .ToListAsync(ct);

        return ranks.Select(rank => new LibraryRank(
            rank.Code,
            rank.Name,
            lessons.Where(l => l.RankId == rank.Id)
                .OrderBy(l => l.Order).ThenBy(l => l.Id)
                .Select(l => new LibraryLesson(
                    l.Id,
                    l.Code,
                    l.Title,
                    l.Tags.Order().Select(LessonTagNames.Display).ToList(),
                    l.Files.OrderBy(f => f.Order).ThenBy(f => f.Id)
                        .Select(f => new LibraryFile(f.Id, f.FileName)).ToList())
                {
                    HasPage = l.HasPage,
                    ContentVersion = l.HasPage ? l.ContentVersion : null,
                    PictureIds = l.HasPage ? l.Pictures.OrderBy(p => p.Order).ThenBy(p => p.Id).Select(p => p.Id).ToList() : []
                })
                .ToList()))
            .ToList();
    }

    /// <summary>
    /// A converted lesson's page (adr/0012 decision 6): its title, rank and text as HTML (<see cref="LessonMarkdown"/>),
    /// pictures pointing at <c>/lessons/pictures/{id}</c>. Null when the code is unknown, the lesson is retired or it has
    /// no text yet. The rank's Teacher's Information comes with it only for <see cref="TeachersInformationRoles"/>:
    /// enforced here, not left to the page.
    /// </summary>
    public async Task<LessonPage?> GetLessonPageAsync(string code, CancellationToken ct = default)
    {
        RequireLibraryRole();

        var lesson = await _db.Lessons.AsNoTracking()
            .Where(l => l.Code == code && l.IsActive && l.Body != null)
            .Select(l => new { l.Id, l.Code, l.Title, l.Body, l.ContentVersion, l.RankId, l.Order, RankCode = l.Rank!.Code, RankName = l.Rank.Name, RankOrder = l.Rank.Order })
            .SingleOrDefaultAsync(ct);
        if (lesson is null)
        {
            return null;
        }

        // Its place in the library's delivery order, as the saved-lessons index sorts by (FR-023).
        var rankPosition = await _db.Ranks.CountAsync(r => r.Order < lesson.RankOrder, ct);
        var position = await _db.Lessons.CountAsync(l => l.RankId == lesson.RankId && l.IsActive
            && (l.Order < lesson.Order || (l.Order == lesson.Order && l.Id < lesson.Id)), ct);

        var pictures = await _db.LessonPictures.AsNoTracking().Where(p => p.LessonId == lesson.Id)
            .OrderBy(p => p.Order).ThenBy(p => p.Id).Select(p => new { p.Id, p.FileName }).ToListAsync(ct);
        var pictureIds = pictures.ToDictionary(p => p.FileName, p => p.Id, StringComparer.Ordinal);
        var html = LessonMarkdown.ToHtml(lesson.Body!,
            name => pictureIds.TryGetValue(name, out var id) ? PictureUrl(id) : null);

        string? information = null;
        if (TeachersInformationRoles.Any(_currentUser.HasRole))
        {
            var body = await _db.Ranks.AsNoTracking().Where(r => r.Id == lesson.RankId).Select(r => r.TeachersInformation).SingleAsync(ct);
            if (body is not null)
            {
                var informationPictures = await _db.TeachersInformationPictures.AsNoTracking().Where(p => p.RankId == lesson.RankId)
                    .Select(p => new { p.Id, p.FileName }).ToDictionaryAsync(p => p.FileName, p => p.Id, ct);
                information = LessonMarkdown.ToHtml(body,
                    name => informationPictures.TryGetValue(name, out var id) ? TeachersInformationPictureUrl(id) : null);
            }
        }

        return new LessonPage(lesson.Id, lesson.Code, lesson.Title, $"{lesson.RankCode}: {lesson.RankName}", html,
            lesson.ContentVersion, pictures.Select(p => p.Id).ToList(), information)
        {
            RankPosition = rankPosition,
            Position = position
        };
    }

    /// <summary>Where a lesson picture is served (by id only).</summary>
    public static string PictureUrl(int id) => $"/lessons/pictures/{id}";

    /// <summary>Where a Teacher's Information picture is served (by id only, staff only).</summary>
    public static string TeachersInformationPictureUrl(int id) => $"/lessons/teachers-information/pictures/{id}";

    /// <summary>
    /// A converted lesson's page (adr/0012 decision 6). It is plain server-rendered HTML needing no live connection, so
    /// it is also what "Save to this device" saves (decision 7, as amended by Philip, 26/10/2026).
    /// </summary>
    public static string PageUrl(string code) => $"/lessons/{Uri.EscapeDataString(code)}";

    /// <summary>
    /// A lesson picture's content, or null if the id is unknown, its lesson is retired or the blob is missing. Same role
    /// check as the rest of the library.
    /// </summary>
    public async Task<LessonFileContent?> OpenPictureAsync(int pictureId, CancellationToken ct = default)
    {
        RequireLibraryRole();

        var picture = await _db.LessonPictures.AsNoTracking()
            .Where(p => p.Id == pictureId && p.Lesson!.IsActive)
            .Select(p => new { p.FileName, p.BlobKey })
            .SingleOrDefaultAsync(ct);
        if (picture is null)
        {
            return null;
        }

        var content = await _blobStorage.ReadAsync(picture.BlobKey, ct);
        return content is null ? null : new LessonFileContent(picture.FileName, content);
    }

    /// <summary>
    /// A Teacher's Information picture's content, or null if unknown or missing. Throws
    /// <see cref="UnauthorizedAccessException"/> for anyone outside <see cref="TeachersInformationRoles"/>.
    /// </summary>
    public async Task<LessonFileContent?> OpenTeachersInformationPictureAsync(int pictureId, CancellationToken ct = default)
    {
        if (!TeachersInformationRoles.Any(_currentUser.HasRole))
        {
            throw new UnauthorizedAccessException("Caller does not hold a role that may read Teacher's Information.");
        }

        var picture = await _db.TeachersInformationPictures.AsNoTracking()
            .Where(p => p.Id == pictureId)
            .Select(p => new { p.FileName, p.BlobKey })
            .SingleOrDefaultAsync(ct);
        if (picture is null)
        {
            return null;
        }

        var content = await _blobStorage.ReadAsync(picture.BlobKey, ct);
        return content is null ? null : new LessonFileContent(picture.FileName, content);
    }

    /// <summary>
    /// The current state of the given lessons, for a device that has saved them for offline use
    /// (FR-023): each one that is still an active lesson, with its rank, position and files. Ids that
    /// are unknown or retired are simply absent, which tells the device to drop them. Nothing beyond
    /// what <see cref="GetLibraryAsync"/> already shows. Same role check as the rest of the library. A converted lesson
    /// (adr/0012 decision 7) is saved as its lesson page and pictures, not its files: <see cref="SavedLesson.Page"/>,
    /// <see cref="SavedLesson.Version"/> (compared by the device, so a changed lesson is fetched again) and
    /// <see cref="SavedLesson.PictureIds"/>, with no files.
    /// </summary>
    public async Task<IReadOnlyList<SavedLesson>> GetSavedLessonsAsync(IReadOnlyCollection<int> lessonIds, CancellationToken ct = default)
    {
        RequireLibraryRole();

        var library = await GetLibraryAsync(ct);
        return library
            .SelectMany((rank, rankPosition) => rank.Lessons.Select((lesson, position) =>
                new SavedLesson(lesson.Id, lesson.Code, lesson.Title, $"{rank.Code}: {rank.Name}", rankPosition, position,
                    lesson.HasPage ? [] : lesson.Files)
                {
                    Page = lesson.HasPage ? PageUrl(lesson.Code) : null,
                    Version = lesson.ContentVersion,
                    PictureIds = lesson.PictureIds
                }))
            .Where(l => lessonIds.Contains(l.Id))
            .ToList();
    }

    /// <summary>
    /// The content of a lesson file, or null if the id is unknown, the lesson is inactive or the
    /// blob is missing. Throws <see cref="UnauthorizedAccessException"/> for a caller outside
    /// <see cref="LibraryRoles"/>.
    /// </summary>
    public async Task<LessonFileContent?> OpenFileAsync(int lessonFileId, CancellationToken ct = default)
    {
        RequireLibraryRole();

        var file = await _db.LessonFiles.AsNoTracking()
            .Where(f => f.Id == lessonFileId && f.Lesson!.IsActive)
            .SingleOrDefaultAsync(ct);
        if (file is null)
        {
            return null;
        }

        var content = await _blobStorage.ReadAsync(file.BlobKey, ct);
        return content is null ? null : new LessonFileContent(file.FileName, content);
    }

    private void RequireLibraryRole()
    {
        if (!LibraryRoles.Any(_currentUser.HasRole))
        {
            throw new UnauthorizedAccessException("Caller does not hold a role that may use the lesson library.");
        }
    }
}

public sealed record LibraryRank(string Code, string Name, IReadOnlyList<LibraryLesson> Lessons);

public sealed record LibraryLesson(int Id, string Code, string Title, IReadOnlyList<string> Tags, IReadOnlyList<LibraryFile> Files)
{
    /// <summary>Whether the lesson has been converted to lesson text (adr/0012), so has a page at <c>/lessons/{code}</c>.</summary>
    public bool HasPage { get; init; }

    /// <summary><see cref="Lesson.ContentVersion"/> of a converted lesson; null otherwise.</summary>
    public string? ContentVersion { get; init; }

    /// <summary>The lesson's pictures, in order (converted lessons only).</summary>
    public IReadOnlyList<int> PictureIds { get; init; } = [];
}

/// <summary>
/// A converted lesson's page: <see cref="Html"/> is the rendered lesson text; <see cref="TeachersInformationHtml"/> is the
/// rank's Teacher's Information, present only for staff (<see cref="LessonLibraryService.TeachersInformationRoles"/>).
/// </summary>
public sealed record LessonPage(int Id, string Code, string Title, string Rank, string Html, string? ContentVersion,
    IReadOnlyList<int> PictureIds, string? TeachersInformationHtml)
{
    /// <summary>The rank's position in the library (0-based), for the saved-lessons index.</summary>
    public int RankPosition { get; init; }

    /// <summary>The lesson's position within its rank (0-based), for the saved-lessons index.</summary>
    public int Position { get; init; }
}

/// <summary>A lesson as a device that saved it needs to know it: <c>Rank</c> reads like "R1: First", the two positions give the delivery order.</summary>
public sealed record SavedLesson(int Id, string Code, string Title, string Rank, int RankPosition, int Position, IReadOnlyList<LibraryFile> Files)
{
    /// <summary>The lesson page to save for a converted lesson (<c>/lessons/{code}</c>); null for one that is still files.</summary>
    public string? Page { get; init; }

    /// <summary>The converted lesson's content version; a device whose copy differs fetches the page and pictures again.</summary>
    public string? Version { get; init; }

    /// <summary>The converted lesson's pictures, served at <c>/lessons/pictures/{id}</c>.</summary>
    public IReadOnlyList<int> PictureIds { get; init; } = [];
}

public sealed record LibraryFile(int Id, string FileName)
{
    /// <summary>The name shown to people: the stored name without its extension (FR-006).</summary>
    public string DisplayName => LessonFileNames.DisplayName(FileName);
}

public sealed record LessonFileContent(string FileName, byte[] Content);
