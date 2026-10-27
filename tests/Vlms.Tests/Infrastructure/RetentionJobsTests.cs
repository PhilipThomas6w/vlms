using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Vlms.Domain;
using Vlms.Infrastructure;
using Vlms.Infrastructure.Retention;
using Vlms.Infrastructure.Security;
using Xunit;

namespace Vlms.Tests.Infrastructure;

/// <summary>
/// The two monthly retention jobs (docs/adr/0014-retention-purge.md decisions 5 and 6): the reminder
/// to each chapter's Admins while records are due, and the audit-log purge of rows older than 6 years.
/// </summary>
public sealed class RetentionJobsTests : IDisposable
{
    private const int Coventry = 2;
    private readonly SqliteConnection _anchorConnection;
    private readonly string _connectionString;
    private readonly DateTime _now = DateTime.UtcNow;

    public RetentionJobsTests()
    {
        _connectionString = $"Data Source=file:vlms-retention-jobs-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        _anchorConnection = new SqliteConnection(_connectionString);
        _anchorConnection.Open();

        using var db = NewContext(TestSeedUserContext.Instance);
        db.Database.EnsureCreated();
        db.Chapters.Add(new Chapter { Id = Coventry, Name = "Coventry", Place = "Coventry", CreatedAt = _now, IsActive = true });
        db.Ranks.Add(new Rank { Id = 1, Order = 1, Code = "R1", Name = "Recruit" });

        AddUser(db, 1, "Shrewsbury Admin", Role.Admin, Chapter.ShrewsburyId);
        AddUser(db, 2, "Second Shrewsbury Admin", Role.Admin, Chapter.ShrewsburyId);
        AddUser(db, 3, "Shrewsbury Safeguarding", Role.SafeguardingOfficer, Chapter.ShrewsburyId);
        AddUser(db, 4, "Former Admin", Role.Admin, Chapter.ShrewsburyId, hasLeft: true);
        AddUser(db, 5, "Coventry Admin", Role.Admin, Coventry);

        // Shrewsbury: two students past retention. Coventry: none.
        db.Students.Add(NewStudent(100, "Alex Old", _now.AddYears(-4), Chapter.ShrewsburyId));
        db.Students.Add(NewStudent(101, "Blake Old", _now.AddYears(-5), Chapter.ShrewsburyId));
        db.Students.Add(NewStudent(200, "Coventry Recent", _now.AddYears(-1), Coventry));
        db.SaveChanges();
    }

    public void Dispose() => _anchorConnection.Dispose();

    // ---- the reminder --------------------------------------------------------------------------

    [Fact]
    public async Task TheReminder_GoesToTheChaptersCurrentAdmins_WithCountsOnly()
    {
        var notifications = new FakeNotificationService();
        using var db = NewContext(SystemCurrentUserContext.Instance);

        await new RetentionReminderService(db, SystemCurrentUserContext.Instance, notifications, "https://example.test/").SendAsync();

        Assert.Equal(["user1@example.com", "user2@example.com"], notifications.SentRequests.Select(r => r.RecipientEmail));
        Assert.All(notifications.SentRequests, r =>
        {
            Assert.Equal(RetentionReminderService.Subject, r.Subject);
            Assert.StartsWith("2 records are due for deletion.", r.Body);
            Assert.Contains("https://example.test/reporting/records-due-for-deletion", r.Body);
            Assert.DoesNotContain("Alex", r.Body);
            Assert.DoesNotContain("Blake", r.Body);
        });
    }

    [Fact]
    public async Task TheReminder_SendsNothing_WhenNothingIsDue()
    {
        using (var db = NewContext(TestSeedUserContext.Instance))
        {
            db.Students.RemoveRange(db.Students.Where(s => s.Id == 100 || s.Id == 101));
            db.SaveChanges();
        }

        var notifications = new FakeNotificationService();
        using var context = NewContext(SystemCurrentUserContext.Instance);
        await new RetentionReminderService(context, SystemCurrentUserContext.Instance, notifications).SendAsync();

        Assert.Empty(notifications.SentRequests);
    }

