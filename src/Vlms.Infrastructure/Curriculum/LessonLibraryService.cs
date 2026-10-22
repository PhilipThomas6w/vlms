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
    /// <summary>The roles that may browse and download lesson material (FR-006).</summary>
    public static readonly IReadOnlyList<Role> LibraryRoles =
        [Role.Teacher, Role.Approver, Role.Admin, Role.Student, Role.Parent];

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
                    l.Code,
                    l.Title,
                    l.Tags.Select(t => t.Tag).Order().Select(LessonTagNames.Display).ToList(),
                    l.Files.OrderBy(f => f.Order).ThenBy(f => f.Id)
                        .Select(f => new LibraryFile(f.Id, f.FileName)).ToList()))
                .ToList()))
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

public sealed record LibraryLesson(string Code, string Title, IReadOnlyList<string> Tags, IReadOnlyList<LibraryFile> Files);

public sealed record LibraryFile(int Id, string FileName);

public sealed record LessonFileContent(string FileName, byte[] Content);
