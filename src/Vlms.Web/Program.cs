using Azure.Communication.Email;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Vlms.Domain;
using Vlms.Infrastructure;
using Vlms.Infrastructure.Authorization;
using Vlms.Infrastructure.Curriculum;
using Vlms.Infrastructure.Engagement;
using Vlms.Infrastructure.Guardianship;
using Vlms.Infrastructure.Identity;
using Vlms.Infrastructure.Notifications;
using Vlms.Infrastructure.Registration;
using Vlms.Infrastructure.Reporting;
using Vlms.Infrastructure.Safeguarding;
using Vlms.Infrastructure.Security;
using Vlms.Web.Components;
using Vlms.Web.Components.Account;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Cascading authentication state — the mechanism that fixes the interactive-render-mode gap below
// (see AuthenticationStatePrincipalResolver's doc comment). Equivalent to placing a
// <CascadingAuthenticationState> at the root of the component tree; Routes.razor's
// AuthorizeRouteView consumes the resulting Task<AuthenticationState> cascading parameter.
builder.Services.AddCascadingAuthenticationState();

// --- ASP.NET Core Identity support pieces (STATE.md "Identity" item — replacing Microsoft Entra
// External ID). IdentityRevalidatingAuthenticationStateProvider is the officially documented
// AuthenticationStateProvider for Blazor Web App + Server interactivity + ASP.NET Core Identity
// (learn.microsoft.com/aspnet/core/blazor/security/server) — periodically re-validates the caller's
// security stamp for a connected circuit so a revoked/changed credential doesn't stay "signed in"
// indefinitely. IdentityRedirectManager is the small helper the Account pages use for
// open-redirect-safe post-auth navigation, same as the official template. -----------------------
builder.Services.AddScoped<IdentityRedirectManager>();
builder.Services.AddScoped<AuthenticationStateProvider, IdentityRevalidatingAuthenticationStateProvider>();

// Data Protection — explicit, not relied on as an implicit side effect of cookie authentication
// below — backs GuardianInviteService's signed, time-limited invite tokens
// (ITimeLimitedDataProtector) as well as Identity's own cookie encryption. Default key-ring
// persistence (OS user profile) is sufficient for ADR-0005's single-VPS, single-instance deployment
// target — see GuardianInviteService's doc comment.
builder.Services.AddDataProtection();

// --- Data access (adr/0005-hosting-pivot-self-hosted-vps-postgresql.md: self-hosted PostgreSQL,
// supersedes adr/0001-technology-stack.md's original Azure SQL Database row) -------------------
builder.Services.AddDbContext<VlmsDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("VlmsDatabase")));

// --- Current-user context: IdentityCurrentUserContext (not NullCurrentUserContext, which stays
// reserved for design-time/migration use in VlmsDbContextFactory) is what VlmsDbContext resolves
// at runtime here, per adr/0004-sensitive-data-access-control.md. Built from the AuthenticationStateProvider
// itself (not IHttpContextAccessor — see AuthenticationStatePrincipalResolver's doc comment for why)
// + a fresh DbContextOptions<VlmsDbContext> — never the DI-resolved VlmsDbContext instance itself,
// to avoid a circular resolution (see IdentityCurrentUserContext's doc comment). Deliberately NOT
// resolved here (i.e. no AuthenticationStatePrincipalResolver.Resolve call in this factory): the
// Entra-era codebase found that shape regressed sign-in (the OIDC/account-creation path is not a
// rendered Razor component's DI scope, and eagerly resolving here throws) — the same architecture is
// retained under Identity for the same reason. IdentityCurrentUserContext's AuthenticationStateProvider
// constructor defers the resolve to first read of UserId/HasRole/HasMfaEnabled — see its doc comment.
builder.Services.AddScoped<ICurrentUserContext>(sp =>
{
    var authenticationStateProvider = sp.GetRequiredService<AuthenticationStateProvider>();
    var dbContextOptions = sp.GetRequiredService<DbContextOptions<VlmsDbContext>>();
    return new IdentityCurrentUserContext(authenticationStateProvider, dbContextOptions);
});

