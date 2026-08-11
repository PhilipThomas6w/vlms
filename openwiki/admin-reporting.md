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

### `Retention.LeavingService` (`src/Vlms.Infrastructure/Retention/LeavingService.cs`)

STATE.md "Manual 'leaving' flag for students and teachers, feeding the retention report" — the
Admin-only, manually-triggered action that finally gives `RetentionFlagging` (below) the leaving-date
signal it originally found missing. Two methods, both role-checked `RequireAdmin` inside the service
itself (no self-service path for a Student/Teacher to mark themselves inactive/left):

- **`MarkStudentInactiveAsync(studentId, ct)`** — sets `Student.Status = StudentStatus.Inactive` (the
  enum value already existed but was previously unreachable by any write path) and `Student.LeftAt`
  to `DateTime.UtcNow`, captured automatically at the moment the action runs rather than a
  manually-entered/backdated date the Admin types in — STATE.md's own item text asked for "a boolean
  the Admin explicitly enables", not a date-entry form; flagged as revisitable if backdating is ever
  wanted. Throws if the student is not currently Active (an already-Inactive or Graduated student has
  no "leaving" transition to make).
- **`MarkTeacherLeftAsync(teacherUserId, ct)`** — sets `AppUser.HasLeft = true`/`AppUser.LeftAt = DateTime.UtcNow`
  on the target `AppUser` (same auto-captured-timestamp reasoning). Validates the target holds
  `Role.Teacher` first (the same check `Safeguarding.DbsCheckService.RecordAsync` already applies).
  `AppUser.HasLeft`/`LeftAt` are schema-generic to any `AppUser` row, not a new Teacher-specific
  entity — see [domain.md](domain.md)'s entity notes for the full "why generic field, Teacher-scoped
  action" reasoning.

