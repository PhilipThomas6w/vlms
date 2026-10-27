// Vlms.Jobs — the scheduled-job host for ConsentExpiryJob
// (docs/adr/0003-scheduled-jobs-webjobs.md, docs/design/low-level-design.md "ConsentExpiryJob").
//
// This process runs once and exits — the schedule lives entirely outside the process, in a systemd
// timer (deploy/systemd/vlms-jobs.timer), not a timer loop in here. All the actual sweep/flag/
// escalation logic lives in Vlms.Infrastructure.Safeguarding.ConsentExpiryJob, which is fully
// unit-tested independently of this host (tests/Vlms.Tests/Infrastructure/ConsentExpiryJobTests.cs)
// — this file's only job is wiring: configuration, a VlmsDbContext, the SystemCurrentUserContext (no
// signed-in human caller exists for a scheduled job), and console logging (captured by
// systemd/journald — see deploy/systemd/README.md's monitoring note, journalctl -u vlms-jobs.service
// replaces the Application Insights alerting the original ADR named for its previous hosting model).
// Since STATE.md's NotificationService item, it also wires ConsentExpiryNotifier and calls it
// straight after RunAsync — closing the loop ConsentExpiryJob's own doc comment named ("once
// NotificationService exists it can call RunAsync and translate the returned ConsentExpiryJobResult
// into real emails").
//
// Deployment (not performed by this codebase/build/verify.ps1 — no live VPS environment exists yet,
// same "structurally correct, not yet wired" status as LocalDiskBlobStorage/IdentityCurrentUserContext
// before their dependencies existed): docs/adr/0005-hosting-pivot-self-hosted-vps-postgresql.md
// retargets this project's deployment from the original WebJobs folder convention
// (App_Data/jobs/triggered/...) to a systemd timer/service pair on the self-hosted VPS —
// deploy/systemd/vlms-jobs.service + deploy/systemd/vlms-jobs.timer, invoking this same published
// console app unchanged; adr/0003's "plain console app, not a scheduler SDK" reasoning is unaffected,
// only *where* the schedule lives changes. Email sends via Microsoft Graph as
// noreply@varangians.co.uk (adr/0005 amendment) — the "GraphMail" section in appsettings.json, with
// the client secret supplied only by /etc/vlms/jobs.env.

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Vlms.Domain;
using Vlms.Infrastructure;
using Vlms.Infrastructure.Calendar;
using Vlms.Infrastructure.Chapters;
using Vlms.Infrastructure.Curriculum;
using Vlms.Infrastructure.Engagement;
using Vlms.Infrastructure.Identity;
using Vlms.Infrastructure.Monitoring;
using Vlms.Infrastructure.Notifications;
using Vlms.Infrastructure.Retention;
using Vlms.Infrastructure.Safeguarding;
using Vlms.Infrastructure.Security;
using Vlms.Infrastructure.Storage;

// `Vlms.Jobs bootstrap-admin <email>` — one-off first-Admin bootstrap (STATE.md "Admin role
// assignment + first-Admin bootstrap"; see FirstAdminBootstrapService's doc comment for why it lives
// here and never in Vlms.Web). Any other invocation runs the scheduled sweep as before. The bootstrap
// arguments are kept out of the host builder so they are never read as configuration.
var bootstrapEmail = args is ["bootstrap-admin", var emailArg] ? emailArg : null;

// `Vlms.Jobs bootstrap-national-admin <email>` — the same one-off pattern for the first National
// Administrator (FR-021, docs/adr/0010-chapters.md), inert once one exists.
var bootstrapNationalEmail = args is ["bootstrap-national-admin", var nationalEmailArg] ? nationalEmailArg : null;

// `Vlms.Jobs seed-dev-chapters --password <password>` — Development only: the Coventry test
// chapter with its own staff, parent and student (DevelopmentChapterSeeder).
var seedDevPassword = args is ["seed-dev-chapters", "--password", var passwordArg] ? passwordArg : null;