// --- Account creation (STATE.md "Identity" item) — creates the paired VlmsIdentityUser/AppUser row
// (shared primary key — see AppUser's doc comment). Used by the Register and guardian invite-accept
// flows; retires the former Entra-federation-triggered UserProvisioningService (see
// AppUserProvisioningService's doc comment for why it isn't merely a rename).
builder.Services.AddScoped<AppUserProvisioningService>();

// --- Curriculum management workflow (docs/design/low-level-design.md "LessonProposalService")
builder.Services.AddScoped<LessonProposalService>();

// --- Guardian invite issuance (STATE.md "Identity" item 5) — must be registered before
// GuardianLinkService below, which now depends on it.
builder.Services.AddScoped<GuardianInviteService>();

// --- Guardian-link creation (functional.md FR-004, data-design.md "Guardian link verification")
builder.Services.AddScoped<GuardianLinkService>();

// --- Student registration/enrolment (data-design.md Student/StudentRankProgress, STATE.md) —
// creates the Student, opens their first StudentRankProgress row, and creates a guardian link via
// GuardianLinkService above (reused, not duplicated).
builder.Services.AddScoped<StudentRegistrationService>();

// --- Consent/DBS management UI (STATE.md, functional.md FR-001/FR-002, data-design.md
// ConsentRecord/ConsentSensitiveDetails/DbsCheck) — Admin/SafeguardingOfficer only, never the
// Approver role (curriculum-only, CLAUDE.md Project Law).
builder.Services.AddScoped<ConsentRecordService>();
builder.Services.AddScoped<DbsCheckService>();

// --- Parent dashboard (STATE.md, functional.md "Parent engagement") — scoped to only the caller's
// own StudentGuardianLink-linked students, via ParentGuardianLinkage (shared with
// ParentStudentAccessHandler below).
builder.Services.AddScoped<ParentDashboardService>();

// --- Admin reporting (STATE.md "Reporting screens: core progress stats + at-risk flagging",
// functional.md "Reporting (MVP)") — Admin-only (see ProgressReportingService's doc comment for
// why this isn't the Admin-or-SafeguardingOfficer pattern the consent/DBS pages use). At-risk
// flagging reuses AtRiskStudentFlagging, the same computation ConsentExpiryJob's daily sweep uses.
builder.Services.AddScoped<ProgressReportingService>();

// --- Notifications (adr/0001-technology-stack.md, low-level-design.md "NotificationService") —
// now wired into Vlms.Web itself, not just Vlms.Jobs (STATE.md "Identity" item 5: guardian invite
// emails are sent from the web app, at the moment the Admin/Teacher creates the link — there is no
// batch/job context to defer this to, unlike ConsentExpiryNotifier's daily-sweep digest). Same
// placeholder-config status CommunicationServices had in Vlms.Jobs before a live Azure Communication
// Services resource existed — appsettings.json's "CommunicationServices" section. Superseded by the
// SMTP-based IEmailSender STATE.md Next item 2 names once that lands.
builder.Services.AddSingleton(sp => new EmailClient(
    sp.GetRequiredService<IConfiguration>().GetValue<string>("CommunicationServices:ConnectionString")
        ?? throw new InvalidOperationException("CommunicationServices:ConnectionString is not configured.")));
builder.Services.AddSingleton<IEmailSender>(sp => new AzureCommunicationEmailSender(
    sp.GetRequiredService<EmailClient>(),
    sp.GetRequiredService<IConfiguration>().GetValue<string>("CommunicationServices:SenderAddress")
        ?? throw new InvalidOperationException("CommunicationServices:SenderAddress is not configured.")));
builder.Services.AddSingleton<INotificationService, NotificationService>();

