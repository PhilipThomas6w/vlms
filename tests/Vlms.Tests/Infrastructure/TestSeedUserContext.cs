using Vlms.Domain;

namespace Vlms.Tests.Infrastructure;

/// <summary>
/// The <see cref="ICurrentUserContext"/> test fixtures seed and inspect data with. Before chapters
/// (docs/adr/0010-chapters.md) fixtures used <c>NullCurrentUserContext</c> or
/// <c>SystemCurrentUserContext</c>; neither has an active chapter, and <c>VlmsDbContext</c> now
/// refuses a new top-level chapter record (a student, a guardian, a DBS check) or a chapter role
/// without one. This one sees every chapter, as the system principal does, and files new records
/// under Shrewsbury unless a test sets a chapter on the row itself. It holds no role, so it reads
/// none of the sensitive entities. Test code only; never a runtime context.
/// </summary>
public sealed class TestSeedUserContext : ICurrentUserContext
{
    public static readonly TestSeedUserContext Instance = new();

    public int? UserId => null;
    public bool HasRole(Role role) => false;
    public bool HasMfaEnabled => false;
    public int? ActiveChapterId => Chapter.ShrewsburyId;
    public bool SeesAllChapters => true;
}