// `Vlms.Jobs seed-curriculum --manifest <path> --content-root <dir>` — idempotent rank ladder +
// curriculum seed (STATE.md "Seed rank ladder + curriculum"; CurriculumSeeder's doc comment). Run once
// after migrations, with the production connection string and Storage:LocalRootPath.
var seedCurriculumArgs = args is ["seed-curriculum", ..] ? SeedCurriculumArgs.Parse(args[1..]) : null;

// `Vlms.Jobs import-calendar` — import the liturgical calendar now, without the rest of the daily
// run (it is also part of every daily run, below).
var importCalendarOnly = args is ["import-calendar"];

// `Vlms.Jobs retention-monthly` — the monthly retention run (docs/adr/0014-retention-purge.md
// decisions 5 and 6), started by deploy/systemd/vlms-retention.timer: the reminder to each chapter's
// Admins while records are due, then the audit-log purge as the vlms_retention database role.
var retentionMonthly = args is ["retention-monthly"];

// `Vlms.Jobs alert-digest` — emails the journal's ESCALATION: lines and newly failed vlms-* units since
// the last successful run to Alerts:Recipient (docs/operations/monitoring.md §3). Started by
// vlms-alert.timer each morning and by OnFailure= on every vlms-* unit (deploy/systemd/vlms-alert.service).
var alertDigestOnly = args is ["alert-digest"];

var isCommand = bootstrapEmail is not null || bootstrapNationalEmail is not null || seedDevPassword is not null
    || seedCurriculumArgs is not null || importCalendarOnly || retentionMonthly || alertDigestOnly;
var builder = Host.CreateApplicationBuilder(isCommand ? [] : args);

builder.Services.AddDbContext<VlmsDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("VlmsDatabase")));

// No signed-in human caller for a scheduled job — see SystemCurrentUserContext's doc comment for
// exactly which two roles it grants and why (only what the sensitive-data query filter needs).
builder.Services.AddSingleton<ICurrentUserContext>(SystemCurrentUserContext.Instance);

// ConsentExpiryJob's own doc comment: low-level-design.md calls the consent/DBS expiry warning
// window "configurable" but names no number, so 28 days is a documented build-time default — bound
// here from Safeguarding:ExpiryWarningWindowDays (appsettings.json, or the
// Safeguarding__ExpiryWarningWindowDays environment variable from /etc/vlms/jobs.env) so the project owner can actually override it without a recompile, per the ADR's own
// wording. AddScoped<ConsentExpiryJob>() alone would resolve the constructor's int parameter to 0,
// not the intended default, since DI has no registration for a bare int — so the value is read
// explicitly and passed through this factory instead.
builder.Services.AddScoped(sp => new ConsentExpiryJob(
    sp.GetRequiredService<VlmsDbContext>(),
    sp.GetRequiredService<ICurrentUserContext>(),
    sp.GetRequiredService<ILogger<ConsentExpiryJob>>(),
    builder.Configuration.GetValue("Safeguarding:ExpiryWarningWindowDays", ConsentExpiryJob.DefaultExpiryWarningWindowDays)));

// --- Notifications (STATE.md, low-level-design.md "NotificationService") — Microsoft Graph as
// noreply@varangians.co.uk (adr/0005 amendment, 12/10/2026); see GraphEmailSender.
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
builder.Services.AddScoped<ConsentExpiryNotifier>();
// Parent consent-expiry reminders (BR-005; GuardianNotificationService's doc comment for the schedule).
builder.Services.AddScoped(sp => new GuardianNotificationService(
    sp.GetRequiredService<VlmsDbContext>(), sp.GetRequiredService<ICurrentUserContext>(),
    sp.GetRequiredService<INotificationService>(), sp.GetRequiredService<ILogger<GuardianNotificationService>>(),
    builder.Configuration["App:PublicBaseUrl"]));
builder.Services.AddScoped<FirstAdminBootstrapService>();

