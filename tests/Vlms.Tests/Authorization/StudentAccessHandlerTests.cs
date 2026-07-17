using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Vlms.Domain;
using Vlms.Infrastructure;
using Vlms.Infrastructure.Authorization;
using Vlms.Infrastructure.Security;
using Vlms.Tests.Infrastructure;
using Xunit;

namespace Vlms.Tests.Authorization;

/// <summary>
/// Verifies the resource-based <see cref="StudentAccessRequirement"/> handlers
/// (`docs/design/low-level-design.md` "Authorization model"): a Parent is authorized only for a
/// <see cref="Student"/> reachable via their <see cref="ParentGuardian.AppUserId"/> -&gt;
/// <see cref="StudentGuardianLink"/>; a Student only for their own row; a Teacher for any row.
///
/// Each handler is exercised directly against a real <see cref="AuthorizationHandlerContext"/> —
/// the standard way to unit test an <see cref="IAuthorizationHandler"/> — rather than through the
/// full <c>IAuthorizationService</c>, so a "deny" case can be observed for a single handler in
/// isolation even though, in the composed policy, another handler might still succeed the same
/// requirement for a different reason.
///
/// Uses the same named, shared-cache SQLite in-memory pattern as
/// <see cref="SensitiveDataAccessControlTests"/> for the Parent handler, which needs real
/// <see cref="StudentGuardianLink"/>/<see cref="ParentGuardian"/> data to join against.
/// </summary>
public sealed class StudentAccessHandlerTests : IDisposable
{
    private readonly SqliteConnection _anchorConnection;
    private readonly DbContextOptions<VlmsDbContext> _options;

    public StudentAccessHandlerTests()
    {
        var connectionString = $"Data Source=file:vlms-authz-test-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        _anchorConnection = new SqliteConnection(connectionString);
        _anchorConnection.Open();
        _options = new DbContextOptionsBuilder<VlmsDbContext>().UseSqlite(connectionString).Options;

        using var context = CreateContext();
        context.Database.EnsureCreated();

        context.Ranks.Add(new Rank { Id = 1, Order = 1, Code = "R1", Name = "Recruit" });
        context.AppUsers.Add(new AppUser { Id = 1, EntraObjectId = "parent-linked", DisplayName = "Linked Parent", Email = "p1@example.com" });
        context.AppUsers.Add(new AppUser { Id = 2, EntraObjectId = "parent-unlinked", DisplayName = "Unlinked Parent", Email = "p2@example.com" });
        context.AppUsers.Add(new AppUser { Id = 3, EntraObjectId = "student-1", DisplayName = "Student One", Email = "s1@example.com" });
        context.ParentGuardians.Add(new ParentGuardian { Id = 1, Name = "Linked Parent", ContactInfo = "p1@example.com", IsPrimary = true, AppUserId = 1 });
        context.ParentGuardians.Add(new ParentGuardian { Id = 2, Name = "Unlinked Parent", ContactInfo = "p2@example.com", IsPrimary = true, AppUserId = 2 });
        context.Students.Add(new Student
        {
            Id = 1,
            Name = "Alex Student",
            DateOfBirth = new DateOnly(2015, 1, 1),
            CurrentRankId = 1,
            Status = StudentStatus.Active,
            EnrolmentDate = new DateOnly(2024, 1, 1),
            AppUserId = 3
        });
        // Only Parent (AppUserId 1) is linked to Student 1 — Parent (AppUserId 2) is not.
        context.StudentGuardianLinks.Add(new StudentGuardianLink { StudentId = 1, ParentGuardianId = 1, CreatedByUserId = 3 });
        context.SaveChanges();
    }

    public void Dispose() => _anchorConnection.Dispose();

    private VlmsDbContext CreateContext() => new(_options, NullCurrentUserContext.Instance);

    private static AuthorizationHandlerContext BuildContext(Student resource) =>
        new([new StudentAccessRequirement()], new ClaimsPrincipal(new ClaimsIdentity()), resource);

    [Fact]
    public async Task ParentHandler_Allows_LinkedStudent()
    {
        using var db = CreateContext();
        var handler = new ParentStudentAccessHandler(db, new FakeCurrentUserContext(userId: 1, Role.Parent));
        var student = await db.Students.SingleAsync(s => s.Id == 1);
        var context = BuildContext(student);

        await handler.HandleAsync(context);

        Assert.True(context.HasSucceeded);
    }

    [Fact]
    public async Task ParentHandler_Denies_UnlinkedStudent()
    {
        using var db = CreateContext();
        var handler = new ParentStudentAccessHandler(db, new FakeCurrentUserContext(userId: 2, Role.Parent));
        var student = await db.Students.SingleAsync(s => s.Id == 1);
        var context = BuildContext(student);

        await handler.HandleAsync(context);

        Assert.False(context.HasSucceeded);
    }

    [Fact]
    public async Task StudentHandler_Allows_OwnRecord()
    {
        using var db = CreateContext();
        var handler = new StudentSelfAccessHandler(new FakeCurrentUserContext(userId: 3, Role.Student));
        var student = await db.Students.SingleAsync(s => s.Id == 1);
        var context = BuildContext(student);

        await handler.HandleAsync(context);

        Assert.True(context.HasSucceeded);
    }

    [Fact]
    public async Task StudentHandler_Denies_AnotherStudentsRecord()
    {
        using var db = CreateContext();
        var handler = new StudentSelfAccessHandler(new FakeCurrentUserContext(userId: 99, Role.Student));
        var student = await db.Students.SingleAsync(s => s.Id == 1);
        var context = BuildContext(student);

        await handler.HandleAsync(context);

        Assert.False(context.HasSucceeded);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(999)]
    public async Task TeacherHandler_Allows_AnyStudent_RegardlessOfWhichOneIsTargeted(int studentId)
    {
        // No resource scoping for Teacher (low-level-design.md), so a Student row that doesn't
        // even exist in the DB is a valid input here — the handler never queries for it.
        var handler = new TeacherStudentAccessHandler(new FakeCurrentUserContext(userId: 10, Role.Teacher));
        var student = new Student
        {
            Id = studentId,
            Name = "X",
            DateOfBirth = new DateOnly(2015, 1, 1),
            CurrentRankId = 1,
            Status = StudentStatus.Active,
            EnrolmentDate = new DateOnly(2024, 1, 1)
        };
        var context = BuildContext(student);

        await handler.HandleAsync(context);

        Assert.True(context.HasSucceeded);
    }

    [Fact]
    public async Task TeacherHandler_Denies_WhenCallerIsNotATeacher()
    {
        var handler = new TeacherStudentAccessHandler(new FakeCurrentUserContext(userId: 1, Role.Parent));
        var student = new Student
        {
            Id = 1,
            Name = "X",
            DateOfBirth = new DateOnly(2015, 1, 1),
            CurrentRankId = 1,
            Status = StudentStatus.Active,
            EnrolmentDate = new DateOnly(2024, 1, 1)
        };
        var context = BuildContext(student);

        await handler.HandleAsync(context);

        Assert.False(context.HasSucceeded);
    }
}
