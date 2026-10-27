using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vlms.Domain;
using Vlms.Infrastructure;
using Vlms.Infrastructure.Identity;
using Vlms.Infrastructure.Registration;
using Vlms.Tests.Infrastructure;
using Vlms.Web.Components.Pages.Registration;
using Xunit;

namespace Vlms.Tests.Web;

/// <summary>
/// The final-approval page offers "Link to existing guardian" for a guardian record in the chapter
/// with no account and the applicant's email (adr/0008 decision 8), showing its name and children,
/// with "Create a new guardian record" chosen by default. The real page, service and SQLite are used.
/// </summary>
public sealed class ApproveRegistrationPageTests : BunitContext, IDisposable
{
    private readonly SqliteConnection _anchor;
    private readonly ServiceProvider _adminProvider;
    private readonly int _applicationId;

    public ApproveRegistrationPageTests()
    {
        var connectionString = $"Data Source=file:vlms-approve-page-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        _anchor = new SqliteConnection(connectionString);
        _anchor.Open();

        using (var seed = new VlmsDbContext(
            new DbContextOptionsBuilder<VlmsDbContext>().UseSqlite(connectionString).Options, TestSeedUserContext.Instance))
        {
            seed.Database.EnsureCreated();
            seed.Ranks.Add(new Rank { Id = 1, Order = 1, Code = "R1", Name = "Recruit" });
            seed.AppUsers.Add(new AppUser { Id = 1, DisplayName = "Admin One", Email = "admin@example.com" });
            seed.AppUsers.Add(new AppUser { Id = 30, DisplayName = "Jane Applicant", Email = "jane@example.com" });
            seed.UserRoles.Add(new UserRole { UserId = 1, Role = Role.Admin, ChapterId = Chapter.ShrewsburyId });
            seed.Students.Add(new Student
            {
                Id = 100, Name = "Kim Child", DateOfBirth = new DateOnly(2015, 3, 4), CurrentRankId = 1,
                Status = StudentStatus.Active, EnrolmentDate = new DateOnly(2024, 1, 1)
            });
            seed.ParentGuardians.Add(new ParentGuardian { Id = 50, Name = "Jane Smith", ContactInfo = " JANE@example.com", IsPrimary = true });
            seed.StudentGuardianLinks.Add(new StudentGuardianLink { StudentId = 100, ParentGuardianId = 50, CreatedByUserId = 1 });
            seed.StudentApplications.Add(new StudentApplication
            {
                Id = 1, ChapterId = Chapter.ShrewsburyId, ApplicantUserId = 30, ParentPhone = "07000 000001", ParentPostalAddress = "1 Church Lane", Relationship = "Mother",
                ChildName = "Chris Child", ChildDateOfBirth = new DateOnly(2016, 5, 6), PhotoMediaConsent = true, TransportConsent = true,
                OffsiteTripConsent = true, DataSharingConsent = true, Status = StudentApplicationStatus.AwaitingAdminApproval,
                SubmittedAt = DateTime.UtcNow, LastSubmittedAt = DateTime.UtcNow, SafeguardingReviewedAt = DateTime.UtcNow
            });
            seed.StudentApplicationSensitiveDetails.Add(new StudentApplicationSensitiveDetails
            {
                Id = 1, StudentApplicationId = 1, ChapterId = Chapter.ShrewsburyId, EmergencyContact = "Grandma"
            });
            seed.SaveChanges();
            _applicationId = 1;
        }

        var auth = AddAuthorization();
        auth.SetAuthorized("Admin One");
        auth.SetRoles("Admin");

        _adminProvider = TestIdentityServices.BuildServiceProvider(connectionString, new FakeCurrentUserContext(1, Role.Admin));
        var scope = _adminProvider.CreateScope();
        Services.AddSingleton(new StudentApplicationService(
            scope.ServiceProvider.GetRequiredService<VlmsDbContext>(),
            scope.ServiceProvider.GetRequiredService<ICurrentUserContext>(),
            scope.ServiceProvider.GetRequiredService<UserManager<VlmsIdentityUser>>()));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _adminProvider.Dispose();
            _anchor.Dispose();
        }

        base.Dispose(disposing);
    }

    [Fact]
    public void Approval_OffersLinkToExistingGuardian_WithNameAndChildren_NewRecordByDefault()
    {
        Services.GetRequiredService<NavigationManager>().NavigateTo($"/registration/approve?id={_applicationId}");
        var cut = Render<Approve>();
        cut.WaitForState(() => cut.FindAll("input[name='Input.ExistingGuardianId']").Count == 2);

        Assert.True(cut.Find("#newGuardian").HasAttribute("checked"));
        Assert.False(cut.Find("#guardian50").HasAttribute("checked"));
        Assert.Equal("50", cut.Find("#guardian50").GetAttribute("value"));
        var label = cut.Find("label[for='guardian50']").TextContent;
        Assert.Contains("Link to existing guardian Jane Smith", label);
        Assert.Contains("Kim Child", label);
    }
}
