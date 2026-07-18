using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Identity.Web;
using Vlms.Domain;
using Vlms.Infrastructure;
using Vlms.Infrastructure.Authorization;
using Vlms.Infrastructure.Curriculum;
using Vlms.Infrastructure.Guardianship;
using Vlms.Infrastructure.Provisioning;
using Vlms.Infrastructure.Registration;
using Vlms.Infrastructure.Security;
using Vlms.Web.Components;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Cascading authentication state — the mechanism that fixes the interactive-render-mode gap below
// (see AuthenticationStatePrincipalResolver's doc comment). Equivalent to placing a
// <CascadingAuthenticationState> at the root of the component tree; Routes.razor's
// AuthorizeRouteView consumes the resulting Task<AuthenticationState> cascading parameter.
builder.Services.AddCascadingAuthenticationState();

// --- Data access (adr/0001-technology-stack.md: Azure SQL Database) -------------------------
builder.Services.AddDbContext<VlmsDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("VlmsDatabase")));

// --- Current-user context: EntraCurrentUserContext (not NullCurrentUserContext, which stays
// reserved for design-time/migration use in VlmsDbContextFactory) is what VlmsDbContext resolves
// at runtime here, per adr/0004-sensitive-data-access-control.md. Built from the AuthenticationStateProvider
// itself (not IHttpContextAccessor — see AuthenticationStatePrincipalResolver's doc comment for why)
// + a fresh DbContextOptions<VlmsDbContext> — never the DI-resolved VlmsDbContext instance itself,
// to avoid a circular resolution (see EntraCurrentUserContext's doc comment). Deliberately NOT
// resolved here (i.e. no AuthenticationStatePrincipalResolver.Resolve call in this factory): that
// shape regressed sign-in, because this factory also runs during the OIDC OnTokenValidated callback
// (via UserProvisioningService -> VlmsDbContext -> ICurrentUserContext), which is not a rendered
// Razor component's DI scope, and eagerly resolving here throws. EntraCurrentUserContext's
// AuthenticationStateProvider constructor defers the resolve to first read of UserId/HasRole —
// see its doc comment. -----------------------------------------------------------------------
builder.Services.AddScoped<ICurrentUserContext>(sp =>
{
    var authenticationStateProvider = sp.GetRequiredService<AuthenticationStateProvider>();
    var dbContextOptions = sp.GetRequiredService<DbContextOptions<VlmsDbContext>>();
    return new EntraCurrentUserContext(authenticationStateProvider, dbContextOptions);
});

builder.Services.AddScoped<UserProvisioningService>();

// --- Curriculum management workflow (docs/design/low-level-design.md "LessonProposalService")
builder.Services.AddScoped<LessonProposalService>();

// --- Guardian-link creation (functional.md FR-004, data-design.md "Guardian link verification")
builder.Services.AddScoped<GuardianLinkService>();

// --- Student registration/enrolment (data-design.md Student/StudentRankProgress, STATE.md) —
// creates the Student, opens their first StudentRankProgress row, and creates a guardian link via
// GuardianLinkService above (reused, not duplicated).
builder.Services.AddScoped<StudentRegistrationService>();

// --- Authorization: one policy per Role.Enum value (role-based), plus a resource-based
// StudentAccess policy (Parent/Student/Teacher handlers) — docs/design/low-level-design.md
// "Authorization model", adr/0002-roles-as-application-claims.md. -----------------------------
builder.Services.AddScoped<IAuthorizationHandler, RoleAuthorizationHandler>();
builder.Services.AddScoped<IAuthorizationHandler, AnyRoleAuthorizationHandler>();
builder.Services.AddScoped<IAuthorizationHandler, ParentStudentAccessHandler>();
builder.Services.AddScoped<IAuthorizationHandler, StudentSelfAccessHandler>();
builder.Services.AddScoped<IAuthorizationHandler, TeacherStudentAccessHandler>();

builder.Services.AddAuthorization(options =>
{
    foreach (var role in Enum.GetValues<Role>())
    {
        options.AddPolicy($"Require{role}", policy => policy.Requirements.Add(new RoleRequirement(role)));
    }

    options.AddPolicy("StudentAccess", policy => policy.Requirements.Add(new StudentAccessRequirement()));

    // Guardian-link creation page (FR-004): Admin or Teacher, never Parent self-service.
    options.AddPolicy("RequireAdminOrTeacher",
        policy => policy.Requirements.Add(new AnyRoleRequirement(Role.Admin, Role.Teacher)));
});

// --- Authentication: Microsoft Entra External ID (CIAM) sign-in via Microsoft.Identity.Web,
// per adr/0001-technology-stack.md. Configuration is placeholder-only in appsettings.json — no
// live Entra tenant is available to this build, so this cannot be end-to-end tested; it is kept
// structurally correct and buildable instead. ---------------------------------------------------
builder.Services.AddAuthentication(OpenIdConnectDefaults.AuthenticationScheme)
    .AddMicrosoftIdentityWebApp(builder.Configuration.GetSection("AzureAd"));

// Provisions the AppUser/UserRole rows (UserProvisioningService) on every successful sign-in —
// a newly created AppUser gets zero roles (deny-by-default; role assignment is a separate,
// Admin-only action, out of scope here).
builder.Services.Configure<OpenIdConnectOptions>(OpenIdConnectDefaults.AuthenticationScheme, options =>
{
    var existingOnTokenValidated = options.Events.OnTokenValidated;
    options.Events.OnTokenValidated = async context =>
    {
        if (existingOnTokenValidated is not null)
        {
            await existingOnTokenValidated(context);
        }

        var principal = context.Principal;
        var entraObjectId = principal?.FindFirst(EntraClaimTypes.ObjectId)?.Value
            ?? principal?.FindFirst(EntraClaimTypes.ObjectIdShort)?.Value;

        if (string.IsNullOrEmpty(entraObjectId) || context.HttpContext is null)
        {
            return;
        }

        var displayName = principal?.Identity?.Name ?? entraObjectId;
        var email = principal?.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value
            ?? principal?.FindFirst("preferred_username")?.Value
            ?? string.Empty;

        var provisioning = context.HttpContext.RequestServices.GetRequiredService<UserProvisioningService>();
        await provisioning.FindOrCreateAsync(entraObjectId, displayName, email);
    };
});

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAuthentication();
app.UseAuthorization();

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
