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
// replaces the Application Insights alerting the original ADR named for the Azure WebJobs host).
// Since STATE.md's NotificationService item, it also wires ConsentExpiryNotifier and calls it
// straight after RunAsync — closing the loop ConsentExpiryJob's own doc comment named ("once
// NotificationService exists it can call RunAsync and translate the returned ConsentExpiryJobResult
// into real emails").
//
// Deployment (not performed by this codebase/build/verify.ps1 — no live VPS environment exists yet,
// same "structurally correct, not yet wired" status as LocalDiskBlobStorage/IdentityCurrentUserContext
// before their dependencies existed): docs/adr/0005-hosting-pivot-self-hosted-vps-postgresql.md
// retargets this project's deployment from the Azure App Service WebJobs folder convention
// (App_Data/jobs/triggered/...) to a systemd timer/service pair on the self-hosted VPS —
// deploy/systemd/vlms-jobs.service + deploy/systemd/vlms-jobs.timer, invoking this same published
// console app unchanged; adr/0003's "plain console app, not a scheduler SDK" reasoning is unaffected,
// only *where* the schedule lives changes. Email sends via SMTP
// (adr/0005-hosting-pivot-self-hosted-vps-postgresql.md, superseding Azure Communication Services
// Email) — Smtp:Host/Port/Username/Password/FromAddress/UseStartTls in appsettings.json are
// placeholders, same status AzureAd's/CommunicationServices' config had before a live target existed;
// the org's existing Microsoft 365 mailbox vs. a free-tier transactional provider is still an open
// pick per the ADR.

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Vlms.Domain;
using Vlms.Infrastructure;
using Vlms.Infrastructure.Notifications;
using Vlms.Infrastructure.Safeguarding;
using Vlms.Infrastructure.Security;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddDbContext<VlmsDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("VlmsDatabase")));

// No signed-in human caller for a scheduled job — see SystemCurrentUserContext's doc comment for
// exactly which two roles it grants and why (only what the sensitive-data query filter needs).
builder.Services.AddSingleton<ICurrentUserContext>(SystemCurrentUserContext.Instance);

// ConsentExpiryJob's own doc comment: low-level-design.md calls the consent/DBS expiry warning
// window "configurable" but names no number, so 28 days is a documented build-time default — bound
// here from Safeguarding:ExpiryWarningWindowDays (appsettings.json/Azure App Service Application
// Settings) so the project owner can actually override it without a recompile, per the ADR's own
// wording. AddScoped<ConsentExpiryJob>() alone would resolve the constructor's int parameter to 0,
// not the intended default, since DI has no registration for a bare int — so the value is read
// explicitly and passed through this factory instead.
builder.Services.AddScoped(sp => new ConsentExpiryJob(
    sp.GetRequiredService<VlmsDbContext>(),
    sp.GetRequiredService<ICurrentUserContext>(),
    sp.GetRequiredService<ILogger<ConsentExpiryJob>>(),
    builder.Configuration.GetValue("Safeguarding:ExpiryWarningWindowDays", ConsentExpiryJob.DefaultExpiryWarningWindowDays)));

// --- Notifications (STATE.md, low-level-design.md "NotificationService") — SMTP
// (adr/0005-hosting-pivot-self-hosted-vps-postgresql.md, superseding Azure Communication Services
// Email), wired the same placeholder-config way AzureAd/CommunicationServices were before a live
// target existed. SmtpEmailSender's own doc comment has the verified MailKit API details;
// Username/Password are optional (null skips SMTP AUTH) — see that constructor's guard clause.
builder.Services.AddSingleton<IEmailSender>(sp =>
{
    var configuration = sp.GetRequiredService<IConfiguration>();
    return new SmtpEmailSender(
        configuration.GetValue<string>("Smtp:Host")
            ?? throw new InvalidOperationException("Smtp:Host is not configured."),
        configuration.GetValue("Smtp:Port", 587),
        configuration.GetValue<string>("Smtp:Username"),
        configuration.GetValue<string>("Smtp:Password"),
        configuration.GetValue<string>("Smtp:FromAddress")
            ?? throw new InvalidOperationException("Smtp:FromAddress is not configured."),
        configuration.GetValue("Smtp:UseStartTls", true));
});
builder.Services.AddSingleton<INotificationService, NotificationService>();
builder.Services.AddScoped<ConsentExpiryNotifier>();

using var host = builder.Build();

using var scope = host.Services.CreateScope();
var job = scope.ServiceProvider.GetRequiredService<ConsentExpiryJob>();
var notifier = scope.ServiceProvider.GetRequiredService<ConsentExpiryNotifier>();
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
}
catch (Exception ex)
{
    // adr/0003: "WebJob failures must be monitored explicitly (e.g. Application Insights
    // alerting) since there is no separate Functions-portal execution history to fall back on."
    // A non-zero exit code plus a Critical log entry is what that alerting acts on.
    logger.LogCritical(ex, "ConsentExpiryJob run failed.");
    Environment.ExitCode = 1;
}
