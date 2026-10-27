using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.Logging;
using Vlms.Domain;
using Vlms.Infrastructure.Identity;
using Vlms.Infrastructure.Reporting;

namespace Vlms.Infrastructure.Retention;

/// <summary>The kinds of record the records-due-for-deletion page deletes.</summary>
public enum RetentionRecordKind
{
    StudentApplication,
    DbsCheck,
    Student
}

/// <summary>
/// One deletion asked for on the page: the record, and the person's name as the Admin typed it to
/// confirm (the student's name, the DBS check's teacher, or the application's child).
/// </summary>
public sealed record RetentionDeletionRequest(RetentionRecordKind Kind, int RecordId, string ConfirmedName);

/// <summary>
/// What one deletion did. <see cref="Error"/> is set, and nothing was deleted, when the record was
/// refused (no longer due, not found, or the typed name did not match).
/// </summary>
public sealed record RetentionDeletionResult(
    RetentionRecordKind Kind,
    int RecordId,
    bool Deleted,
    string? Error,
    IReadOnlyList<int> AccountsDeleted,
    IReadOnlyList<int> AccountsKept);

/// <summary>
/// The retention purge's deletions (docs/adr/0014-retention-purge.md decisions 1 to 4): an Admin
/// deletes a record on the records-due-for-deletion page after typing the person's name; nothing is
/// deleted automatically. Every rule is enforced here, not on the page:
///
/// <list type="bullet">
/// <item>The caller is an Admin of their active chapter with two-factor authentication, and the
/// record is in that chapter (adr/0010). The Admin role and the chapter come from
/// <see cref="ICurrentUserContext"/>; the record is found through the chapter and sensitive-data
/// query filters (never past them), and its chapter is checked again explicitly.</item>
/// <item>The record is still due at the moment of deletion: <see cref="RetentionFlagging"/> is run
/// again, so a record whose leaving was undone, or whose dates changed, is refused.</item>
/// <item>The typed name matches the person's name (ignoring case and surrounding spaces).</item>
/// <item>A student's deletion removes the student with their completions, certificates (and the
/// stored PDFs, after the database commit), rank history, badges, attendance marks, consent records
/// (their sensitive details go with them by the database's cascade, so they are not read), guardian
/// links and invites; each guardian left with no child here; and the child's own sign-in account
/// (subject to the account rule below). A DBS check's deletion removes that check, an application's
/// removes it with its restricted details.</item>
/// <item>Account rule (decision 3, Philip 27/10/2026): an account is deleted only when nothing keeps
/// it. It is kept while the person holds a current staff role (Teacher, Admin, Safeguarding Officer
/// and not marked as left), any role in another chapter or a national role, an Approver role, a child
/// still in the programme (a guardian row with a link, or a student record), a DBS check, or a
/// registration application. A staff leaver's account goes only once they left at least 6 years ago
/// (the DBS retention period) and their DBS records are deleted; their staff roles, kept as history,
/// do not count as current. The account is also kept, and reported as kept, while any other kept
/// record still refers to it (for example a lesson they suggested), because deleting it would break
/// that record. The caller's own account is never deleted.</item>
/// <item>Each deleted record, guardian row and account writes a <see cref="DeletionLogEntry"/> in the
/// same transaction: type, id, date, who, chapter; no personal data.</item>
/// </list>
/// </summary>
public sealed class RetentionDeletionService
{
    private readonly VlmsDbContext _db;
    private readonly ICurrentUserContext _currentUser;
    private readonly IBlobStorage _blobStorage;
    private readonly ILogger<RetentionDeletionService> _logger;

    public RetentionDeletionService(
        VlmsDbContext db, ICurrentUserContext currentUser, IBlobStorage blobStorage, ILogger<RetentionDeletionService> logger)
    {
        _db = db;
        _currentUser = currentUser;
        _blobStorage = blobStorage;
        _logger = logger;
    }

