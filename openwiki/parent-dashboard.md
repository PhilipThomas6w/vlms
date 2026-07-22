# Parent dashboard

Design source: STATE.md, `docs/requirements/functional.md` "Parent engagement" ("Parents get... an in-app dashboard (view own child's progress, certificates, badges)"), `docs/design/low-level-design.md` "Authorization model", `docs/quality/test-plan.md` TC-006 ("Parent views own child's dashboard; cannot view another parent's child").

## `ParentDashboardService` (`src/Vlms.Infrastructure/Engagement/ParentDashboardService.cs`)

`GetDashboardAsync()` — role-checked inside the service itself (defense in depth, same pattern as every other service in this codebase), Parent only. Returns one `ParentDashboardStudent` per student linked to the caller: current rank + when they started it, badges, certificates, and non-sensitive consent status/expiry (the same `ConsentRecord` fields Teacher/Approver can already see — `ConsentSensitiveDetails`/`DbsCheck` are not surfaced at all, since those stay whole-entity-restricted to Admin/SafeguardingOfficer via the existing query filter regardless of role, adr/0004).

**Scoping — the property TC-006 names — reuses `ParentGuardianLinkage`** (`src/Vlms.Infrastructure/Authorization/ParentGuardianLinkage.cs`), a new shared helper extracted in this increment: `LinkedStudentIds(db, parentAppUserId)` enumerates every `Student` linked to a given Parent via `StudentGuardianLink` → `ParentGuardian.AppUserId`. `ParentStudentAccessHandler` (see [authentication-authorization.md](authentication-authorization.md)) now delegates its single-resource check (`IsLinkedAsync`) to the same helper, rather than each place writing its own copy of the same join — same relationship, two directions ("is this one Student linked" vs "enumerate all of mine"), one query.

All subsequent per-student data (badges, certificates, consent) is fetched via simple int-keyed EF joins projected to anonymous types and combined in memory — the same style `ConsentExpiryJob.SweepConsentAsync`/`SweepDbsAsync` already use (see [safeguarding-consent.md](safeguarding-consent.md)) — rather than `Include`/`ThenInclude` chains through nullable navigation properties.

**Scope decision:** deliberately just this — progress, badges, certificates, consent status/expiry. No reporting/analytics, no notification-preferences UI, no certificate download wiring (that's still gated on `IBlobStorage` being wired into `Vlms.Web`, unchanged from the progress-tracking increment) — not asked for, and a minimal, correct dashboard beats a speculative one.

**Design-gap history — flipped once, now decided by Philip (2026-07-20):** does reversing a `StudentLessonCompletion` (`IsReversed`) cascade to hiding its already-generated `Certificate`?

- **First pass (commit 922cc69):** concluded "no gap found" — the certificate join deliberately did *not* filter on `IsReversed`, reasoning that the only existing consumer of `IsReversed` (`PromotionService`) treats it purely as "doesn't count toward rank-progression counting", never as "never happened", and that `Certificate` has no documented lifecycle beyond generation.
- **Checker-FAIL reclassification:** an adversarial checker review found this had been closed as Done unilaterally on a live, docs-silent, consequential business-judgement call (what a parent is told did happen to their child) — reclassified to `## Blocked / needs decision` for Philip to decide, per CLAUDE.md's "flag assumptions... instead of silently choosing".
- **Decision (Philip, 2026-07-20):** the certificate join now **does** filter out certificates whose underlying `StudentLessonCompletion.IsReversed == true`. Reading: a reversal means "this shouldn't have happened" (functional.md's Teacher self-correction wording), so a certificate generated from a reversed completion is itself wrong and must not be shown to a parent — the certificate's validity is derivative of the completion it came from, not independent of it. This is the current, load-bearing behaviour; do not silently revert it again without a fresh, deliberate decision recorded the same way.

A regression test (`ParentDashboardServiceTests.GetDashboardAsync_CertificateForReversedCompletion_IsHidden_ReversalCascadesToCertificateVisibility`) proves the current behaviour: exactly one certificate is returned for the non-reversed completion, and the reversed completion's certificate ("Fire Safety") is absent.

**Closed:** the gap this section used to name ("no production code path anywhere in `src/` actually sets `IsReversed = true` — only test seed data does") is fixed — `CompletionService.ReverseAsync` (see [progress-tracking.md](progress-tracking.md)) is the Teacher-facing write path, with a page at `/progress/completions`. `ParentDashboardServiceTests.GetDashboardAsync_CertificateHiddenAfterGenuineReversal_ViaCompletionServiceReverseAsync` proves the certificate-hiding behaviour above holds for a completion reversed through that real write path, not just for one seeded directly with `IsReversed = true` — closing the loop this section's own history had left open.

## Page (`src/Vlms.Web/Components/Pages/Parent/ParentDashboard.razor`)

`/parent/dashboard`, gated `[Authorize(Policy = "RequireParent")]` (the existing per-role policy — no new policy needed, unlike the multi-role Admin/Teacher/SafeguardingOfficer pages). Linked from `Home.razor` via `AuthorizeView Policy="RequireParent"`.

## Tests

`tests/Vlms.Tests/Infrastructure/ParentDashboardServiceTests.cs` — SQLite-in-memory-via-DI pattern, two separate parent/child families seeded in the same database. The discriminating test is `GetDashboardAsync_ParentB_SeesOnlySam_NeverAlex_CrossParentAccessIsDenied` (TC-006's exact property): Parent B's dashboard never contains Parent A's child, even though both exist in the same database Parent B's own query runs against. Also covers: full progress/badges/certificates/consent for a linked student, a student with no consent record yet (nulls, not an exception), a Parent with zero linked students (empty list, not an error), and denial (`UnauthorizedAccessException`) for every non-Parent role.
