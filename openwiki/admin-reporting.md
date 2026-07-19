# Admin reporting

Design source: `docs/requirements/functional.md` "Reporting (MVP)" ("Core progress reports: rank/completion stats, promotion history." / "At-risk/disengaged student flagging: no lesson completions within 8 weeks, surfaced to Admin for proactive follow-up."), `docs/quality/test-plan.md` TC-010, VISION.md's mission statement ("gives the Admin core progress and at-risk reporting"). Implements STATE.md's "Reporting screens: core progress stats + at-risk flagging" item.

## `ProgressReportingService` (`src/Vlms.Infrastructure/Reporting/ProgressReportingService.cs`)

Admin-facing, read-only aggregation service backing a single Blazor page — same shape as `Engagement.ParentDashboardService` (see [parent-dashboard.md](parent-dashboard.md)), but Admin-facing rather than Parent-facing. Two methods, both role-checked inside the service itself (defense in depth, same pattern as every other service in this codebase):

- **`GetProgressStatsAsync()` -> `ProgressStatsReport`** — exactly the metrics functional.md's "Reporting (MVP)" sentence names, nothing invented beyond it:
  - `StudentsByRank` (`RankStudentCount[]`) — Active-student headcount per `Rank`, ordered by `Rank.Order`. A rank with zero active students still appears with count 0 (proved by a dedicated test assertion, not just omitted).
  - `StatusCounts` (`StudentStatusCounts`) — totals by `Student.Status` (Active/Inactive/Graduated).
  - `TotalLessonCompletions` — count of non-reversed `StudentLessonCompletion` rows.
  - `PromotionHistory` (`PromotionHistoryEntry[]`) — one row per closed `StudentRankProgress` (`CompletedAt != null`, i.e. "the student completed that rank" — data-design.md's own description of what closing a row means), newest first. An open row (current rank, not yet completed) is not a promotion event and doesn't appear.
- **`GetAtRiskStudentsAsync()` -> `IReadOnlyList<AtRiskStudentFlag>`** — see below.

**Role scope, a documented judgement call:** gated `RequireAdmin` (the existing single-role policy), *not* the `RequireAdminOrSafeguardingOfficer` pattern the consent/DBS pages use (see [safeguarding-consent.md](safeguarding-consent.md)). VISION.md names the viewer explicitly as Admin, and functional.md's at-risk wording says "surfaced to Admin", never SafeguardingOfficer. Disengagement (no completions) is a distinct concern from a safeguarding-document lapse (expired consent/DBS), and nothing in the docs extends SafeguardingOfficer's remit to it — unlike FR-001/FR-002, which explicitly name both roles throughout. Touches no ADR-0004-restricted entity (`DbsCheck`/`ConsentSensitiveDetails`) at all, so the query filter/read-audit interceptor are simply irrelevant here, not bypassed.

## `AtRiskStudentFlagging` (`src/Vlms.Infrastructure/Reporting/AtRiskStudentFlagging.cs`)

The 8-week disengagement-flag computation, **extracted out of `Safeguarding.ConsentExpiryJob`** during this increment so it has exactly one implementation shared by both callers, rather than the new reporting screen carrying a second copy of the same window arithmetic:

- `ConsentExpiryJob.RunAsync()` (the daily WebJob sweep, unchanged behaviour — see [safeguarding-consent.md](safeguarding-consent.md)) now calls `AtRiskStudentFlagging.GetAtRiskStudentsAsync(_db, now, ct)` instead of a private method it used to own.
- `ProgressReportingService.GetAtRiskStudentsAsync()` calls the same static method, after its own `RequireAdmin` check.

`AtRiskStudentFlag`/`AtRiskThresholdDays` (56 days, functional.md/TC-010) moved here too, out of `ConsentExpiryJob.cs`. The static method deliberately has **no role check of its own** — the same "shared helper, caller-owned authorization" shape as `Authorization.ParentGuardianLinkage` (see [parent-dashboard.md](parent-dashboard.md)): the two current callers legitimately gate on different role sets for the same underlying query (Admin-or-SafeguardingOfficer vs Admin-only), so baking a single check into the shared helper would make one of them wrong.

A dedicated test (`ProgressReportingServiceTests.GetAtRiskStudentsAsync_ProducesTheSamePopulation_AsConsentExpiryJobsSweep`) seeds the same disengagement scenario `ConsentExpiryJobTests` uses and asserts both `ConsentExpiryJob.RunAsync()` and `ProgressReportingService.GetAtRiskStudentsAsync()` return the identical flagged population (same student IDs, `LastActivityAt`, `DaysSinceLastActivity`) — proving the shared implementation, not just duplicating the scenario.

## Page

`src/Vlms.Web/Components/Pages/Reporting/ProgressReporting.razor` (`/reporting`), gated by `[Authorize(Policy = "RequireAdmin")]`, linked from `Home.razor`. Shows students-by-rank, status totals, the completion total, the promotion history table, and the at-risk table — read-only, no forms.

## Tests

`tests/Vlms.Tests/Infrastructure/ProgressReportingServiceTests.cs` (SQLite-in-memory-via-DI, same pattern as `ParentDashboardServiceTests`): a multi-student/multi-rank scenario asserting exact rank/status counts, completion total, and ordered promotion history; the at-risk/`ConsentExpiryJob`-equivalence test described above; and role-gating denial (both methods) for every non-Admin role.

No EF Core schema/migration changes — pure read-side aggregation over `Student`/`Rank`/`StudentRankProgress`/`StudentLessonCompletion`, all of which already existed (`dotnet ef migrations has-pending-model-changes` confirmed no drift).
