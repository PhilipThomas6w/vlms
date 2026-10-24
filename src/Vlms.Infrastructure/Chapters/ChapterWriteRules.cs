using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Vlms.Domain;

namespace Vlms.Infrastructure.Chapters;

/// <summary>
/// The write side of chapter separation (docs/adr/0010-chapters.md), run by
/// <see cref="VlmsDbContext"/>'s <c>SaveChanges</c> before anything is written. The query filters
/// stop a caller reading another chapter's rows; these rules stop a write putting a row into the
/// wrong chapter:
/// <list type="number">
/// <item>A new top-level record (<see cref="Student"/>, <see cref="ParentGuardian"/>,
/// <see cref="DbsCheck"/>) goes into the caller's active chapter; left at 0 its chapter is set to
/// it, and any other chapter is refused. A <see cref="StudentApplication"/> is the exception: the
/// applicant chooses the chapter (StudentApplicationService checks it is an open one).</item>
/// <item>A new child record (rank progress, completion, certificate, badge, guardian link, consent
/// record and details, guardian invite, application details) takes its parent row's chapter; left
/// at 0 it is copied, and a different value is refused. The parent is looked up through the query
/// filters, so a parent the caller cannot see (another chapter's student) is refused too. A guardian
/// link, a guardian invite and a consent record also need their guardian in the same chapter.</item>
/// <item>No existing record changes chapter. A <c>SetValues</c> copy that leaves ChapterId at 0 keeps
/// the original value.</item>
/// <item>A new chapter role (<see cref="RoleScope.IsChapterRole"/>) carries a chapter, the caller's
/// active chapter when left null; a national role carries none.</item>
/// </list>
/// The scheduled job's system principal (<see cref="ICurrentUserContext.SeesAllChapters"/>) has no
/// active chapter, so it can add a top-level record only when the record names its chapter (the
/// job itself adds none); its child records take their parent's chapter like anyone's.
/// </summary>
internal static class ChapterWriteRules
{
    public static void Apply(VlmsDbContext db, ICurrentUserContext currentUser)
    {
        var entries = db.ChangeTracker.Entries()
            .Where(e => e.State is EntityState.Added or EntityState.Modified)
            .ToList();

        foreach (var entry in entries.Where(e => e.State == EntityState.Modified && e.Entity is IChapterOwned))
        {
            KeepChapter(entry);
        }

        var added = entries.Where(e => e.State == EntityState.Added).ToList();

        // Parents before children, so a parent added in the same save is stamped first.
        foreach (var entry in added.Where(e => e.Entity is Student or ParentGuardian or DbsCheck))
        {
            StampTopLevel(entry, currentUser);
        }

        foreach (var entry in added.Where(e => e.Entity is StudentApplication))
        {
            if (Chapter(entry) == 0)
            {
                throw new InvalidOperationException("A registration application must name the chapter it is for.");
            }
        }

        foreach (var entry in added.Where(e => e.Entity is IChapterOwned && e.Entity is not (Student or ParentGuardian or DbsCheck or StudentApplication or Certificate or ConsentSensitiveDetails)))
        {
            StampChild(db, entry);
        }

        foreach (var entry in added.Where(e => e.Entity is Certificate or ConsentSensitiveDetails))
        {
            StampChild(db, entry);
        }

        foreach (var entry in added.Where(e => e.Entity is UserRole))
        {
            StampRole(entry, currentUser);
        }
    }

    private static void KeepChapter(EntityEntry entry)
    {
        var property = entry.Property(nameof(IChapterOwned.ChapterId));
        if (!property.IsModified)
        {
            return;
        }

        if ((int)property.CurrentValue! == 0)
        {
            property.CurrentValue = property.OriginalValue;
            property.IsModified = false;
            return;
        }

        if (!Equals(property.CurrentValue, property.OriginalValue))
        {
            throw new InvalidOperationException($"A {entry.Metadata.ClrType.Name} cannot be moved to another chapter.");
        }
    }

    private static void StampTopLevel(EntityEntry entry, ICurrentUserContext currentUser)
    {
        var chapterId = Chapter(entry);
        var active = currentUser.ActiveChapterId;
        if (chapterId == 0)
        {
            SetChapter(entry, active
                ?? throw new InvalidOperationException(
                    $"A new {entry.Metadata.ClrType.Name} needs a chapter, and the caller is not working in one."));
            return;
        }

        // The system principal sees every chapter and may file a row in a named one (it has no
        // active chapter to default to); everyone else writes only in the chapter they work in.
        if (chapterId != active && !currentUser.SeesAllChapters)
        {
            throw new InvalidOperationException(
                $"A new {entry.Metadata.ClrType.Name} can only be added to the chapter the caller is working in.");
        }
    }