// --- Authorization: one policy per Role.Enum value (role-based), plus a resource-based
// StudentAccess policy (Parent/Student/Teacher handlers) — docs/design/low-level-design.md
// "Authorization model", adr/0002-roles-as-application-claims.md. Every policy below also carries
// an MfaRequirement (STATE.md "Identity" item 4) — see MfaRequirement's own doc comment for the
// "enforced globally for Admin/SafeguardingOfficer, offered to everyone else" scope decision. -----
builder.Services.AddScoped<IAuthorizationHandler, RoleAuthorizationHandler>();
builder.Services.AddScoped<IAuthorizationHandler, AnyRoleAuthorizationHandler>();
builder.Services.AddScoped<IAuthorizationHandler, ParentStudentAccessHandler>();
builder.Services.AddScoped<IAuthorizationHandler, StudentSelfAccessHandler>();
builder.Services.AddScoped<IAuthorizationHandler, TeacherStudentAccessHandler>();
builder.Services.AddScoped<IAuthorizationHandler, MfaRequirementHandler>();

builder.Services.AddAuthorization(options =>
{
    foreach (var role in Enum.GetValues<Role>())
    {
        options.AddPolicy($"Require{role}", policy =>
        {
            policy.Requirements.Add(new RoleRequirement(role));
            policy.Requirements.Add(new MfaRequirement());
        });
    }

    options.AddPolicy("StudentAccess", policy =>
    {
        policy.Requirements.Add(new StudentAccessRequirement());
        policy.Requirements.Add(new MfaRequirement());
    });

    // Guardian-link creation page (FR-004): Admin or Teacher, never Parent self-service.
    options.AddPolicy("RequireAdminOrTeacher", policy =>
    {
        policy.Requirements.Add(new AnyRoleRequirement(Role.Admin, Role.Teacher));
        policy.Requirements.Add(new MfaRequirement());
    });

    // Consent/DBS management pages (STATE.md, adr/0004-sensitive-data-access-control.md): matches
    // the VlmsDbContext query filter on DbsCheck/ConsentSensitiveDetails exactly — Admin or
    // SafeguardingOfficer, never Approver (curriculum-only) or Teacher.
    options.AddPolicy("RequireAdminOrSafeguardingOfficer", policy =>
    {
        policy.Requirements.Add(new AnyRoleRequirement(Role.Admin, Role.SafeguardingOfficer));
        policy.Requirements.Add(new MfaRequirement());
    });
});

// --- Authentication: self-rolled ASP.NET Core Identity (STATE.md "Identity" item; adr/0005 —
// supersedes adr/0001-technology-stack.md's Microsoft Entra External ID row). AddIdentityCookies()
// wires the two cookie schemes Identity's own SignInManager/Account pages expect
// (IdentityConstants.ApplicationScheme for the signed-in cookie, .ExternalScheme for the transient
// external-login handshake — unused here since no external providers are configured, but part of
// the framework's standard wiring). AddIdentityCore (not AddIdentity) + AddSignInManager: the
// leaner "no role store" combination — see VlmsDbContext's doc comment for why ASP.NET Core
// Identity's own role feature is never used (adr/0002-roles-as-application-claims.md). ------------
builder.Services.AddAuthentication(options =>
    {
        options.DefaultScheme = IdentityConstants.ApplicationScheme;
        options.DefaultSignInScheme = IdentityConstants.ExternalScheme;
    })
    .AddIdentityCookies();

builder.Services.AddIdentityCore<VlmsIdentityUser>(options =>
    {
        // No live email provider is wired for confirmation links yet (STATE.md Next item 2, the
        // SMTP-based email item — ADR-0005) — requiring confirmation would permanently lock every
        // new account out with no way to confirm. Revisit once that item lands.
        options.SignIn.RequireConfirmedAccount = false;
    })
    .AddEntityFrameworkStores<VlmsDbContext>()
    .AddSignInManager()
    .AddDefaultTokenProviders();

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

// Non-interactive endpoints Identity's own sign-out needs (writing/clearing the auth cookie, which
// an interactive Razor component cannot do — see IdentityComponentsEndpointRouteBuilderExtensions's
// doc comment).
app.MapAdditionalIdentityEndpoints();

app.Run();
