# Progress tracking

Design source: `docs/design/low-level-design.md` "CompletionService"/"PromotionService"/"CertificateService", `docs/design/data-design.md`, `docs/requirements/functional.md` (FR-003, TC-001/TC-003). Implements VISION.md's second mission sentence: lesson completions, auto-promotion through the rank ladder, and auto-generated PDF certificates.

All three services live in `src/Vlms.Infrastructure/Progress/` — despite low-level-design.md heading this section "Key domain services (Vlms.Domain)", they're built in Infrastructure because they depend on `VlmsDbContext`/EF Core, the same divergence already established by `LessonProposalService` (see [curriculum.md](curriculum.md)). `Vlms.Domain` stays EF-Core-free.

## `CompletionService.MarkCompleteAsync(studentId, lessonId, note?)`

Teacher-only (role-checked inside the service — same defense-in-depth pattern as `LessonProposalService`, see [curriculum.md](curriculum.md)). Steps:

1. **Consent gate (functional.md FR-003, data-design.md):** blocked with `InvalidOperationException` unless the student has at least one `ConsentRecord` with `Status == Approved` and `ExpiryDate >= today`. Reads only the non-sensitive `ConsentRecord` (`Status`/`ExpiryDate`) — never `ConsentSensitiveDetails`, which this service has no reason to touch (see [access-control.md](access-control.md)).
2. Records the `StudentLessonCompletion` row.
3. Calls `PromotionService.CheckAndPromoteAsync` (order matches low-level-design.md's literal wording: "triggers PromotionService check and CertificateService.Generate(...)").
4. Calls `CertificateService.GenerateAsync` for the new completion — data-design.md: a `Certificate` is generated **per completion**, not just per promotion (`StudentLessonCompletion` 1:1 `Certificate`).

**Not wrapped in one explicit database transaction** across all three steps — each service `SaveChanges()`s its own work. A deliberate simplification (VISION.md "fewest moving parts" for a solo-maintained, tens-of-users system): a failure partway (e.g. a blob upload failing) leaves the completion recorded but the certificate/promotion step incomplete, recoverable by re-running rather than something that corrupts state.

## `PromotionService.CheckAndPromoteAsync(studentId)`

Called by `CompletionService` after every completion (no role check of its own — it's a system-internal step following an already-authorized `MarkCompleteAsync` call). Returns `true` if the student was promoted or graduated, `false` if their current rank still has incomplete active lessons — the definition-of-done "non-promotion case".

- **Completion test:** every `Lesson` with `RankId == student.CurrentRankId && IsActive` must have a non-reversed `StudentLessonCompletion` for that student. A rank with zero active lessons is vacuously "complete" (unusual in practice, not specifically guarded against). Reversed completions (`IsReversed == true`) never count.
- **Ladder ordering:** `Rank.Order` (already on the entity, with the existing `Rank.IsBefore` helper) — "next rank" = the `Rank` with the smallest `Order` greater than the current one. No schema gap here — `Order` already fully describes the ladder.
- **On promotion:** closes the current `StudentRankProgress` row (sets `CompletedAt`), opens a new one for the next rank, advances `Student.CurrentRankId`, and awards that (just-completed) rank's `RankBadge` via a new `StudentBadge` — if no `RankBadge` is configured for that rank, promotion still proceeds with no badge (badge reference data is populated separately, e.g. by an Admin; its absence must not block a student's progression).
- **At the final rank** (no `Rank` with a higher `Order`): sets `Student.Status = Graduated` instead of advancing; `CurrentRankId` is left unchanged.
- **Precondition — flagged, not silently patched over:** this service expects an *open* `StudentRankProgress` row (`CompletedAt == null`) to already exist for the student's current rank before it's ever called, and throws a clear `InvalidOperationException` if none is found rather than fabricating one. **Closed:** `StudentRegistrationService` (see [student-registration.md](student-registration.md)) now opens exactly this row at registration; `PromotionServiceTests` still seed the row directly (unit-test isolation), while `StudentRegistrationServiceTests` has a dedicated integration test proving a freshly registered student can actually be driven through `CheckAndPromoteAsync` without hitting this exception.

## `CertificateService.GenerateAsync(completionId)`

Builds a minimal QuestPDF document (student name, lesson title, completion date), uploads it via `IBlobStorage` under `certificates/{studentId}/{completionId}.pdf`, and writes the `Certificate` row. `QuestPDF.Settings.License = LicenseType.Community` is set once in a static constructor — independent of whether `Vlms.Web`'s `Program.cs` ever touches this class, so it's set correctly regardless of call site (mirrors the "process-wide, set-once" nature of a licence flag). No role check of its own, same reasoning as `PromotionService`.

## `IBlobStorage` (`src/Vlms.Domain/IBlobStorage.cs`)

New abstraction, same placement pattern as `ICurrentUserContext`: the interface lives in `Vlms.Domain` (technology-agnostic, no Azure SDK dependency), both implementations live in `Vlms.Infrastructure`:

- **`Storage/AzureBlobStorage.cs`** — real implementation backed by `Azure.Storage.Blobs` (adr/0001-technology-stack.md). **Not yet wired into `Vlms.Web`'s DI container or `appsettings.json`** — no UI consumes `CertificateService` yet (this increment is service-layer only), so there's nothing to point a real storage account at. Kept structurally correct and buildable, the same way `EntraCurrentUserContext`/Microsoft.Identity.Web were built out against placeholder Entra config before a live tenant existed. Wire a `"BlobStorage"` config section + DI registration when the first consumer (a Teacher-facing "mark complete" page, or similar) lands.
- **`InMemoryBlobStorage`** (`tests/Vlms.Tests/Infrastructure/InMemoryBlobStorage.cs`) — the test double, same spirit as `FakeCurrentUserContext`. Records every uploaded blob's bytes so tests can assert on what was actually written (including checking the `%PDF` magic-byte header — proof QuestPDF produced a real document, not placeholder bytes).

## Tests

`tests/Vlms.Tests/Infrastructure/CompletionServiceTests.cs`, `PromotionServiceTests.cs`, `CertificateServiceTests.cs` — same SQLite-in-memory-via-DI pattern as `LessonProposalServiceTests` (see [testing.md](testing.md)). Coverage includes: completion recording with active consent (and the resulting certificate), expired-consent and missing-consent blocking, non-Teacher denial, promotion on final-lesson completion, the non-promotion case (incomplete lessons), reversed completions not counting, badge award with/without a configured `RankBadge`, graduation at the final rank, and certificate generation including the PDF magic-byte check and an unknown-completion-id failure.
