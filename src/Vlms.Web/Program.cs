using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Vlms.Domain;
using Vlms.Infrastructure;
using Vlms.Infrastructure.Attendance;
using Vlms.Infrastructure.Calendar;
using Vlms.Infrastructure.Authorization;
using Vlms.Infrastructure.Badges;
using Vlms.Infrastructure.Chapters;
using Vlms.Infrastructure.Curriculum;
using Vlms.Infrastructure.Engagement;
using Vlms.Infrastructure.Guardianship;
using Vlms.Infrastructure.Identity;
using Vlms.Infrastructure.Notifications;
using Vlms.Infrastructure.Progress;
using Vlms.Infrastructure.Registration;
using Vlms.Infrastructure.Reporting;
using Vlms.Infrastructure.Administration;
using Vlms.Infrastructure.Retention;
using Vlms.Infrastructure.Safeguarding;
using Vlms.Infrastructure.Security;
using Vlms.Infrastructure.Storage;
using Vlms.Web;
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
// (ITimeLimitedDataProtector) as well as Identity's own cookie encryption. The key ring is persisted
// to DataProtection:KeysPath (required in Production) — see VlmsDataProtection's doc comment.
builder.Services.AddVlmsDataProtection(builder.Configuration, requireKeysPath: builder.Environment.IsProduction());

// Allowed hosts (required in Production), HSTS lifetime, and rate limiting on the account forms —
// see WebHardening's doc comment.
builder.Services.AddVlmsWebHardening(builder.Configuration, builder.Environment);

// --- Data access (adr/0005-hosting-pivot-self-hosted-vps-postgresql.md: self-hosted PostgreSQL,
// supersedes adr/0001-technology-stack.md's original Azure SQL Database row) -------------------
builder.Services.AddDbContext<VlmsDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("VlmsDatabase")));

// /health for the external uptime monitor (docs/operations/monitoring.md §5): a database check, status only.
builder.Services.AddVlmsHealthChecks();

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
// The ChapterScopeOverride is the one per-scope object that lets GuardianInviteService read an
// invite's chapter for a caller not working in it (docs/adr/0010-chapters.md).
builder.Services.AddScoped<ChapterScopeOverride>();
builder.Services.AddScoped<ICurrentUserContext>(sp =>
{
    var authenticationStateProvider = sp.GetRequiredService<AuthenticationStateProvider>();
    var dbContextOptions = sp.GetRequiredService<DbContextOptions<VlmsDbContext>>();
    return new IdentityCurrentUserContext(
        authenticationStateProvider, dbContextOptions, sp.GetRequiredService<ChapterScopeOverride>());
});

// --- Account creation (STATE.md "Identity" item) — creates the paired VlmsIdentityUser/AppUser row
// (shared primary key — see AppUser's doc comment). Used by the Register and guardian invite-accept
// flows; retires the former Entra-federation-triggered UserProvisioningService (see
// AppUserProvisioningService's doc comment for why it isn't merely a rename).
builder.Services.AddScoped<AppUserProvisioningService>();

// --- Role assignment (STATE.md "Admin role assignment + first-Admin bootstrap") — Admin-only
// grant/revoke of UserRole rows, role-checked inside the service. Its companion
// FirstAdminBootstrapService is deliberately NOT registered here: it is wired only into the
// Vlms.Jobs console host (see its doc comment and FirstAdminBootstrapWiringTests).
builder.Services.AddScoped<RoleAssignmentService>();

// --- Chapters (FR-021, docs/adr/0010-chapters.md): the chapter switcher, open chapters for
// applicants, and the National Administrator's chapter and national-role pages; the anonymous
// national totals (counts per chapter per rank, never names).
builder.Services.AddScoped<ChapterService>();
builder.Services.AddScoped<NationalTotalsService>();

// --- Lesson suggestions, review and history (FR-007, docs/adr/0013): drafts, review, "Publish now", versions and the
// national publishing setting, each call role-checked in the service; pictures through LessonSuggestionEndpoints.
// Every Approver is emailed when a suggestion is submitted (adr/0013 decision 8), after the save, never throwing.
builder.Services.AddScoped(sp => new LessonSuggestionNotificationService(
    sp.GetRequiredService<VlmsDbContext>(), sp.GetRequiredService<INotificationService>(),
    sp.GetRequiredService<ILogger<LessonSuggestionNotificationService>>(), builder.Configuration["App:PublicBaseUrl"]));
