// Vlms.Jobs — the Azure App Service Triggered WebJob host for ConsentExpiryJob
// (docs/adr/0003-scheduled-jobs-webjobs.md, docs/design/low-level-design.md "ConsentExpiryJob").
//
// This process runs once and exits (Triggered WebJob semantics — the schedule in settings.job
// drives when it runs; the process itself has no timer loop). All the actual sweep/flag/escalation
// logic lives in Vlms.Infrastructure.Safeguarding.ConsentExpiryJob, which is fully unit-tested
// independently of this host (tests/Vlms.Tests/Infrastructure/ConsentExpiryJobTests.cs) — this
// file's only job is wiring: configuration, a VlmsDbContext, the SystemCurrentUserContext (no
// signed-in human caller exists for a scheduled job), and console logging (which Application
// Insights picks up once deployed — see the ADR's monitoring note).
//
// Deployment (not performed by this codebase/build/verify.ps1 — no live Azure environment exists
// yet, same "structurally correct, not yet wired" status as AzureBlobStorage/EntraCurrentUserContext
// before their dependencies existed): this project's publish output is deployed into Vlms.Web's App
// Service under App_Data/jobs/triggered/ConsentExpiryJob/, per the classic Azure WebJobs deployment
// model (learn.microsoft.com/azure/app-service/webjobs-create) — co-located with the same App
// Service plan, per adr/0003.

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Vlms.Domain;
using Vlms.Infrastructure;
using Vlms.Infrastructure.Safeguarding;
using Vlms.Infrastructure.Security;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddDbContext<VlmsDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("VlmsDatabase")));

// No signed-in human caller for a scheduled job — see SystemCurrentUserContext's doc comment for
// exactly which two roles it grants and why (only what the sensitive-data query filter needs).
builder.Services.AddSingleton<ICurrentUserContext>(SystemCurrentUserContext.Instance);

builder.Services.AddScoped<ConsentExpiryJob>();

using var host = builder.Build();

using var scope = host.Services.CreateScope();
var job = scope.ServiceProvider.GetRequiredService<ConsentExpiryJob>();
var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();

try
{
    var result = await job.RunAsync();
    logger.LogInformation(
        "ConsentExpiryJob completed: {ConsentFlagCount} consent flag(s), {DbsFlagCount} DBS flag(s), {AtRiskCount} at-risk student(s).",
        result.ConsentFlags.Count, result.DbsFlags.Count, result.AtRiskStudents.Count);
}
catch (Exception ex)
{
    // adr/0003: "WebJob failures must be monitored explicitly (e.g. Application Insights
    // alerting) since there is no separate Functions-portal execution history to fall back on."
    // A non-zero exit code plus a Critical log entry is what that alerting acts on.
    logger.LogCritical(ex, "ConsentExpiryJob run failed.");
    Environment.ExitCode = 1;
}
