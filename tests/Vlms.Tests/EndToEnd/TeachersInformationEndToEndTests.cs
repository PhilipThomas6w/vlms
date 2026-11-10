using System.Net;
using Vlms.Domain;
using Vlms.Infrastructure.Storage;
using Xunit;

namespace Vlms.Tests.EndToEnd;

/// <summary>
/// docs/adr/0015 over the real pipeline: the rendered lesson page (which is also exactly what a device saves for offline
/// use, as the service worker caches the page as served) carries the lesson's Teacher's Information for Teachers, Admins,
/// the National Administrator and Approvers only; a Student or Parent gets none, nor any trace of a picture only it
/// uses, and the Teacher's Information picture address refuses them.
/// </summary>
public sealed class TeachersInformationEndToEndTests(TeachersInformationEndToEndTests.Site site) : IClassFixture<TeachersInformationEndToEndTests.Site>
{
    private const string InformationMarker = "INFORMATION-E2E-PLAN";

    private static readonly Role[] MfaGatedRoles = [Role.Admin, Role.SafeguardingOfficer, Role.Teacher, Role.NationalAdministrator];

    public sealed class Site : IAsyncLifetime
    {
        public VlmsWebFactory Factory { get; } = new();
        public Dictionary<Role, HttpClient> Clients { get; } = new();

        public async Task InitializeAsync()
        {
            await new LocalDiskBlobStorage(Factory.BlobRoot).UploadAsync("lesson-pictures/E2E-01/plan.jpg", [0xFF, 0xD8, 0xFF]);
            await Factory.WithDbAsync(async db =>
            {
                db.Ranks.Add(new Rank { Id = 1, Order = 1, Code = "E2E", Name = "End to end" });
                db.Lessons.Add(new Lesson
                {
                    Id = 1, RankId = 1, Code = "E2E-01", Title = "Lesson", Order = 1, IsActive = true, Body = "## Questions\n\nVisible to all.\n",
                    ContentVersion = "v1", TeachersInformation = $"## Plan\n\n{InformationMarker}\n\n![Plan](plan.jpg)\n"
                });
                db.LessonPictures.Add(new LessonPicture
                {
                    Id = 1, LessonId = 1, Order = 1, FileName = "plan.jpg", BlobKey = "lesson-pictures/E2E-01/plan.jpg", TeachersInformationOnly = true
                });
                await db.SaveChangesAsync();
            });

            foreach (var role in new[] { Role.Teacher, Role.Admin, Role.NationalAdministrator, Role.Approver, Role.Student, Role.Parent })
            {
                var user = await Factory.SeedUserAsync($"{role}@information.example", MfaGatedRoles.Contains(role), role);
                Clients[role] = await Browser.SignInAsync(Factory, user);
            }
        }

        public Task DisposeAsync()
        {
            foreach (var client in Clients.Values)
            {
                client.Dispose();
            }

            Factory.Dispose();
            return Task.CompletedTask;
        }
    }

    [Theory]
    [InlineData(Role.Teacher)]
    [InlineData(Role.Admin)]
    [InlineData(Role.NationalAdministrator)]
    [InlineData(Role.Approver)]
    public async Task LessonPage_ForStaff_ShowsTheTeachersInformation(Role role)
    {
        var response = await site.Clients[role].GetAsync("/lessons/E2E-01");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains(InformationMarker, html);
        Assert.Contains("data-teachers-information", html);
        Assert.Contains("/lessons/teachers-information/pictures/1", html);
    }

    [Theory]
    [InlineData(Role.Student)]
    [InlineData(Role.Parent)]
    public async Task LessonPage_ForStudentsAndParents_HasNoTeachersInformationAndNoTraceOfIt(Role role)
    {
        var response = await site.Clients[role].GetAsync("/lessons/E2E-01");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("Visible to all.", html);
        Assert.DoesNotContain(InformationMarker, html);
        Assert.DoesNotContain("Teacher&#x27;s Information", html);
        Assert.DoesNotContain("Teacher's Information", html);
        Assert.DoesNotContain("teachers-information", html);
        Assert.DoesNotContain("data-teachers-information", html);
    }

    [Theory]
    [InlineData(Role.Teacher)]
    [InlineData(Role.Approver)]
    public async Task TeachersInformationPicture_IsServedToStaff(Role role)
    {
        var response = await site.Clients[role].GetAsync("/lessons/teachers-information/pictures/1");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal([0xFF, 0xD8, 0xFF], await response.Content.ReadAsByteArrayAsync());
    }

    [Theory]
    [InlineData(Role.Student)]
    [InlineData(Role.Parent)]
    public async Task TeachersInformationPicture_IsRefusedToStudentsAndParents_AndNotServedAtTheLessonPictureAddress(Role role)
    {
        // The cookie scheme answers a refusal with a redirect to the access-denied page.
        var refused = await site.Clients[role].GetAsync("/lessons/teachers-information/pictures/1");
        Assert.Equal(HttpStatusCode.Redirect, refused.StatusCode);
        Assert.StartsWith("/Account/AccessDenied", Browser.RelativeLocation(refused), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HttpStatusCode.NotFound, (await site.Clients[role].GetAsync("/lessons/pictures/1")).StatusCode);
    }
}