builder.Services.AddScoped<LessonSuggestionService>();

// --- Lesson library (functional.md FR-006) — read-only browse + file download for Teacher/Approver/
// Admin/Student/Parent, via LessonFileEndpoint and Pages/Lessons/LessonLibrary.razor.
builder.Services.AddScoped<LessonLibraryService>();

// --- Rank badge images (functional.md FR-014) — Admin uploads one image per rank at
// /administration/rank-badges; shown on the parent dashboard via RankBadgeEndpoint.
builder.Services.AddScoped<RankBadgeService>();

// --- Guardian invite issuance (STATE.md "Identity" item 5) — must be registered before
// GuardianLinkService below, which now depends on it.
builder.Services.AddScoped<GuardianInviteService>();

// --- Guardian-link creation (functional.md FR-004, data-design.md "Guardian link verification")
builder.Services.AddScoped<GuardianLinkService>();

// --- Student registration/enrolment (data-design.md Student/StudentRankProgress, STATE.md) —
// creates the Student, opens their first StudentRankProgress row, and creates a guardian link via
// GuardianLinkService above (reused, not duplicated).
builder.Services.AddScoped<StudentRegistrationService>();

// --- Parent registration applications with two-stage approval (functional.md FR-004 as amended
// 22/10/2026, adr/0008 answers 1, 4, 5, 6): any signed-in adult applies; a Safeguarding Officer
// reviews (stage 1), an Admin approves (stage 2), which alone creates the Student, link, consent and
// Parent role. Emails after each save, never throwing.
builder.Services.AddScoped(sp => new RegistrationNotificationService(
    sp.GetRequiredService<VlmsDbContext>(), sp.GetRequiredService<INotificationService>(),
    sp.GetRequiredService<ILogger<RegistrationNotificationService>>(), builder.Configuration["App:PublicBaseUrl"]));
builder.Services.AddScoped<StudentApplicationService>();

// --- Consent/DBS management UI (STATE.md, functional.md FR-001/FR-002, data-design.md
// ConsentRecord/ConsentSensitiveDetails/DbsCheck) — Admin/SafeguardingOfficer only, never the
// Approver role (curriculum-only, CLAUDE.md Project Law).
// Emails on submission and decision (FR-003, adr/0008 decision 3); the services take it optionally.
builder.Services.AddScoped(sp => new ConsentNotificationService(
    sp.GetRequiredService<VlmsDbContext>(), sp.GetRequiredService<INotificationService>(),
    sp.GetRequiredService<ILogger<ConsentNotificationService>>(), builder.Configuration["App:PublicBaseUrl"]));
builder.Services.AddScoped<ConsentRecordService>();
builder.Services.AddScoped<DbsCheckService>();

// --- Parent consent form and teacher read-only consent view (functional.md FR-015,
// adr/0006-parent-consent-and-teacher-read-access.md). Each checks its own role, and the Parent one
// also the caller's StudentGuardianLink, inside the service.
builder.Services.AddScoped<ParentConsentService>();
builder.Services.AddScoped<StudentConsentViewService>();

// --- Parent dashboard (STATE.md, functional.md "Parent engagement") — scoped to only the caller's
// own StudentGuardianLink-linked students, via ParentGuardianLinkage (shared with
// ParentStudentAccessHandler below).
builder.Services.AddScoped<ParentDashboardService>();
builder.Services.AddScoped<StudentRosterService>();

// --- Attendance registers (functional.md FR-020, FR-022): one per class per meeting day; the
// chapter's Safeguarding Officers are told when a child without consent in force is marked present.
builder.Services.AddScoped<AttendanceService>();
builder.Services.AddScoped<ClassTeacherService>();

// --- Calendar (functional.md FR-022): the liturgical calendar (imported by Vlms.Jobs), the
// chapter's events, and each person's private subscribe link.
builder.Services.AddScoped<CalendarService>();
builder.Services.AddScoped<AddedSaintService>();
builder.Services.AddScoped<CalendarFeedService>();
builder.Services.AddScoped(sp => new AttendanceNotificationService(
    sp.GetRequiredService<VlmsDbContext>(), sp.GetRequiredService<ICurrentUserContext>(), sp.GetRequiredService<INotificationService>(),
    sp.GetRequiredService<ILogger<AttendanceNotificationService>>(), builder.Configuration["App:PublicBaseUrl"]));