// Accounts for the Development seed only (seed-dev-chapters): Identity's user store and the real
// password rules, as Vlms.Web registers them.
builder.Services.AddIdentityCore<VlmsIdentityUser>()
    .AddEntityFrameworkStores<VlmsDbContext>()
    .AddVlmsPasswordRules();
builder.Services.AddScoped<AppUserProvisioningService>();
builder.Services.AddScoped<DevelopmentChapterSeeder>();

// Lesson files for seed-curriculum go to the same root the web app reads (Storage:LocalRootPath).
// Resolved lazily so the scheduled sweep never needs it; Production refuses a blank, relative or
// in-app-directory root (BlobStorageRoot).
builder.Services.AddScoped<IBlobStorage>(sp => new LocalDiskBlobStorage(BlobStorageRoot.Resolve(
    sp.GetRequiredService<IConfiguration>()[BlobStorageRoot.RootPathKey],
    sp.GetRequiredService<IHostEnvironment>().IsProduction(),
    AppContext.BaseDirectory)));
builder.Services.AddScoped<CurriculumSeeder>();

// The liturgical calendar (FR-022): the New Calendar feed, address from Calendar:LiturgicalFeedUrl.
// One HttpClient for the process, which runs once and exits.
builder.Services.AddSingleton(_ => new HttpClient { Timeout = TimeSpan.FromMinutes(2) });
builder.Services.AddScoped(sp => new LiturgicalCalendarImporter(
    sp.GetRequiredService<VlmsDbContext>(), sp.GetRequiredService<ICurrentUserContext>(), sp.GetRequiredService<HttpClient>(),
    sp.GetRequiredService<ILogger<LiturgicalCalendarImporter>>(), builder.Configuration[LiturgicalCalendarImporter.FeedUrlKey]));

// The alert digest (docs/operations/monitoring.md §3): reads journalctl/systemctl, mails through the
// IEmailSender above, and keeps its last-run time in a small file (Alerts:StatePath, by default under the
// unit's STATE_DIRECTORY).
builder.Services.AddSingleton<IProcessRunner, ProcessRunner>();
builder.Services.AddSingleton<ISystemStatusSource, SystemdStatusSource>();
builder.Services.AddSingleton(sp => AlertDigestOptions.FromConfiguration(
    sp.GetRequiredService<IConfiguration>(), AppContext.BaseDirectory));
builder.Services.AddSingleton<IAlertStateStore>(sp =>
    new FileAlertStateStore(sp.GetRequiredService<AlertDigestOptions>().StatePath));
builder.Services.AddSingleton<AlertDigestService>();

using var host = builder.Build();

using var scope = host.Services.CreateScope();

if (bootstrapEmail is not null)
{
    var bootstrapLogger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    try
    {
        var admin = await scope.ServiceProvider.GetRequiredService<FirstAdminBootstrapService>().BootstrapAsync(bootstrapEmail);
        bootstrapLogger.LogInformation(
            "First Admin bootstrapped: AppUser {UserId} ({DisplayName}). They must enrol TOTP MFA before Admin pages open.",
            admin.Id, admin.DisplayName);
    }
    catch (InvalidOperationException ex)
    {
        bootstrapLogger.LogError("First-Admin bootstrap refused: {Reason}", ex.Message);
        Environment.ExitCode = 1;
    }

    return;
}

if (bootstrapNationalEmail is not null)
{
    var nationalLogger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    try
    {
        var national = await scope.ServiceProvider.GetRequiredService<FirstAdminBootstrapService>()
            .BootstrapNationalAdministratorAsync(bootstrapNationalEmail);
        nationalLogger.LogInformation(
            "First National Administrator bootstrapped: AppUser {UserId} ({DisplayName}). They must enrol TOTP MFA before the chapters page opens.",
            national.Id, national.DisplayName);
    }
    catch (InvalidOperationException ex)
    {
        nationalLogger.LogError("National Administrator bootstrap refused: {Reason}", ex.Message);
        Environment.ExitCode = 1;
    }

    return;
}