    private static void StampChild(VlmsDbContext db, EntityEntry entry)
    {
        var (parentChapter, guardianChapter) = entry.Entity switch
        {
            StudentGuardianLink l => (StudentChapter(db, l.StudentId), (int?)GuardianChapter(db, l.ParentGuardianId)),
            GuardianInvite i => (StudentChapter(db, i.StudentId), GuardianChapter(db, i.ParentGuardianId)),
            ConsentRecord c => (StudentChapter(db, c.StudentId), GuardianChapter(db, c.SubmittedByParentId)),
            StudentLessonCompletion c => (StudentChapter(db, c.StudentId), null),
            StudentRankProgress p => (StudentChapter(db, p.StudentId), null),
            StudentBadge b => (StudentChapter(db, b.StudentId), null),
            Certificate c => (ChapterOf<StudentLessonCompletion>(db, c.StudentLessonCompletionId), null),
            ConsentSensitiveDetails d => (ChapterOf<ConsentRecord>(db, d.ConsentRecordId), null),
            StudentApplicationSensitiveDetails d => (ChapterOf<StudentApplication>(db, d.StudentApplicationId), null),
            _ => throw new InvalidOperationException($"{entry.Metadata.ClrType.Name} has no chapter rule.")
        };

        var parent = parentChapter
            ?? throw new InvalidOperationException(
                $"A new {entry.Metadata.ClrType.Name} refers to a record that does not exist in the caller's chapter.");

        if (guardianChapter is int guardian && guardian != parent)
        {
            throw new InvalidOperationException(
                $"A new {entry.Metadata.ClrType.Name} links a student and a guardian who are not in the same chapter.");
        }

        var chapterId = Chapter(entry);
        if (chapterId == 0)
        {
            SetChapter(entry, parent);
        }
        else if (chapterId != parent)
        {
            throw new InvalidOperationException(
                $"A new {entry.Metadata.ClrType.Name} must be in the same chapter as the record it belongs to.");
        }
    }

    private static void StampRole(EntityEntry entry, ICurrentUserContext currentUser)
    {
        var role = (UserRole)entry.Entity;
        var chapterProperty = entry.Property(nameof(UserRole.ChapterId));
        if (role.Role.IsNationalRole())
        {
            if (chapterProperty.CurrentValue is not null)
            {
                throw new InvalidOperationException($"{role.Role} is a national role and is not held in a chapter.");
            }

            return;
        }

        if (chapterProperty.CurrentValue is null)
        {
            chapterProperty.CurrentValue = currentUser.ActiveChapterId
                ?? throw new InvalidOperationException($"{role.Role} is held in a chapter, and none was given.");
        }
    }

    // A guardian the caller cannot see (or that does not exist) counts as a mismatch: -1 is no chapter.
    private static int GuardianChapter(VlmsDbContext db, int guardianId) => ChapterOf<ParentGuardian>(db, guardianId) ?? -1;

    private static int? StudentChapter(VlmsDbContext db, int studentId) => ChapterOf<Student>(db, studentId);

    /// <summary>
    /// The chapter of the <typeparamref name="T"/> with this id: from the change tracker if it is
    /// being added or already loaded, otherwise from the database through the query filters (so a
    /// row the caller may not see counts as missing). A scalar projection, so nothing is
    /// materialized and nothing is read-audited.
    /// </summary>
    private static int? ChapterOf<T>(VlmsDbContext db, int id) where T : class, IChapterOwned
    {
        var tracked = db.ChangeTracker.Entries<T>()
            .FirstOrDefault(e => e.State != EntityState.Deleted && (int)e.Property("Id").CurrentValue! == id);
        if (tracked is not null)
        {
            return tracked.Entity.ChapterId;
        }

        return db.Set<T>().AsNoTracking()
            .Where(x => EF.Property<int>(x, "Id") == id)
            .Select(x => (int?)EF.Property<int>(x, nameof(IChapterOwned.ChapterId)))
            .FirstOrDefault();
    }

    private static int Chapter(EntityEntry entry) => (int)entry.Property(nameof(IChapterOwned.ChapterId)).CurrentValue!;

    private static void SetChapter(EntityEntry entry, int chapterId) =>
        entry.Property(nameof(IChapterOwned.ChapterId)).CurrentValue = chapterId;
}
