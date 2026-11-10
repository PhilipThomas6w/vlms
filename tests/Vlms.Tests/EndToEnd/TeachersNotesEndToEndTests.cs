using System.Net;
using Vlms.Domain;
using Vlms.Infrastructure.Storage;
using Xunit;

namespace Vlms.Tests.EndToEnd;

/// <summary>
/// docs/adr/0015 over the real pipeline: the rendered lesson page (which is also exactly what a device saves for offline
/// use, as the service worker caches the page as served) carries the teacher's notes and the rank's Teacher's
/// Information for Teachers, Admins, the National Administrator and Approvers only; a Student or Parent gets neither,
/// nor any trace of a notes-only picture, and the notes-picture address refuses them.
/// </summary>
public sealed class TeachersNotesEndToEndTests(TeachersNotesEndToEndTests.Site site) : IClassFixture<TeachersNotesEndToEndTests.Site>
{
    private const string NotesMarker = "NOTES-E2E-PLAN";
    private const string InformationMarker = "INFORMATION-E2E-GUIDANCE";

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
                db.Ranks.Add(new Rank { Id = 1, Order = 1, Code = "E2E", Name = "End to end", TeachersInformation = $"## Guidance\n\n{InformationMarker}\n" });
                db.Lessons.Add(new Lesson
                {
                    Id = 1, RankId = 1, Code = "E2E-01", Title = "Lesson", Order = 1, IsActive = true, Body = "## Questions\n\nVisible to all.\n",
                    ContentVersion = "v1", TeachersNotes = $"## Plan\n\n{NotesMarker}\n\n![Plan](plan.jpg)\n"
                });
                db.LessonPictures.Add(new LessonPicture
                {
                    Id = 1, LessonId = 1, Order = 1, FileName = "plan.jpg", BlobKey = "lesson-pictures/E2E-01/plan.jpg", TeachersNotesOnly = true
                });
                await db.SaveChangesAsync();
            });

            foreach (var role in new[] { Role.Teacher, Role.Admin, Role.NationalAdministrator, Role.Approver, Role.Student, Role.Parent })
            {
                var user = await Factory.SeedUserAsync($"{role}@notes.example", MfaGatedRoles.Contains(role), role);
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
    public async Task LessonPage_ForStaff_ShowsTheNotesAndTheTeachersInformation(Role role)
    {
        var response = await site.Clients[role].GetAsync("/lessons/E2E-01");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains(NotesMarker, html);
        Assert.Contains(InformationMarker, html);
        Assert.Contains("data-teachers-notes", html);
        Assert.Contains("/lessons/teachers-notes/pictures/1", html);
    }

    [Theory]
    [InlineData(Role.Student)]
    [InlineData(Role.Parent)]
    public async Task LessonPage_ForStudentsAndParents_HasNoNotesNoTeachersInformationAndNoTraceOfEither(Role role)
    {
        var response = await site.Clients[role].GetAsync("/lessons/E2E-01");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("Visible to all.", html);
        Assert.DoesNotContain(NotesMarker, html);
        Assert.DoesNotContain(InformationMarker, html);
        Assert.DoesNotContain("Teacher&#x27;s notes", html);
        Assert.DoesNotContain("Teacher's notes", html);
        Assert.DoesNotContain("teachers-notes", html);
        Assert.DoesNotContain("data-teachers-information", html);
    }

    [Theory]
    [InlineData(Role.Teacher)]
    [InlineData(Role.Approver)]
    public async Task NotesPicture_IsServedToStaff(Role role)
    {
        var response = await site.Clients[role].GetAsync("/lessons/teachers-notes/pictures/1");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal([0xFF, 0xD8, 0xFF], await response.Content.ReadAsByteArrayAsync());
    }

    [Theory]
    [InlineData(Role.Student)]
    [InlineData(Role.Parent)]
    public async Task NotesPicture_IsRefusedToStudentsAndParents_AtBothAddresses(Role role)
    {
        // The cookie scheme answers a refusal with a redirect to the access-denied page.
        var refused = await site.Clients[role].GetAsync("/lessons/teachers-notes/pictures/1");
        Assert.Equal(HttpStatusCode.Redirect, refused.StatusCode);
        Assert.StartsWith("/Account/AccessDenied", Browser.RelativeLocation(refused), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HttpStatusCode.NotFound, (await site.Clients[role].GetAsync("/lessons/pictures/1")).StatusCode);
    }
}