    /// <summary>
    /// Deletes one record. Throws <see cref="UnauthorizedAccessException"/> for a caller who may not
    /// delete at all, and <see cref="InvalidOperationException"/> (nothing deleted) when the record is
    /// refused.
    /// </summary>
    public async Task<RetentionDeletionResult> DeleteAsync(RetentionDeletionRequest request, CancellationToken ct = default)
    {
        var (adminId, chapterId) = RequireAdmin();
        ArgumentNullException.ThrowIfNull(request);

        // A long-lived page context may still track rows from an earlier load: start clean so every
        // check below reads the database as it is now.
        _db.ChangeTracker.Clear();

        return request.Kind switch
        {
            RetentionRecordKind.Student => await DeleteStudentAsync(request, adminId, chapterId, ct),
            RetentionRecordKind.DbsCheck => await DeleteDbsCheckAsync(request, adminId, chapterId, ct),
            RetentionRecordKind.StudentApplication => await DeleteApplicationAsync(request, adminId, chapterId, ct),
            _ => throw new ArgumentOutOfRangeException(nameof(request))
        };
    }

    /// <summary>
    /// "Delete all shown": each record is deleted on its own, with every check above, applications
    /// first, then DBS checks, then students (so an application does not keep a parent's account).
    /// A refused record is reported in its result and does not stop the others.
    /// </summary>
    public async Task<IReadOnlyList<RetentionDeletionResult>> DeleteAllAsync(
        IReadOnlyList<RetentionDeletionRequest> requests, CancellationToken ct = default)
    {
        RequireAdmin();
        ArgumentNullException.ThrowIfNull(requests);

        var results = new List<RetentionDeletionResult>();
        foreach (var request in requests.OrderBy(r => r.Kind))
        {
            try
            {
                results.Add(await DeleteAsync(request, ct));
            }
            catch (InvalidOperationException ex)
            {
                results.Add(new RetentionDeletionResult(request.Kind, request.RecordId, false, ex.Message, [], []));
            }
        }

        return results;
    }

