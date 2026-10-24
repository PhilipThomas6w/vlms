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

    /// <summary>Every rank in <see cref="Rank.Order"/>, each with its active lessons in <see cref="Lesson.Order"/>.</summary>
    public async Task<IReadOnlyList<LibraryRank>> GetLibraryAsync(CancellationToken ct = default)
    {
        RequireLibraryRole();

        var ranks = await _db.Ranks.AsNoTracking().OrderBy(r => r.Order).ToListAsync(ct);
        var lessons = await _db.Lessons.AsNoTracking()
            .Where(l => l.IsActive)
            .Include(l => l.Tags)
            .Include(l => l.Files)
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
                    l.Tags.Select(t => t.Tag).Order().Select(LessonTagNames.Display).ToList(),
                    l.Files.OrderBy(f => f.Order).ThenBy(f => f.Id)
                        .Select(f => new LibraryFile(f.Id, f.FileName)).ToList()))
                .ToList()))
            .ToList();
    }

    /// <summary>
    /// The current state of the given lessons, for a device that has saved them for offline use
    /// (FR-023): each one that is still an active lesson, with its rank, position and files. Ids that
    /// are unknown or retired are simply absent, which tells the device to drop them. Nothing beyond
    /// what <see cref="GetLibraryAsync"/> already shows. Same role check as the rest of the library.
    /// </summary>
    public async Task<IReadOnlyList<SavedLesson>> GetSavedLessonsAsync(IReadOnlyCollection<int> lessonIds, CancellationToken ct = default)
    {
        RequireLibraryRole();

        var library = await GetLibraryAsync(ct);
        return library
            .SelectMany((rank, rankPosition) => rank.Lessons.Select((lesson, position) =>
                new SavedLesson(lesson.Id, lesson.Code, lesson.Title, $"{rank.Code}: {rank.Name}", rankPosition, position, lesson.Files)))
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

public sealed record LibraryLesson(int Id, string Code, string Title, IReadOnlyList<string> Tags, IReadOnlyList<LibraryFile> Files);

/// <summary>A lesson as a device that saved it needs to know it: <c>Rank</c> reads like "R1: First", the two positions give the delivery order.</summary>
public sealed record SavedLesson(int Id, string Code, string Title, string Rank, int RankPosition, int Position, IReadOnlyList<LibraryFile> Files);

public sealed record LibraryFile(int Id, string FileName);

public sealed record LessonFileContent(string FileName, byte[] Content);