if (seedDevPassword is not null)
{
    var devLogger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    if (!builder.Environment.IsDevelopment())
    {
        devLogger.LogError("seed-dev-chapters refused: it runs only in the Development environment (DOTNET_ENVIRONMENT=Development).");
        Environment.ExitCode = 2;
        return;
    }

    try
    {
        var seeded = await scope.ServiceProvider.GetRequiredService<DevelopmentChapterSeeder>().SeedAsync(seedDevPassword);
        devLogger.LogInformation(
            "seed-dev-chapters done: chapter {ChapterId} ({Created}); accounts created: {Accounts}.",
            seeded.ChapterId, seeded.ChapterCreated ? "created" : "already there",
            seeded.AccountsCreated.Count == 0 ? "none" : string.Join(", ", seeded.AccountsCreated));
    }
    catch (Exception ex)
    {
        devLogger.LogCritical(ex, "seed-dev-chapters failed.");
        Environment.ExitCode = 1;
    }

    return;
}

if (seedCurriculumArgs is not null)
{
    var seedLogger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    if (seedCurriculumArgs.Error is not null)
    {
        seedLogger.LogError("seed-curriculum: {Error}", seedCurriculumArgs.Error);
        Environment.ExitCode = 2;
        return;
    }

    try
    {
        var seed = await scope.ServiceProvider.GetRequiredService<CurriculumSeeder>()
            .SeedAsync(seedCurriculumArgs.ManifestPath!, seedCurriculumArgs.ContentRoot!, default,
                seedCurriculumArgs.FileNamesPath, seedCurriculumArgs.NamesOnly);
        foreach (var warning in seed.Warnings)
        {
            seedLogger.LogWarning("seed-curriculum: {Warning}", warning);
        }

        if (!seed.Succeeded)
        {
            seedLogger.LogError("seed-curriculum refused: {ErrorCount} validation error(s); nothing was written.", seed.Errors.Count);
            foreach (var error in seed.Errors)
            {
                seedLogger.LogError("  {Error}", error);
            }
            Environment.ExitCode = 1;
            return;
        }

        seedLogger.LogInformation(
            "seed-curriculum done: ranks {RanksCreated} created / {RanksUpdated} updated; lessons {LessonsCreated} created / {LessonsUpdated} updated; {FilesUploaded} file(s) uploaded; {FileNamesAdded} file-name list entr(ies) added, {FilesRenamed} file(s) renamed; {LessonTextsUpdated} lesson text(s) updated, {PicturesUploaded} picture(s) uploaded.",
            seed.RanksCreated, seed.RanksUpdated, seed.LessonsCreated, seed.LessonsUpdated, seed.FilesUploaded, seed.FileNamesAdded, seed.FilesRenamed, seed.LessonTextsUpdated, seed.PicturesUploaded);
    }
    catch (Exception ex)
    {
        seedLogger.LogCritical(ex, "seed-curriculum failed.");
        Environment.ExitCode = 1;
    }

    return;
}

if (alertDigestOnly)
{
    var digest = await scope.ServiceProvider.GetRequiredService<AlertDigestService>().RunAsync(DateTimeOffset.UtcNow);
    if (digest == AlertDigestOutcome.Failed)
    {
        Environment.ExitCode = 1;
    }

    return;
}

if (importCalendarOnly)
{
    await ImportCalendarAsync(scope.ServiceProvider);
    return;
}

