using System.Net;
using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Vlms.Domain;
using Vlms.Infrastructure.Authorization;

namespace Vlms.Tests.EndToEnd;

/// <summary>
/// Every routable page of Vlms.Web, requested over the real pipeline as each role (and two
/// multi-role users). Routes come from reflection over <see cref="RouteAttribute"/>; the expected
/// outcome is derived from each page's <c>[Authorize(Policy=...)]</c> and <see cref="PolicyRoles"/>,
/// which <see cref="Policy_role_map_matches_the_policies_registered_in_Program"/> keeps in step with Program.cs.
/// Allowed gives 200, refused gives 302 to /Account/AccessDenied (itself 200), never 500.
/// </summary>
public sealed class RoleMatrixEndToEndTests(RoleMatrixEndToEndTests.Sessions sessions)
    : IClassFixture<RoleMatrixEndToEndTests.Sessions>
{
    /// <summary>Roles that must hold MFA (MfaRequirementHandler); every profile holding one is seeded with TOTP.</summary>
    private static readonly Role[] MfaGatedRoles = [Role.Admin, Role.SafeguardingOfficer, Role.Teacher];

    /// <summary>Policy name to the roles that satisfy it (before the MFA requirement every policy carries).</summary>
    private static readonly Dictionary<string, Role[]> PolicyRoles = new()
    {
        ["RequireAdmin"] = [Role.Admin],
        ["RequireTeacher"] = [Role.Teacher],
        ["RequireApprover"] = [Role.Approver],
        ["RequireParent"] = [Role.Parent],
        ["RequireStudent"] = [Role.Student],
        ["RequireSafeguardingOfficer"] = [Role.SafeguardingOfficer],
        ["RequireAdminOrTeacher"] = [Role.Admin, Role.Teacher],
        ["RequireAdminOrSafeguardingOfficer"] = [Role.Admin, Role.SafeguardingOfficer],
        ["RequireLessonLibrary"] = [Role.Teacher, Role.Approver, Role.Admin, Role.Student, Role.Parent],
        // Any signed-in user, with no role requirement at all (parent registration, adr/0008 answer 1).
        ["RequireSignedIn"] = Enum.GetValues<Role>(),
    };

    public static TheoryData<string> Profiles => new()
    {
        "Admin", "Teacher", "Approver", "Parent", "Student", "SafeguardingOfficer", "Teacher+Admin", "Teacher+Parent",
    };

    /// <summary>One factory for the class: every profile is seeded and signed in once.</summary>
    public sealed class Sessions : IAsyncLifetime
    {
        public VlmsWebFactory Factory { get; } = new();
        public Dictionary<string, (Role[] Roles, HttpClient Client)> ByProfile { get; } = new();

        public async Task InitializeAsync()
        {
            foreach (var profile in (IEnumerable<object[]>)Profiles)
            {
                var name = (string)profile[0];
                var roles = name.Split('+').Select(Enum.Parse<Role>).ToArray();
                var user = await Factory.SeedUserAsync(
                    $"{name.Replace('+', '-')}@matrix.example", roles.Any(MfaGatedRoles.Contains), roles);
                ByProfile[name] = (roles, await Browser.SignInAsync(Factory, user));
            }
        }

        public Task DisposeAsync()
        {
            foreach (var (_, client) in ByProfile.Values)
            {
                client.Dispose();
            }

            Factory.Dispose();
            return Task.CompletedTask;
        }
    }

    private sealed record Page(string Route, string? Policy, bool RequiresSignIn);

    /// <summary>Static routes of components in Vlms.Web, skipping parameterised and /Account routes.</summary>
    private static List<Page> DiscoverPages() =>
        typeof(Vlms.Web.Components.App).Assembly.GetTypes()
            .SelectMany(type => type.GetCustomAttributes<RouteAttribute>(inherit: false).Select(route => (type, route.Template)))
            .Where(x => !x.Template.Contains('{') && !x.Template.StartsWith("/Account", StringComparison.OrdinalIgnoreCase))
            .Select(x =>
            {
                var authorize = x.type.GetCustomAttributes<AuthorizeAttribute>(inherit: true).FirstOrDefault();
                return new Page(x.Template, authorize?.Policy, authorize is not null);
            })
            .OrderBy(p => p.Route, StringComparer.Ordinal)
            .ToList();

    private static bool IsAllowed(Page page, Role[] roles) =>
        !page.RequiresSignIn || page.Policy is null || PolicyRoles[page.Policy].Intersect(roles).Any();

    [Fact]
    public void Routes_are_discovered_and_every_policy_used_by_a_page_is_in_the_role_map()
    {
        var pages = DiscoverPages();

        Assert.True(pages.Count >= 20, $"Only {pages.Count} routable pages discovered; reflection looks broken.");
        Assert.Contains(pages, p => p.Route == "/lessons");
        Assert.Contains(pages, p => p.Route == "/safeguarding/consent");
        var unmapped = pages.Where(p => p.Policy is not null && !PolicyRoles.ContainsKey(p.Policy)).Select(p => $"{p.Route} ({p.Policy})");
        Assert.Empty(unmapped);
    }

    [Fact]
    public async Task Policy_role_map_matches_the_policies_registered_in_Program()
    {
        var provider = sessions.Factory.Services.GetRequiredService<IAuthorizationPolicyProvider>();

        foreach (var (name, expectedRoles) in PolicyRoles)
        {
            var policy = await provider.GetPolicyAsync(name);
            Assert.True(policy is not null, $"Policy {name} is not registered in Program.cs.");
            Assert.Contains(policy!.Requirements, r => r is MfaRequirement);

            if (name == "RequireSignedIn")
            {
                // No role requirement: authenticated users only, plus the MFA rule.
                Assert.Contains(policy.Requirements, r => r is Microsoft.AspNetCore.Authorization.Infrastructure.DenyAnonymousAuthorizationRequirement);
                Assert.DoesNotContain(policy.Requirements, r => r is AnyRoleRequirement or RoleRequirement);
                continue;
            }

            var actual = policy.Requirements.SelectMany(r => r switch
            {
                AnyRoleRequirement any => any.Roles,
                RoleRequirement single => [single.Role],
                _ => Array.Empty<Role>(),
            }).ToHashSet();
            Assert.True(expectedRoles.ToHashSet().SetEquals(actual),
                $"{name}: map says [{string.Join(", ", expectedRoles)}], Program.cs registers [{string.Join(", ", actual)}].");
        }

        // The per-role policies Program.cs registers from Enum.GetValues<Role>() are all in the map.
        foreach (var role in Enum.GetValues<Role>())
        {
            Assert.True(PolicyRoles.ContainsKey($"Require{role}"), $"Require{role} is registered but missing from the map.");
        }
    }

    [Theory]
    [MemberData(nameof(Profiles))]
    public async Task Each_page_gives_200_when_allowed_and_access_denied_otherwise_never_500(string profile)
    {
        var (roles, client) = sessions.ByProfile[profile];
        var problems = new List<string>();
        var allowedCount = 0;

        foreach (var page in DiscoverPages())
        {
            var response = await client.GetAsync(page.Route);
            if (!page.RequiresSignIn)
            {
                // Public pages (home, invite acceptance, not-found): only a server error is a failure.
                if ((int)response.StatusCode >= 500)
                {
                    problems.Add($"{page.Route}: public page gave {(int)response.StatusCode}");
                }

                continue;
            }

            if (IsAllowed(page, roles))
            {
                allowedCount++;
                if (response.StatusCode != HttpStatusCode.OK)
                {
                    problems.Add($"{page.Route}: expected 200 for {profile}, got {(int)response.StatusCode}");
                }
            }
            else if (response.StatusCode != HttpStatusCode.Redirect
                     || !Browser.RelativeLocation(response).StartsWith("/Account/AccessDenied", StringComparison.OrdinalIgnoreCase))
            {
                problems.Add($"{page.Route}: expected 302 to /Account/AccessDenied for {profile}, got {(int)response.StatusCode}"
                    + (response.Headers.Location is null ? "" : $" to {response.Headers.Location}"));
            }
        }

        Assert.True(allowedCount > 0, $"{profile} reached no pages at all.");
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/Account/AccessDenied")).StatusCode);
    }

    [Fact]
    public async Task Teacher_plus_admin_reaches_both_the_teacher_and_the_admin_pages()
    {
        var client = sessions.ByProfile["Teacher+Admin"].Client;

        foreach (var path in new[] { "/progress/record", "/curriculum/teacher", "/students", "/administration/students", "/reporting" })
        {
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(path)).StatusCode);
        }
    }

    [Fact]
    public async Task Teacher_plus_parent_reaches_teacher_pages_and_the_parent_dashboard_but_not_admin_pages()
    {
        var client = sessions.ByProfile["Teacher+Parent"].Client;

        foreach (var path in new[] { "/progress/record", "/curriculum/teacher", "/parent/dashboard", "/parent/consent", "/lessons" })
        {
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(path)).StatusCode);
        }

        var denied = await client.GetAsync("/administration/students");
        Assert.Equal(HttpStatusCode.Redirect, denied.StatusCode);
        Assert.StartsWith("/Account/AccessDenied", Browser.RelativeLocation(denied), StringComparison.OrdinalIgnoreCase);
    }
}