// --- Student sign-ins and the student page (functional.md FR-011, adr/0007-student-sign-in-accounts.md).
// A linked parent creates a username-and-password sign-in with no email, and resets it (Admins can
// also reset); StudentPageService resolves the caller's own Student record, never one from the URL.
builder.Services.AddScoped<StudentAccountService>();
builder.Services.AddScoped<StudentPageService>();

// --- Admin reporting (STATE.md "Reporting screens: core progress stats + at-risk flagging",
// functional.md "Reporting (MVP)") — Admin-only (see ProgressReportingService's doc comment for
// why this isn't the Admin-or-SafeguardingOfficer pattern the consent/DBS pages use). At-risk
// flagging reuses AtRiskStudentFlagging, the same computation ConsentExpiryJob's daily sweep uses.
builder.Services.AddScoped<ProgressReportingService>();

// --- "Records due for deletion" report (STATE.md, data-design.md "Deletion enforcement — interim
// decision", security-compliance.md "Retention") — Admin-only, same reasoning as
// ProgressReportingService above (see RecordsDueForDeletionService's own doc comment). Reuses the
// RetentionFlagging helper ConsentExpiryJob's daily sweep also now calls.
builder.Services.AddScoped<RecordsDueForDeletionService>();

// --- Manual "leaving" flag for students and teachers (STATE.md "Manual 'leaving' flag..."),
// feeding RetentionFlagging above — Admin-only, no self-service path (see LeavingService's own doc
// comment for the schema-placement judgement call on AppUser.HasLeft/LeftAt).
builder.Services.AddScoped<LeavingService>();

// --- Admin student administration (FR-013: assign teacher, change status) — Admin-only; delegates
// Active-to-Inactive to LeavingService (see StudentAdministrationService's doc comment).
builder.Services.AddScoped<StudentAdministrationService>();

// --- Progress tracking (docs/design/low-level-design.md "CompletionService"/"PromotionService"/
// "CertificateService", STATE.md "Teacher-facing reverse a completion flow") — first wired into
// Vlms.Web here: Progress/CompletionReversals.razor (the Teacher-facing reversal page) is the first
// UI consumer of CompletionService, which is exactly the moment progress-tracking.md's own doc
// comment on LocalDiskBlobStorage named as the trigger to finally wire IBlobStorage/
// CertificateService/PromotionService into DI (previously "structurally correct, not yet wired" —
// no page referenced any of them). "Storage:LocalRootPath" mirrors the "App" section above:
// a local-dev default (LocalDiskBlobStorage.DefaultRootPath, App_Data/blobs) when unset, overridden
// for a real deployment path via appsettings.json/an environment variable. Resolved here, at startup,
// so Production refuses a blank, relative or in-app-directory root (BlobStorageRoot's doc comment).
var blobRootPath = BlobStorageRoot.Resolve(
    builder.Configuration[BlobStorageRoot.RootPathKey],
    requireExternalRoot: builder.Environment.IsProduction(),
    builder.Environment.ContentRootPath, AppContext.BaseDirectory);
builder.Services.AddScoped<IBlobStorage>(_ => new LocalDiskBlobStorage(blobRootPath));
builder.Services.AddScoped<PromotionService>();
builder.Services.AddScoped<CertificateService>();
// Certificate view/download (FR-010): CertificateEndpoint, with its own role and link checks.
builder.Services.AddScoped<CertificateAccessService>();
// Proactive parent emails on completion/promotion (BR-005, GuardianNotificationService's doc comment);
// CompletionService takes it as an optional dependency. The dashboard link uses App:PublicBaseUrl.
builder.Services.AddScoped(sp => new GuardianNotificationService(
    sp.GetRequiredService<VlmsDbContext>(), sp.GetRequiredService<ICurrentUserContext>(),
    sp.GetRequiredService<INotificationService>(), sp.GetRequiredService<ILogger<GuardianNotificationService>>(),
    builder.Configuration["App:PublicBaseUrl"]));
builder.Services.AddScoped<CompletionService>();

