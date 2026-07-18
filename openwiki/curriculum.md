# Curriculum management

Design source: `docs/design/low-level-design.md` "LessonProposalService", `docs/requirements/functional.md` "Curriculum management". Implements the propose → approve/reject-with-comments → resubmit workflow (VISION.md's curriculum-management sentence).

## `LessonProposalService` (`src/Vlms.Infrastructure/Curriculum/LessonProposalService.cs`)

Same layering as `UserProvisioningService` (see [authentication-authorization.md](authentication-authorization.md)/[data-access.md](data-access.md)): takes `VlmsDbContext` plus `ICurrentUserContext` directly, no ASP.NET Core dependency. Four methods:

- `ProposeAsync(lessonId, changeType, content)` — any Teacher. `lessonId` is null only for `LessonChangeType.Create`. Creates a `Pending` `LessonChangeProposal`.
- `ApproveAsync(proposalId)` — Approver only. Applies the proposal's content onto the `Lesson` (creates it if `LessonId` was null; otherwise updates it in place, including setting `IsActive = false` for a Retire), then marks the proposal `Approved`.
- `RejectAsync(proposalId, comments)` — Approver only. Sets `Rejected` + `ApprovalComments`; the `Lesson` is untouched.
- `ResubmitAsync(originalProposalId, content)` — any Teacher (same or different one than the original proposer). Only valid when the original is `Rejected`. Creates a **new** `Pending` proposal chained via `ResubmissionOfProposalId`; the original row is left alone.

**Authorization is enforced inside the service, not just assumed from page-level gating.** `RequireRole`/`RequireResolvedUserId` throw `UnauthorizedAccessException` if the caller lacks the role or has no resolved `UserId`. This is deliberate defense-in-depth — the same reasoning as `VlmsDbContext`'s sensitive-data query filters (adr/0004): a caller reaching this service by any path other than the intended UI must still be denied here. `LessonProposalServiceTests` (`tests/Vlms.Tests/Infrastructure/`) has an explicit test proving a Teacher (including the original proposer) cannot approve — not just "the UI wouldn't show them the button".

**Curriculum-only, structurally.** Nothing in this service reads or writes `DbsCheck`/`ConsentSensitiveDetails`, and `Lesson`/`LessonChangeProposal` carry no sensitive-data query filter — there's nothing here that could bypass ADR-0004's restriction even by accident.

## `ProposedLessonContent` (`src/Vlms.Infrastructure/Curriculum/ProposedLessonContent.cs`)

`docs/design/data-design.md` specifies `LessonChangeProposal.ProposedContent` only as a string — the JSON shape is a build-time decision, not a design-gate one: a proposal always carries the **full** target state of the `Lesson` (`RankId` — Create only —, `Code`, `Title`, `ContentBlobKey`, `IsActive`), never a partial patch. Create/Edit/Retire are all "apply this content", which keeps `LessonProposalService`'s apply logic uniform; a Retire proposal is expected to carry the lesson's existing `Code`/`Title`/`ContentBlobKey` with `IsActive` set to `false` (the Teacher page's "Propose retirement" button pre-fills exactly this). Serialized via `System.Text.Json`.

## Updating init-only domain entities

`Lesson`/`LessonChangeProposal` use this codebase's usual init-only property convention (see [domain.md](domain.md)) — no public setters. `ApproveAsync`/`RejectAsync` update them via `context.Entry(x).CurrentValues.SetValues(new Lesson { ... })`, which EF Core performs by writing through the compiler-generated backing fields, not the C# `init` accessor. This is the officially documented EF Core pattern for updating entities without public setters (see "Identity Resolution in EF Core" → "Updating an entity" on Microsoft Learn), not a workaround — the first place in this codebase an entity gets updated in place rather than only created.

## Teacher/Approver pages (`src/Vlms.Web/Components/Pages/Curriculum/`)

- `TeacherProposals.razor` (`/curriculum/teacher`, `@attribute [Authorize(Policy = "RequireTeacher")]`) — browse lessons; propose a create/edit/retire; see the status of your own proposals (`ProposedByUserId == CurrentUser.UserId`), including rejection comments, with a resubmit action that re-opens the form pre-filled from the rejected proposal's `ProposedContent`.
- `ApproverProposals.razor` (`/curriculum/approver`, `@attribute [Authorize(Policy = "RequireApprover")]`) — lists `Pending` proposals; approve, or reject with mandatory comments.

Both pages use `@rendermode InteractiveServer` and inject `LessonProposalService` directly (registered `AddScoped` in `Program.cs`) — this is exactly the interactive-render scenario [web.md](web.md)/[authentication-authorization.md](authentication-authorization.md) describe the `AuthenticationStateProvider` fix as being for; these pages are the first to actually exercise it. Gating is via the existing `RequireTeacher`/`RequireApprover` policies (no ad hoc role checks in the pages themselves) — enforced by `Routes.razor`'s `AuthorizeRouteView`.

Form binding uses a private mutable `ProposalFormModel` per page, distinct from `ProposedLessonContent` — Blazor's `EditForm`/`InputText` two-way binding needs plain `{ get; set; }` properties, which `ProposedLessonContent`'s `required`/init-only shape doesn't support.
