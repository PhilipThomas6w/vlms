# Admin reporting

Design source: `docs/requirements/functional.md` "Reporting (MVP)" ("Core progress reports: rank/completion stats, promotion history." / "At-risk/disengaged student flagging: no lesson completions within 8 weeks, surfaced to Admin for proactive follow-up."), `docs/quality/test-plan.md` TC-010, VISION.md's mission statement ("gives the Admin core progress and at-risk reporting"). Implements STATE.md's "Reporting screens: core progress stats + at-risk flagging" item and, further down this page, STATE.md's "'Records due for deletion' report" item.

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

## "Records due for deletion" report

Design source: `docs/design/data-design.md` "Deletion enforcement — interim decision" ("manual, Admin-triggered deletion once a record passes its retention period (an Admin-facing 'records due for deletion' report from `ConsentExpiryJob`'s wider sweep)") and `docs/governance/security-compliance.md` "Retention" (Student records: 3 years from leaving/graduating; `DbsCheck` records: 6 years after expiry or the teacher leaving; hard delete, not anonymisation). This is a **flagging-only** report — nothing in this codebase deletes a record. Actual deletion stays a manual, human/DB-level action outside the system, matching the docs' own "manual, Admin-triggered deletion" wording; automated purge is explicitly deferred (data-design.md).

### `RetentionFlagging` (`src/Vlms.Infrastructure/Reporting/RetentionFlagging.cs`)

The retention-flagging computation, extracted as a shared static helper — the same "one implementation, two callers" shape `AtRiskStudentFlagging` established above — so `Safeguarding.ConsentExpiryJob`'s daily sweep (the docs name it as this report's source) and the new on-demand `RecordsDueForDeletionService` share one implementation. No role check of its own, same "shared helper, caller-owned authorization" shape as `AtRiskStudentFlagging`/`ParentGuardianLinkage`.

- **`GetStudentsDueForDeletionAsync(db, now, ct)` -> `IReadOnlyList<StudentDueForDeletion>`** — Graduated students only, flagged when more than 3 years have passed since their graduation date.
- **`GetDbsChecksDueForDeletionAsync(db, today, ct)` -> `IReadOnlyList<DbsCheckDueForDeletion>`** — every `DbsCheck` row (any status, not just Clear — retention is about the record itself, not current clearance validity) whose `ExpiryDate` is more than 6 years in the past. Each row is evaluated independently, not just a teacher's most recent check.

**A genuine schema gap this item surfaced, documented rather than silently patched or fabricated:** retention for both entity kinds is defined relative to an event date the schema does not actually record for every reachable case.

- `Student.Status` can be Active, Inactive, or Graduated, but no write path in this codebase ever sets it to Inactive (grepped — only student-registration, which creates Active, and `PromotionService`, which only ever sets Graduated), and there is no "left the programme on this date" field anywhere. **"Graduating" is reliably derivable without a schema change**: `PromotionService` closes exactly one `StudentRankProgress` row (`CompletedAt`) in the same operation that sets Status to Graduated, and for the final rank no new row is opened — so the latest `StudentRankProgress.CompletedAt` across a Graduated student's history *is* their graduation date. **"Leaving" has no equivalent signal at all.** Rather than fabricate a proxy (e.g. `EnrolmentDate` would be actively wrong), `GetStudentsDueForDeletionAsync` only ever flags Graduated students — an Inactive student (reachable in the data model, e.g. via a future direct support/DB action, just not by any code path in this repo today) is silently excluded, not given a wrong date. A Graduated student with no closed `StudentRankProgress` row at all (a data anomaly not enforced by a database constraint) is likewise skipped.
- `DbsCheck.ExpiryDate` is always present, so the expiry-based trigger is fully implementable. "The teacher leaving" has no schema support — `AppUser`/`UserRole` carry no status/leaving date of any kind — so only the expiry-based trigger is implemented. This is the conservative reading: a DBS record is never flagged *later* than 6 years past its own expiry regardless of whether the teacher left earlier; "or the teacher leaving" could only ever bring the flag date forward, never push it back.

Tracked as a new `STATE.md` Next item: a real "leaving date" field (plus whatever marks a student Inactive in the first place) is a genuine prerequisite this report cannot itself invent.