    private async Task<RetentionDeletionResult> DeleteStudentAsync(
        RetentionDeletionRequest request, int adminId, int chapterId, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var due = (await RetentionFlagging.GetStudentsDueForDeletionAsync(_db, now, ct))
            .FirstOrDefault(s => s.StudentId == request.RecordId);
        RequireDueInChapter(due is not null && due.ChapterId == chapterId);
        RequireName(request.ConfirmedName, due!.StudentName);

        var student = await _db.Students.SingleAsync(s => s.Id == request.RecordId, ct);
        RequireDueInChapter(student.ChapterId == chapterId);

        var links = await _db.StudentGuardianLinks.Where(l => l.StudentId == student.Id).ToListAsync(ct);
        var guardianIds = links.Select(l => l.ParentGuardianId).Distinct().ToList();
        var completions = await _db.StudentLessonCompletions.Where(c => c.StudentId == student.Id).ToListAsync(ct);
        var completionIds = completions.Select(c => c.Id).ToList();
        var certificates = await _db.Certificates.Where(c => completionIds.Contains(c.StudentLessonCompletionId)).ToListAsync(ct);
        var blobKeys = certificates.Select(c => c.BlobKey).ToList();

        var deletedAccounts = new List<int>();
        var keptAccounts = new List<int>();

        await using (var transaction = await _db.Database.BeginTransactionAsync(ct))
        {
            _db.Certificates.RemoveRange(certificates);
            _db.StudentLessonCompletions.RemoveRange(completions);
            _db.StudentRankProgresses.RemoveRange(await _db.StudentRankProgresses.Where(p => p.StudentId == student.Id).ToListAsync(ct));
            _db.StudentBadges.RemoveRange(await _db.StudentBadges.Where(b => b.StudentId == student.Id).ToListAsync(ct));
            _db.AttendanceMarks.RemoveRange(await _db.AttendanceMarks.Where(m => m.StudentId == student.Id).ToListAsync(ct));
            // The consent records only: their ConsentSensitiveDetails rows are removed by the
            // database's ON DELETE CASCADE, so the deletion never has to read them.
            _db.ConsentRecords.RemoveRange(await _db.ConsentRecords.Where(c => c.StudentId == student.Id).ToListAsync(ct));
            _db.GuardianInvites.RemoveRange(await _db.GuardianInvites.Where(i => i.StudentId == student.Id).ToListAsync(ct));
            _db.StudentGuardianLinks.RemoveRange(links);
            _db.Students.Remove(student);
            await _db.SaveChangesAsync(ct);
            Log(DeletionLogEntry.RecordTypes.Student, student.Id, now, adminId, chapterId);

            // Guardians left with no child: their guardian row goes, and then (rule below) their account.
            var candidateAccounts = new List<int>();
            if (student.AppUserId is int childAccount)
            {
                candidateAccounts.Add(childAccount);
            }

            foreach (var guardianId in guardianIds)
            {
                var guardian = await _db.ParentGuardians.SingleOrDefaultAsync(g => g.Id == guardianId, ct);
                if (guardian is null
                    || await _db.StudentGuardianLinks.AnyAsync(l => l.ParentGuardianId == guardianId, ct)
                    || await IsReferencedAsync(typeof(ParentGuardian), guardianId, ct))
                {
                    continue;
                }

                _db.ParentGuardians.Remove(guardian);
                await _db.SaveChangesAsync(ct);
                Log(DeletionLogEntry.RecordTypes.ParentGuardian, guardianId, now, adminId, chapterId);
                if (guardian.AppUserId is int parentAccount)
                {
                    candidateAccounts.Add(parentAccount);
                }
            }

            foreach (var userId in candidateAccounts.Distinct())
            {
                (await TryDeleteAccountAsync(userId, now, adminId, chapterId, ct) ? deletedAccounts : keptAccounts).Add(userId);
            }

            await _db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }

        // Files cannot join the database transaction, so they go only once it has committed. A file
        // that cannot be deleted is logged (an orphaned PDF is found by its key, which names the student id).
        foreach (var key in blobKeys)
        {
            try
            {
                await _blobStorage.DeleteAsync(key, ct);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                _logger.LogError(ex, "Deleted student {StudentId}, but could not delete certificate file {BlobKey}.", student.Id, key);
            }
        }

        return new RetentionDeletionResult(RetentionRecordKind.Student, student.Id, true, null, deletedAccounts, keptAccounts);
    }

    private async Task<RetentionDeletionResult> DeleteDbsCheckAsync(
        RetentionDeletionRequest request, int adminId, int chapterId, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        // Materializes the chapter's DBS checks through the filters, so the read-audit interceptor
        // logs the read as it does for the report. Done before the transaction opens.
        var due = (await RetentionFlagging.GetDbsChecksDueForDeletionAsync(_db, DateOnly.FromDateTime(now), ct))
            .FirstOrDefault(d => d.DbsCheckId == request.RecordId);
        RequireDueInChapter(due is not null && due.ChapterId == chapterId);
        RequireName(request.ConfirmedName, due!.TeacherName);

        // Already tracked by the read above: no second read of the restricted entity.
        var check = await _db.DbsChecks.FindAsync([request.RecordId], ct)
            ?? throw new InvalidOperationException(NoLongerDue);
        RequireDueInChapter(check.ChapterId == chapterId);

        var deletedAccounts = new List<int>();
        var keptAccounts = new List<int>();
        await using (var transaction = await _db.Database.BeginTransactionAsync(ct))
        {
            _db.DbsChecks.Remove(check);
            await _db.SaveChangesAsync(ct);
            Log(DeletionLogEntry.RecordTypes.DbsCheck, check.Id, now, adminId, chapterId);

            (await TryDeleteAccountAsync(check.TeacherUserId, now, adminId, chapterId, ct) ? deletedAccounts : keptAccounts)
                .Add(check.TeacherUserId);

            await _db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }

        return new RetentionDeletionResult(RetentionRecordKind.DbsCheck, check.Id, true, null, deletedAccounts, keptAccounts);
    }