if (retentionMonthly)
{
    var retentionLogger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    try
    {
        var reminders = await new RetentionReminderService(
            scope.ServiceProvider.GetRequiredService<VlmsDbContext>(), SystemCurrentUserContext.Instance,
            scope.ServiceProvider.GetRequiredService<INotificationService>(), builder.Configuration["App:PublicBaseUrl"]).SendAsync();
        retentionLogger.LogInformation("Retention reminder sent {ReminderCount} email(s).", reminders.Count);
    }
    catch (Exception ex)
    {
        retentionLogger.LogCritical(ex, "Retention reminder failed.");
        Environment.ExitCode = 1;
    }

    // The purge connects as vlms_retention (ConnectionStrings:AuditLogPurge, in /etc/vlms/jobs.env
    // only), never as vlms_app, which cannot delete audit rows at all.
    var purgeConnection = builder.Configuration.GetConnectionString(AuditLogPurgeService.ConnectionStringName);
    if (string.IsNullOrWhiteSpace(purgeConnection))
    {
        retentionLogger.LogCritical("Audit-log purge not run: ConnectionStrings:{Name} is not configured.", AuditLogPurgeService.ConnectionStringName);
        Environment.ExitCode = 1;
        return;
    }

    try
    {
        await using var purgeDb = new VlmsDbContext(
            new DbContextOptionsBuilder<VlmsDbContext>().UseNpgsql(purgeConnection).Options, SystemCurrentUserContext.Instance);
        var purged = await new AuditLogPurgeService(purgeDb, SystemCurrentUserContext.Instance).PurgeAsync(DateTime.UtcNow);
        retentionLogger.LogInformation("Audit-log purge deleted {RowCount} row(s) older than {Years} years.", purged, AuditLogPurgeService.RetentionYears);
    }
    catch (Exception ex)
    {
        retentionLogger.LogCritical(ex, "Audit-log purge failed.");
        Environment.ExitCode = 1;
    }

    return;
}

var job = scope.ServiceProvider.GetRequiredService<ConsentExpiryJob>();
var notifier = scope.ServiceProvider.GetRequiredService<ConsentExpiryNotifier>();
var guardianNotifications = scope.ServiceProvider.GetRequiredService<GuardianNotificationService>();
var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();

try
{
    var result = await job.RunAsync();
    logger.LogInformation(
        "ConsentExpiryJob completed: {ConsentFlagCount} consent flag(s), {DbsFlagCount} DBS flag(s), {AtRiskCount} at-risk student(s), {StudentsDueForDeletionCount} student(s) due for deletion, {DbsChecksDueForDeletionCount} DBS check(s) due for deletion.",
        result.ConsentFlags.Count, result.DbsFlags.Count, result.AtRiskStudents.Count,
        result.StudentsDueForDeletion.Count, result.DbsChecksDueForDeletion.Count);

    var notificationOutcomes = await notifier.NotifyAsync(result);
    logger.LogInformation(
        "ConsentExpiryNotifier sent {NotificationCount} safeguarding-critical notification(s).",
        notificationOutcomes.Count);

    var reminderOutcomes = await guardianNotifications.SendConsentRemindersAsync(ConsentYear.Today());
    logger.LogInformation("GuardianNotificationService sent {ReminderCount} parent consent reminder(s).", reminderOutcomes.Count);
}
catch (Exception ex)
{
    // adr/0003: "WebJob failures must be monitored explicitly (e.g. Application Insights
    // alerting) since there is no separate Functions-portal execution history to fall back on."
    // A non-zero exit code plus a Critical log entry is what that alerting acts on.
    logger.LogCritical(ex, "ConsentExpiryJob run failed.");
    Environment.ExitCode = 1;
}

// The liturgical calendar import runs after the safeguarding sweep, so a feed outage never delays or
// stops the sweep; a failure is logged and fails the run like any other.
await ImportCalendarAsync(scope.ServiceProvider);

static async Task ImportCalendarAsync(IServiceProvider services)
{
    var calendarLogger = services.GetRequiredService<ILogger<LiturgicalCalendarImporter>>();
    try
    {
        await services.GetRequiredService<LiturgicalCalendarImporter>().ImportAsync();
    }
    catch (Exception ex)
    {
        calendarLogger.LogError(ex, "Liturgical calendar import failed; the calendar keeps the days it already had.");
        Environment.ExitCode = 1;
    }
}