No-cascade scope, explicitly: neither method triggers deletion (that stays
`RecordsDueForDeletionService`'s existing separate manual step) or touches promotions, badges,
certificates, or any `DbsCheck` row — only the two fields named above change.

Page: `src/Vlms.Web/Components/Pages/Retention/MarkAsLeft.razor` (`/retention/mark-as-left`), gated
`[Authorize(Policy = "RequireAdmin")]`, linked from `Home.razor`. Two per-row-button lists (Active
students; Teachers who haven't already left) — no `EditForm`, matching `DbsChecks.razor`'s existing
per-row "Mark @status" button shape.

### `RetentionFlagging` (`src/Vlms.Infrastructure/Reporting/RetentionFlagging.cs`)

The retention-flagging computation, extracted as a shared static helper — the same "one implementation, two callers" shape `AtRiskStudentFlagging` established above — so `Safeguarding.ConsentExpiryJob`'s daily sweep (the docs name it as this report's source) and the on-demand `RecordsDueForDeletionService` share one implementation. No role check of its own, same "shared helper, caller-owned authorization" shape as `AtRiskStudentFlagging`/`ParentGuardianLinkage`.

- **`GetStudentsDueForDeletionAsync(db, now, ct)` -> `IReadOnlyList<StudentDueForDeletion>`** — now flags **both** Graduated students (`StudentDeletionReason.Graduated`, anchor = latest closed `StudentRankProgress.CompletedAt`, unchanged) **and** Inactive students with a recorded `Student.LeftAt` (`StudentDeletionReason.Left`, anchor = `Student.LeftAt` directly), each flagged when more than 3 years have passed since their respective anchor date. An Inactive student with no `LeftAt` recorded (a data anomaly — `LeavingService` always sets both together, but not database-enforced) is skipped, not fabricated a date, the same discipline already applied to a Graduated student with no closed `StudentRankProgress` row.
- **`GetDbsChecksDueForDeletionAsync(db, today, ct)` -> `IReadOnlyList<DbsCheckDueForDeletion>`** — every `DbsCheck` row (any status, not just Clear), anchored on whichever is earlier of its own `ExpiryDate` (`DbsCheckDeletionReason.Expired`) or its teacher's `AppUser.LeftAt` if `AppUser.HasLeft` and that date is earlier than the check's own expiry (`DbsCheckDeletionReason.TeacherLeft`) — the conservative reading this file always documented: "the teacher leaving" can only ever bring the retention anchor forward, never push it back past the check's own expiry. Each row is evaluated independently, not just a teacher's most recent check.

**The "no leaving date recorded anywhere" schema gap this item originally surfaced (commit 73eb0f8) is now closed** by `Student.LeftAt`/`AppUser.HasLeft`/`AppUser.LeftAt` (STATE.md "Manual 'leaving' flag..." item, migration `AddLeavingFieldsToStudentAndAppUser`) and `Retention.LeavingService` above — both methods are additive over what existed before, not a rewrite.

Fetches **every** `DbsCheck` row (not just those already past retention by their own `ExpiryDate`) — evaluating "due" now requires comparing each row against its teacher's `HasLeft`/`LeftAt`, which a SQL-side `ExpiryDate`-only filter alone can't express; the broader read mirrors `ConsentExpiryJob.SweepDbsAsync`'s own existing "materialize a broad set, filter in memory" shape (`.ToListAsync()` on the `DbsCheck` query itself, before any further `.Select(...)` projection — the same discipline that fixed the d23f6e4 bug class) rather than inventing a narrower query, at this codebase's tens-of-rows scale. One consequence worth knowing: every `DbsCheck` row now generates a `SensitiveDataAccessLog` entry each time this report/sweep runs, not only the rows that end up flagged (a fuller audit trail, not a thinner one) — except where EF Core's own identity resolution means a row already tracked earlier in the *same* `VlmsDbContext` (e.g. by `ConsentExpiryJob.SweepDbsAsync` running first in the same `RunAsync` call) is returned from the change tracker rather than re-materialized, so it does not add a second audit row for that already-read row.

`Student` is not access-restricted, so no query-filter concern there.

### `ConsentExpiryJob` integration

`ConsentExpiryJob.RunAsync()` (see [safeguarding-consent.md](safeguarding-consent.md)) now also calls `RetentionFlagging.GetStudentsDueForDeletionAsync`/`GetDbsChecksDueForDeletionAsync`, widening its sweep per the docs' own "from ConsentExpiryJob's wider sweep" wording — `ConsentExpiryJobResult` gained `StudentsDueForDeletion`/`DbsChecksDueForDeletion` fields. Logged at `LogWarning` (not `LogError`) — this is administrative housekeeping ("manual, Admin-triggered deletion"), not a safeguarding-critical block the way expired consent/DBS are. Deliberately **not** passed to `ConsentExpiryNotifier` — that class's scope is `IsExpired = true` consent/DBS flags only; retention due-for-deletion stays log-only plus browsable on demand via the report page, the same log-only treatment `ConsentExpiryJob`'s "escalation" judgement call already established for a different docs-silent gap before `NotificationService` existed.

### `RecordsDueForDeletionService` (`src/Vlms.Infrastructure/Reporting/RecordsDueForDeletionService.cs`)

Read-only aggregation service backing a single Blazor page, same shape as `ProgressReportingService` above. `GetReportAsync()` -> `RecordsDueForDeletionReport` (`Students` + `DbsChecks`), role-checked `RequireAdmin` inside the service itself.

**Role scope, a documented judgement call (same discipline as `ProgressReportingService` above):** Admin-only, matching both docs' exact wording — data-design.md calls this "an Admin-facing... report", and every retention/deletion sentence in security-compliance.md says "manual, *Admin*-triggered deletion", never naming SafeguardingOfficer the way the consent/DBS *management* pages do. This is narrower than the `RequireAdminOrSafeguardingOfficer` gate `ConsentRecordService`/`DbsCheckService` use for ordinary DBS viewing/editing — deliberately so — and does not weaken ADR-0004's whole-entity restriction: Admin is already one of the two roles that restriction allows, so a narrower Admin-only page-level gate is strictly inside what the `DbsCheck` query filter already permits, not a bypass of it.

### Page

`src/Vlms.Web/Components/Pages/Reporting/RecordsDueForDeletion.razor` (`/reporting/records-due-for-deletion`), gated by `[Authorize(Policy = "RequireAdmin")]`, linked from `Home.razor` and cross-linked from `ProgressReporting.razor`. Two read-only tables (students, DBS checks) — no forms, no delete action. Each table now shows a `Reason`/anchor-date column alongside the existing date/days-past-retention columns (`StudentDeletionReason`/`DbsCheckDeletionReason` — Graduated/Left, Expired/TeacherLeft), and both explanatory paragraphs link to `/retention/mark-as-left`.

### Tests

`tests/Vlms.Tests/Infrastructure/RecordsDueForDeletionServiceTests.cs` (SQLite-in-memory-via-DI): the flagging scenario (graduated-long-ago flagged, recently-graduated not, no-history-row not fabricated, Active-with-no-LeftAt and Inactive-with-no-LeftAt both excluded as anomalies, left-long-ago flagged, recently-left not, DBS-past-retention-by-expiry flagged per-row not per-teacher, DBS-past-retention-because-the-teacher-left flagged even when the check's own expiry is not yet due, DBS-teacher-recently-left not flagged); role-gating denial for every non-Admin role; a discriminating audit-log test proving `DbsCheck` reads are genuinely materialized, now asserting one row per *seeded* check (every row is read now, not only flagged ones — see `RetentionFlagging`'s doc comment above); and an equivalence test proving `RecordsDueForDeletionService` and `ConsentExpiryJob.RunAsync()` produce the identical flagged population (including `Reason`/anchor date) from the shared `RetentionFlagging` helper. `ConsentExpiryJobTests.cs` gained matching integration tests for the widened sweep (including a dedicated `RunAsync_FlagsLeftStudentsAndLeftTeacherDbsChecks` test for the new Left/TeacherLeft branches), plus one using a Flagged-status (non-Clear) `DbsCheck` to isolate that the retention sweep's own materialization — not `SweepDbsAsync`'s pre-existing Clear-only one — is what triggers its audit row. `tests/Vlms.Tests/Infrastructure/LeavingServiceTests.cs` (new, SQLite-in-memory-via-DI, same pattern as `DbsCheckServiceTests`): both methods succeed for Admin and auto-capture `LeftAt`/`HasLeft` (asserted via a before/after `DateTime` range, not just non-null); role-gating denial for every non-Admin role with an explicit no-change assertion; `MarkStudentInactiveAsync` rejects an already-Graduated student, an already-Inactive student, and an unknown student id; `MarkTeacherLeftAsync` rejects a non-Teacher `AppUser`, an unknown `AppUser` id, and an already-left teacher.

EF Core schema/migration change: `AddLeavingFieldsToStudentAndAppUser` (`Student.LeftAt`, `AppUser.HasLeft`/`LeftAt` — see [data-access.md](data-access.md)); `dotnet ef migrations has-pending-model-changes` confirmed no drift after generating it. The Graduated-student anchor date is still derived from `StudentRankProgress` history, not a new field — only the Inactive/TeacherLeft branches use the new columns directly.