// --- Notifications (adr/0001-technology-stack.md, low-level-design.md "NotificationService") —
// wired into Vlms.Web itself, not just Vlms.Jobs (STATE.md "Identity" item 5: guardian invite
// emails are sent from the web app, at the moment the Admin/Teacher creates the link — there is no
// batch/job context to defer this to, unlike ConsentExpiryNotifier's daily-sweep digest). Sent via
// Microsoft Graph as noreply@varangians.co.uk (adr/0005 amendment, 12/10/2026) — see
// GraphEmailSender; the "GraphMail" section, with the client secret supplied only by environment.
// Development is the exception (Philip's decision, 22/10/2026): the development machine holds no
// real GraphMail:ClientSecret, so Development writes each email to a folder instead
// (PickupDirectoryEmailSender, Mail:PickupDirectory). Every other environment is unchanged.
if (builder.Environment.IsDevelopment())
{
    builder.Services.AddSingleton<IEmailSender>(sp => new PickupDirectoryEmailSender(
        PickupDirectoryEmailSender.ResolveDirectory(sp.GetRequiredService<IConfiguration>()),
        sp.GetRequiredService<ILogger<PickupDirectoryEmailSender>>()));
}
else
{
    builder.Services.AddSingleton<IEmailSender>(sp =>
        new GraphEmailSender(GraphEmailOptions.FromConfiguration(sp.GetRequiredService<IConfiguration>())));
}
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

    // Lesson library (FR-006): any of Teacher/Approver/Admin/Student/Parent, never SafeguardingOfficer alone.
    options.AddLessonLibraryPolicy();
    // Teacher's Information pictures (adr/0012 decision 5): Teacher, Admin or National Administrator only.
    options.AddTeachersInformationPolicy();
    // Lesson suggestions (FR-007, adr/0013): Teacher or Approver to suggest; Approver or Admin to read the history.
    options.AddLessonSuggestionPolicies();

    // Certificate view/download (FR-010): Student/Parent/Teacher/Admin; the service checks ownership or link.
    options.AddCertificateAccessPolicy();

    options.AddPolicy("StudentAccess", policy =>
    {
        policy.Requirements.Add(new StudentAccessRequirement());
        policy.Requirements.Add(new MfaRequirement());
    });

    // Any signed-in user, with the same MFA rule as every other policy (Admin, Safeguarding Officer
    // and Teacher must have MFA). Used by the parent registration application pages, which an adult
    // with no role yet must reach (adr/0008 answer 1); the service refuses student sign-ins and leavers.
    options.AddPolicy("RequireSignedIn", policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.Requirements.Add(new MfaRequirement());
    });

    // The teacher's student list (FR-009): Admin or Teacher. Registering students and creating
    // guardian links are Admin only since 22/10/2026 (adr/0008 answer 4) and use RequireAdmin.
    options.AddPolicy("RequireAdminOrTeacher", policy =>
    {
        policy.Requirements.Add(new AnyRoleRequirement(Role.Admin, Role.Teacher));
        policy.Requirements.Add(new MfaRequirement());
    });

    // Reading attendance (chapters-and-attendance.md section 7, 27/10/2026): the registers list, the
    // student list and the student record. Admin, Teacher or Safeguarding Officer; never Approver.
    options.AddPolicy("RequireAttendanceReader", policy =>
    {
        policy.Requirements.Add(new AnyRoleRequirement(Role.Admin, Role.Teacher, Role.SafeguardingOfficer));
        policy.Requirements.Add(new MfaRequirement());
    });

    // The calendar (FR-022): anyone holding a role in the chapter they are working in.
    options.AddPolicy("RequireChapterMember", policy =>
    {
        policy.Requirements.Add(new AnyRoleRequirement(Role.Admin, Role.Teacher, Role.SafeguardingOfficer, Role.Parent, Role.Student));
        policy.Requirements.Add(new MfaRequirement());
    });

    // Consent/DBS management pages (STATE.md, adr/0004-sensitive-data-access-control.md): matches
    // the VlmsDbContext query filter on DbsCheck exactly — Admin or SafeguardingOfficer, never
    // Approver (curriculum-only) or Teacher. ConsentSensitiveDetails is also readable by Teachers
    // and linked Parents since adr/0006, but only through their own pages (/students/consent,
    // /parent/consent); managing and deciding consent stays with these two roles.
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