    private async Task<RetentionDeletionResult> DeleteApplicationAsync(
        RetentionDeletionRequest request, int adminId, int chapterId, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var due = (await RetentionFlagging.GetApplicationsDueForDeletionAsync(_db, now, ct))
            .FirstOrDefault(a => a.ApplicationId == request.RecordId);
        RequireDueInChapter(due is not null && due.ChapterId == chapterId);
        RequireName(request.ConfirmedName, due!.ChildName);

        var application = await _db.StudentApplications.SingleAsync(a => a.Id == request.RecordId, ct);
        RequireDueInChapter(application.ChapterId == chapterId);

        await using (var transaction = await _db.Database.BeginTransactionAsync(ct))
        {
            // Its StudentApplicationSensitiveDetails row is removed by the database's ON DELETE
            // CASCADE, unread. The applicant's account is not considered here: an application is not
            // a child in the programme, and ADR-0014 names no rule for an applicant's account.
            _db.StudentApplications.Remove(application);
            await _db.SaveChangesAsync(ct);
            Log(DeletionLogEntry.RecordTypes.StudentApplication, application.Id, now, adminId, chapterId);
            await _db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }

        return new RetentionDeletionResult(RetentionRecordKind.StudentApplication, application.Id, true, null, [], []);
    }

    /// <summary>
    /// The account rule (this class's doc comment). Deletes the account, its sign-in and (by cascade)
    /// its roles and preferences when nothing keeps it, and returns whether it did.
    /// </summary>
    private async Task<bool> TryDeleteAccountAsync(int userId, DateTime now, int adminId, int chapterId, CancellationToken ct)
    {
        if (userId == adminId)
        {
            return false;
        }

        var user = await _db.AppUsers.SingleOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null)
        {
            return false;
        }

        // UserRole has no query filter: every role the person holds, in every chapter.
        var roles = await _db.UserRoles.AsNoTracking().Where(r => r.UserId == userId).ToListAsync(ct);
        if (roles.Any(r => r.ChapterId != chapterId))
        {
            return false;   // a role in another chapter, or a national role
        }

        if (roles.Any(r => r.Role is Role.Approver or Role.NationalAdministrator))
        {
            return false;
        }

        if (roles.Any(r => LeftStaffPolicy.IsStaffRole(r.Role)))
        {
            // Staff roles count as current unless the person was marked as left; a leaver's account
            // goes only after the DBS retention period from their leaving.
            if (!user.HasLeft || user.LeftAt is not { } leftAt || leftAt > now.AddYears(-RetentionFlagging.DbsCheckRetentionYears))
            {
                return false;
            }
        }

        if (await _db.DbsChecks.AnyAsync(d => d.TeacherUserId == userId, ct)
            || await _db.Students.AnyAsync(s => s.AppUserId == userId, ct)
            || await _db.StudentGuardianLinks.AnyAsync(l => l.ParentGuardian!.AppUserId == userId, ct)
            || await _db.StudentApplications.AnyAsync(a => a.ApplicantUserId == userId, ct))
        {
            return false;
        }

        // Guardian rows with no child left (for example from an earlier deletion) go with the account.
        var orphanGuardians = await _db.ParentGuardians.Where(g => g.AppUserId == userId).ToListAsync(ct);
        foreach (var guardian in orphanGuardians)
        {
            if (await IsReferencedAsync(typeof(ParentGuardian), guardian.Id, ct))
            {
                return false;
            }
        }

        var guardiansToLog = orphanGuardians.Select(g => g.Id).ToList();
        _db.ParentGuardians.RemoveRange(orphanGuardians);
        await _db.SaveChangesAsync(ct);

        if (await IsReferencedAsync(typeof(AppUser), userId, ct))
        {
            // Another kept record names this account (for example a lesson they suggested or a
            // register they took): deleting it would break that record. Kept and reported.
            _logger.LogWarning("Account {UserId} kept: other records still refer to it.", userId);
            foreach (var guardianId in guardiansToLog)
            {
                Log(DeletionLogEntry.RecordTypes.ParentGuardian, guardianId, now, adminId, chapterId);
            }

            return false;
        }