`GetDbsChecksDueForDeletionAsync` materializes real `DbsCheck` entities (`.ToListAsync()` on the query itself, enrichment done afterwards in memory) rather than an anonymous-type projection — the exact bug class a prior checker review found in `ConsentExpiryJob.SweepDbsAsync` (commit d23f6e4): a projection never triggers EF Core's `IMaterializationInterceptor`, so `SensitiveDataAuditInterceptor` would silently never fire. `Student` is not access-restricted, so no query-filter concern there.

### `ConsentExpiryJob` integration

`ConsentExpiryJob.RunAsync()` (see [safeguarding-consent.md](safeguarding-consent.md)) now also calls `RetentionFlagging.GetStudentsDueForDeletionAsync`/`GetDbsChecksDueForDeletionAsync`, widening its sweep per the docs' own "from ConsentExpiryJob's wider sweep" wording — `ConsentExpiryJobResult` gained `StudentsDueForDeletion`/`DbsChecksDueForDeletion` fields. Logged at `LogWarning` (not `LogError`) — this is administrative housekeeping ("manual, Admin-triggered deletion"), not a safeguarding-critical block the way expired consent/DBS are. Deliberately **not** passed to `ConsentExpiryNotifier` — that class's scope is `IsExpired = true` consent/DBS flags only; retention due-for-deletion stays log-only plus browsable on demand via the report page, the same log-only treatment `ConsentExpiryJob`'s "escalation" judgement call already established for a different docs-silent gap before `NotificationService` existed.

### `RecordsDueForDeletionService` (`src/Vlms.Infrastructure/Reporting/RecordsDueForDeletionService.cs`)

Read-only aggregation service backing a single Blazor page, same shape as `ProgressReportingService` above. `GetReportAsync()` -> `RecordsDueForDeletionReport` (`Students` + `DbsChecks`), role-checked `RequireAdmin` inside the service itself.

**Role scope, a documented judgement call (same discipline as `ProgressReportingService` above):** Admin-only, matching both docs' exact wording — data-design.md calls this "an Admin-facing... report", and every retention/deletion sentence in security-compliance.md says "manual, *Admin*-triggered deletion", never naming SafeguardingOfficer the way the consent/DBS *management* pages do. This is narrower than the `RequireAdminOrSafeguardingOfficer` gate `ConsentRecordService`/`DbsCheckService` use for ordinary DBS viewing/editing — deliberately so — and does not weaken ADR-0004's whole-entity restriction: Admin is already one of the two roles that restriction allows, so a narrower Admin-only page-level gate is strictly inside what the `DbsCheck` query filter already permits, not a bypass of it.

### Page

`src/Vlms.Web/Components/Pages/Reporting/RecordsDueForDeletion.razor` (`/reporting/records-due-for-deletion`), gated by `[Authorize(Policy = "RequireAdmin")]`, linked from `Home.razor` and cross-linked from `ProgressReporting.razor`. Two read-only tables (students, DBS checks) — no forms, no delete action.

### Tests

`tests/Vlms.Tests/Infrastructure/RecordsDueForDeletionServiceTests.cs` (SQLite-in-memory-via-DI): the flagging scenario (graduated-long-ago flagged, recently-graduated not, no-history-row not fabricated, Active/Inactive excluded, DBS-past-retention flagged per-row not per-teacher); role-gating denial for every non-Admin role; a discriminating audit-log test proving `DbsCheck` reads are genuinely materialized (regression guard for the d23f6e4 bug class); and an equivalence test proving `RecordsDueForDeletionService` and `ConsentExpiryJob.RunAsync()` produce the identical flagged population from the shared `RetentionFlagging` helper. `ConsentExpiryJobTests.cs` gained matching integration tests for the widened sweep, including one using a Flagged-status (non-Clear) `DbsCheck` to isolate that the retention sweep's own materialization — not `SweepDbsAsync`'s pre-existing Clear-only one — is what triggers its audit row.

No EF Core schema/migration changes (`dotnet ef migrations has-pending-model-changes` confirmed no drift) — the graduation date is derived from the existing `StudentRankProgress` history, not a new field.