// Sign-in policy (confirmed email required, lockout) — see VlmsIdentityPolicy's doc comment.
// VlmsUserConfirmation keeps the confirmed-email rule for every account with an email, and treats
// an emailless account as confirmed only when it is a student's parent-created sign-in (adr/0007).
// Student sign-ins (adr/0007): VlmsPasswordValidator keeps the adult password rules and needs only
// 8+ characters for a student; VlmsSignInManager gives a student a persistent, 90-day sliding cookie
// (adults unchanged). Disabled sign-ins fail VlmsUserConfirmation.
builder.Services.AddIdentityCore<VlmsIdentityUser>(VlmsIdentityPolicy.Apply)
    .AddEntityFrameworkStores<VlmsDbContext>()
    .AddSignInManager<VlmsSignInManager>()
    .AddDefaultTokenProviders()
    .AddUserConfirmation<VlmsUserConfirmation>()
    .AddVlmsPasswordRules();
builder.Services.Configure<DataProtectionTokenProviderOptions>(options =>
    options.TokenLifespan = VlmsIdentityPolicy.EmailTokenLifespan);
// Cookie security-stamp check for static SSR requests, explicit rather than the 30-minute default
// (FR-016; see VlmsIdentityPolicy's doc comment). The circuit's revalidation uses the same interval.
// Sliding cookie expiry, set explicitly: the student "stay signed in" window (adr/0007) relies on it.
builder.Services.ConfigureApplicationCookie(options => options.SlidingExpiration = true);
builder.Services.Configure<SecurityStampValidatorOptions>(options =>
    options.ValidationInterval = VlmsIdentityPolicy.SecurityStampValidationInterval);
builder.Services.AddScoped<AccountEmailService>();
builder.Services.AddScoped<PasswordChangeService>();

var app = builder.Build();

// Configure the HTTP request pipeline.

// --- Forwarded Headers Middleware (deploy/Caddyfile, adr/0005-hosting-pivot-self-hosted-vps-postgresql.md:
// "Kestrel behind Caddy") — required, not optional, whenever Kestrel sits behind a TLS-terminating
// reverse proxy on the same box: without it, every request reaches Kestrel as plain HTTP (Caddy
// talks to Kestrel over loopback HTTP per vlms-web.service's ASPNETCORE_URLS), so Request.IsHttps
// is false even though the browser's own connection to Caddy was HTTPS — which would make
// UseHttpsRedirection() below redirect every already-HTTPS request, and would make ASP.NET Core
// Identity's auth cookie (CookieAuthenticationOptions.Cookie.SecurePolicy, default SameAsRequest)
// get issued without the Secure flag in production, a real regression for an app that handles
// safeguarding data. Verified against Microsoft's current documentation rather than assumed
// (learn.microsoft.com/aspnet/core/host-and-deploy/proxy-load-balancer): must run before UseHsts()/
// UseHttpsRedirection() so Request.Scheme is corrected before either inspects it; the exact
// deployment topology this ADR describes — a single reverse proxy, on the same host, reachable
// only via loopback — is precisely the "default KnownNetworks/KnownProxies (loopback only)"
// scenario that page names as needing no extra KnownProxies configuration, so none is added here.
// (ForwardedHeaders defaults to None — the desired headers must be set explicitly, per the same
// page.) This line has no effect in Development (nothing proxies to it there, so the headers are
// simply never present) and is unconditional rather than gated to non-Development for that reason.
app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
});

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // Max-age comes from WebHardening (Hsts:MaxAgeDays, default 365 days).
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

// After UseForwardedHeaders, so limits partition on the real client IP. WebApplication's implicit
// routing runs first, so the account pages' [EnableRateLimiting] metadata is visible here.
app.UseRateLimiter();

app.UseAuthentication();
// Between authentication and authorization: the authorization handlers resolve the caller through
// AuthenticationStateProvider (see AuthenticationStatePriming's doc comment).
app.UseAuthenticationStatePriming();
app.UseAuthorization();

app.UseAntiforgery();

app.MapStaticAssets();
app.MapVlmsHealth();
app.MapLessonFileEndpoint();
app.MapSavedLessonsEndpoint();
app.MapLessonPageEndpoints();
app.MapRankBadgeEndpoint();
app.MapCertificateEndpoint();
app.MapCalendarFeedEndpoint();
app.MapLessonSuggestionEndpoints();
app.MapChapterSwitchEndpoint();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

// Non-interactive endpoints Identity's own sign-out needs (writing/clearing the auth cookie, which
// an interactive Razor component cannot do — see IdentityComponentsEndpointRouteBuilderExtensions's
// doc comment).
app.MapAdditionalIdentityEndpoints();

app.Run();