    [Fact]
    public async Task TheReminder_IsSentByTheScheduledJobOnly()
    {
        var admin = new FakeCurrentUserContext(1, Role.Admin);
        using var db = NewContext(admin);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            new RetentionReminderService(db, admin, new FakeNotificationService()).SendAsync());
    }

    // ---- the audit-log purge -------------------------------------------------------------------

    [Fact]
    public async Task ThePurge_DeletesOnlyRowsOlderThanSixYears()
    {
        using (var db = NewContext(TestSeedUserContext.Instance))
        {
            db.SensitiveDataAccessLogs.AddRange(
                Log(_now.AddYears(-7)),
                Log(_now.AddYears(-6).AddDays(-1)),
                Log(_now.AddYears(-6).AddDays(1)),
                Log(_now.AddYears(-1)));
            db.SaveChanges();
        }

        using var purgeDb = NewContext(SystemCurrentUserContext.Instance);
        var purged = await new AuditLogPurgeService(purgeDb, SystemCurrentUserContext.Instance).PurgeAsync(_now);

        Assert.Equal(2, purged);
        using var check = NewContext(TestSeedUserContext.Instance);
        Assert.All(check.SensitiveDataAccessLogs.ToList(), l => Assert.True(l.AccessedAt > _now.AddYears(-6)));
        Assert.Equal(2, check.SensitiveDataAccessLogs.Count());
    }

    [Fact]
    public async Task ThePurge_TouchesNoOtherTable()
    {
        using (var db = NewContext(TestSeedUserContext.Instance))
        {
            db.SensitiveDataAccessLogs.Add(Log(_now.AddYears(-7)));
            db.DeletionLog.Add(new DeletionLogEntry { RecordType = "Student", RecordId = 1, DeletedAt = _now.AddYears(-8), DeletedByUserId = 1, ChapterId = 1 });
            db.SaveChanges();
        }

        using var purgeDb = NewContext(SystemCurrentUserContext.Instance);
        await new AuditLogPurgeService(purgeDb, SystemCurrentUserContext.Instance).PurgeAsync(_now);

        using var check = NewContext(TestSeedUserContext.Instance);
        Assert.Equal(1, check.DeletionLog.Count());
        Assert.Equal(3, check.Students.Count());
    }

    [Fact]
    public async Task ThePurge_IsRunByTheScheduledJobOnly()
    {
        var admin = new FakeCurrentUserContext(1, Role.Admin) { SeesAllChapters = true };
        using var db = NewContext(admin);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new AuditLogPurgeService(db, admin).PurgeAsync(_now));
    }

    [Fact]
    public async Task ThePurge_RefusesALocalTime()
    {
        using var db = NewContext(SystemCurrentUserContext.Instance);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            new AuditLogPurgeService(db, SystemCurrentUserContext.Instance).PurgeAsync(DateTime.Now));
    }

    // ---- helpers -------------------------------------------------------------------------------

    private static SensitiveDataAccessLog Log(DateTime at) => new()
    {
        Id = 0, UserId = 1, Entity = nameof(DbsCheck), EntityId = 1, AccessedAt = at, AccessType = SensitiveAccessType.View
    };

    private VlmsDbContext NewContext(ICurrentUserContext currentUser) =>
        new(new DbContextOptionsBuilder<VlmsDbContext>().UseSqlite(_connectionString).Options, currentUser);

    private static Student NewStudent(int id, string name, DateTime leftAt, int chapterId) => new()
    {
        ChapterId = chapterId,
        Id = id,
        Name = name,
        DateOfBirth = new DateOnly(2010, 1, 1),
        CurrentRankId = 1,
        Status = StudentStatus.Inactive,
        EnrolmentDate = new DateOnly(2018, 1, 1),
        LeftAt = leftAt
    };

    private static void AddUser(VlmsDbContext db, int id, string name, Role role, int chapterId, bool hasLeft = false)
    {
        db.AppUsers.Add(new AppUser { Id = id, DisplayName = name, Email = $"user{id}@example.com", HasLeft = hasLeft, LeftAt = hasLeft ? DateTime.UtcNow.AddYears(-1) : null });
        db.UserRoles.Add(new UserRole { UserId = id, Role = role, ChapterId = chapterId });
    }
}