        foreach (var guardianId in guardiansToLog)
        {
            Log(DeletionLogEntry.RecordTypes.ParentGuardian, guardianId, now, adminId, chapterId);
        }

        // Roles, the active-chapter choice, calendar preferences and subscribe links cascade from the
        // account; the sign-in's claims, logins and tokens cascade from the sign-in.
        _db.AppUsers.Remove(user);
        var signIn = await _db.Users.SingleOrDefaultAsync(u => u.Id == userId, ct);
        if (signIn is not null)
        {
            _db.Users.Remove(signIn);
        }

        await _db.SaveChangesAsync(ct);
        Log(DeletionLogEntry.RecordTypes.Account, userId, now, adminId, chapterId);
        return true;
    }

    /// <summary>
    /// Whether any row still refers to <paramref name="principalType"/>'s row <paramref name="id"/>
    /// through a foreign key the database would refuse to delete past (restrict or no action). Read
    /// from the model, so a foreign key added later is covered without changing this class. Each
    /// check is an EXISTS through the query filters; it reads no row.
    /// </summary>
    private async Task<bool> IsReferencedAsync(Type principalType, int id, CancellationToken ct)
    {
        var principal = _db.Model.FindEntityType(principalType)!;
        foreach (var foreignKey in principal.GetReferencingForeignKeys())
        {
            if (foreignKey.DeleteBehavior is DeleteBehavior.Cascade or DeleteBehavior.SetNull)
            {
                continue;
            }

            var property = foreignKey.Properties.Single();
            var method = typeof(RetentionDeletionService)
                .GetMethod(nameof(AnyReferenceAsync), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .MakeGenericMethod(foreignKey.DeclaringEntityType.ClrType);
            if (await (Task<bool>)method.Invoke(this, [property, id, ct])!)
            {
                return true;
            }
        }

        return false;
    }

    private Task<bool> AnyReferenceAsync<T>(IProperty property, int id, CancellationToken ct) where T : class
    {
        var row = Expression.Parameter(typeof(T), "row");
        var value = Expression.Call(typeof(EF), nameof(EF.Property), [property.ClrType], row, Expression.Constant(property.Name));
        var predicate = Expression.Lambda<Func<T, bool>>(
            Expression.Equal(value, Expression.Constant(id, property.ClrType)), row);
        return _db.Set<T>().AnyAsync(predicate, ct);
    }

    private void Log(string recordType, int recordId, DateTime now, int adminId, int chapterId) =>
        _db.DeletionLog.Add(new DeletionLogEntry
        {
            RecordType = recordType,
            RecordId = recordId,
            DeletedAt = now,
            DeletedByUserId = adminId,
            ChapterId = chapterId
        });

    private const string NoLongerDue = "This record is no longer due for deletion. Reload the page to see the current list.";

    private static void RequireDueInChapter(bool due)
    {
        if (!due)
        {
            throw new InvalidOperationException(NoLongerDue);
        }
    }

    private static void RequireName(string? typed, string expected)
    {
        if (!NameMatches(typed, expected))
        {
            throw new InvalidOperationException("The name you typed does not match. Nothing was deleted.");
        }
    }

    /// <summary>The confirmation rule the page also uses: the full name, ignoring case and surrounding spaces.</summary>
    public static bool NameMatches(string? typed, string expected) =>
        typed is not null && string.Equals(typed.Trim(), expected.Trim(), StringComparison.OrdinalIgnoreCase);

    private (int AdminId, int ChapterId) RequireAdmin()
    {
        if (!_currentUser.HasRole(Role.Admin) || !_currentUser.HasMfaEnabled
            || _currentUser.UserId is not int adminId || _currentUser.ActiveChapterId is not int chapterId)
        {
            throw new UnauthorizedAccessException(
                "Only an Admin with two-factor authentication can delete records, in the chapter they are working in.");
        }

        return (adminId, chapterId);
    }
}
