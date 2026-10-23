# WCAG 2.2 AA accessibility review checklist

Gated by `build/check-accessibility.ps1` as part of `build/verify.ps1`'s full run (skipped by
`-Fast`). Same currency mechanism as `docs/governance/asvs-access-control-checklist.md`: the script
hashes every `.razor` file under `src/Vlms.Web/Components/` plus `wwwroot/app.css`, and compares
that hash to the `Reviewed-hash` line at the bottom of this file. A mismatch — because markup or
styling changed since the last review — fails the stage until a human re-reviews and records the
new hash (run `pwsh -File build/check-accessibility.ps1 -PrintHash`).

**Why this is a checklist and not a fully automated scan:** checked against current tooling before
deciding this (CLAUDE.md "verify, don't invent") — there is no established, genuinely CI-runnable
static/build-time accessibility linter for Razor/Blazor markup. The real tools (axe-core, Deque's
axe DevTools, Microsoft's Accessibility Insights) all inspect a rendered DOM and need a live browser
(Playwright/Selenium) driving a running instance of the app — a real option for a later increment
once this project has CI and a hosted test environment, but disproportionate new machinery to add
to every local `-Full` run today, for a solo-maintained, tens-of-users, no-CI-yet project (the same
"fewest moving parts" reasoning ADR-0003 applies to WebJob hosting). `build/check-accessibility.ps1`
automates the narrow slice that's genuinely checkable from the markup alone (see its header
comment); this checklist covers the rest.

## Automated by build/check-accessibility.ps1, not repeated here

- Every `<img>` has an `alt` attribute.
- Every `InputText`/`InputTextArea`/`InputDate`/`InputNumber`/`InputSelect` with an `id` has a
  matching `<label for="...">` in the same file.
- The root `<html>` element (`App.razor`) declares a `lang` attribute.

## Manual review items — current UI surface

The current pages (`src/Vlms.Web/Components/Pages/`): `Home.razor`,
`Curriculum/TeacherProposals.razor`, `Curriculum/ApproverProposals.razor`,
`Guardianship/GuardianLinks.razor`, `Guardianship/AcceptInvite.razor`,
`Registration/RegisterStudent.razor`, `Safeguarding/ConsentRecords.razor`,
`Safeguarding/DbsChecks.razor`, `Safeguarding/StudentConsent.razor`, `Parent/ParentDashboard.razor`,
`Parent/ParentConsent.razor`,
`Reporting/ProgressReporting.razor`, `Reporting/RecordsDueForDeletion.razor`,
`Progress/CompletionReversals.razor`, `Retention/MarkAsLeft.razor`, plus the shared
`Layout/MainLayout.razor`/`Layout/ReconnectModal.razor` — and (STATE.md "Identity" item —
`src/Vlms.Web/Components/Account/Pages/`): `Login.razor`, `Register.razor`, `LoginWith2fa.razor`,
`LoginWithRecoveryCode.razor`, `Manage/EnableAuthenticator.razor`,
`Manage/TwoFactorAuthentication.razor`.

- [ ] **`Administration/UserRoles.razor`, reviewed this pass (STATE.md "Admin role assignment +
      first-Admin bootstrap"):** one data table (`<table>`/`<thead>`/`<tbody>`, real `<th>` header
      cells), same structure as `Retention/MarkAsLeft.razor`. Each row's grant `<select>` has a
      visible `<label for>` with a per-row `id` ("Role for Jane Doe"), so the label check and 1.3.1/
      4.1.2 are met without relying on placeholder text. Per-row buttons are native `<button
      type="button">` (keyboard-operable, 2.1.1) with an `aria-label` naming the action and the user
      ("Revoke Teacher from Jane Doe", "Grant selected role to Jane Doe") — each starts with the
      visible text ("Revoke"/"Grant"), so Label in Name (2.5.3) holds. Built with the established
      message pattern from the start: `<p class="error" role="alert">@_errorMessage</p>` and an
      always-rendered `<p role="status">@_statusMessage</p>` (e.g. "Teacher granted to Jane Doe."),
      both covered by `StatusMessageAccessibilityTests`. Leavers are shown with a text "(left)"
      marker, not colour alone (1.4.1). `Home.razor` gained one plain `<a>` nav link inside the
      existing `RequireAdmin` `AuthorizeView`. **Updated 12/10/2026** (waiting-for-approval item): the
      page now opens with a "Waiting for approval" `<h2>` section (second table, real `<th>`
      headers) above an "All users" `<h2>`, so the heading structure stays sequential (1.3.1/2.4.6).
      Its per-row "Approve as Parent" `<button>` has `aria-label` "Approve Jane Doe as Parent",
      which starts with the visible text (2.5.3), and success uses the same `role="status"` element
      ("Jane Doe approved as Parent.", covered by `StatusMessageAccessibilityTests`). Origin and
      linked students are plain text cells, not colour or icons.

- [ ] **`Retention/MarkAsLeft.razor`, reviewed this pass (STATE.md "Manual 'leaving' flag for
      students and teachers"):** no `EditForm`/`Input*` components at all — two lists of plain
      `<button>` elements ("Mark as left" per row), so the label-pairing question doesn't arise
      (nothing for `build/check-accessibility.ps1`'s label check to even find), the same shape as
      `Safeguarding/DbsChecks.razor`'s existing "Mark @status" per-row buttons — not a new pattern.
      Built with the "Status messages (4.1.3)" fix already in place from the start, not retrofitted:
      its error path uses `<p class="error" role="alert">@_errorMessage</p>` and its success path
      uses `<p role="status">@_statusMessage</p>` (e.g. "Jane Doe marked as left (Inactive)."),
      matching the established pattern exactly. Two data tables (`<table>`/`<thead>`/`<tbody>` with
      real `<th>` header cells), same structure as every other list page. The explanatory
      paragraphs on this page and on `Reporting/RecordsDueForDeletion.razor` (updated this pass to
      link here) use plain `<a>` elements, covered by the existing "Consistent navigation" item, not
      a new concern.

- [ ] **`Reporting/RecordsDueForDeletion.razor`, reviewed this pass (STATE.md "'Records due for
      deletion' report"):** a read-only report page, no `EditForm`/`Input*` components at all — no
      label-pairing question arises (nothing for `build/check-accessibility.ps1`'s label check to
      even find). Its error path uses `<p class="error" role="alert">`, the same pattern as every
      other page. Two data tables (`<table>`/`<thead>`/`<tbody>` with real `<th>` header cells),
      same structure as `ProgressReporting.razor`'s existing tables — no new pattern introduced. Not
      in scope for the "Status messages (4.1.3)" gap below: like `ProgressReporting.razor`, it has no
      form submission at all (nothing to confirm/report back after), so that item's "seven form
      pages" framing doesn't apply to it, same reasoning already applied to `ProgressReporting.razor`
      itself. The two new plain `<a>` links to it (from `Home.razor`'s existing `RequireAdmin`
      `<AuthorizeView>` block, and from `ProgressReporting.razor`) are real anchor elements, covered
      by the existing "Consistent navigation" item below, not a new concern.

- [ ] **`Progress/CompletionReversals.razor`, reviewed this pass (STATE.md "Teacher-facing reverse a
      completion flow"):** follows the same established pattern as the other eight form/list pages —
      its one `InputTextArea` (the reversal-reason field) has a matching `<label for="...">`
      (mechanically enforced by `build/check-accessibility.ps1`); its error path uses
      `<p class="error" role="alert">`, same as `RegisterStudent.razor`'s `_errorMessage` pattern;
      its list-then-inline-form shape (a table of completions, a reversal form that appears below it
      when "Reverse" is clicked) mirrors `Curriculum/TeacherProposals.razor`'s existing
      list-then-form pattern, not a new one. No new colour/contrast surface (plain table/form markup
      against the existing unstyled `app.css`, same open item as every other page below). **Update
      (STATE.md "WCAG 'Status messages (4.1.3)' gap"):** this page did have the same gap flagged
      here at the time — a successful reversal cleared the inline form and reloaded the list with
      no explicit success message — and has now been fixed alongside the seven form pages (see the
      "Status messages" item below for the full mechanism/reasoning); it gained a
      `<p role="status">@_statusMessage</p>` element set to "Completion reversed." on success.

- [ ] **New Identity/Account pages, reviewed this pass:** all six follow the same pattern already
      established elsewhere in this codebase — every `<input>`/`InputText`/`InputCheckbox` has a
      `<label for="...">` (mechanically enforced by `build/check-accessibility.ps1` for the
      `Input*` components; `Login.razor`'s "Remember me" `InputCheckbox` and `LoginWith2fa.razor`'s
      "Remember this device" one use a wrapping `<label>` instead, which is an equally valid
      accessible-name pattern the automated check doesn't need to cover since the label
      unambiguously owns the control); every error uses `<p class="error" role="alert">` and every
      `<ValidationSummary role="alert" />`, matching the existing pattern this checklist already
      signed off on for the seven form pages above. `Manage/EnableAuthenticator.razor` generates no
      QR code image at all (a documented scope decision in its own doc comment — no `<img>`, so no
      alt-text question arises) — only a manually-typed shared-secret key, which sidesteps the
      "QR code needs accessible alt text describing the underlying secret" problem entirely rather
      than solving it. `Manage/TwoFactorAuthentication.razor`'s recovery-code regeneration message
      uses `<p role="status">` — at the time this bullet was written, this was a genuine
      improvement on the "Status messages (4.1.3)" gap then still open on the older seven pages;
      that gap is now closed everywhere (see the "Status messages" item below), and this bullet is
      kept as the historical origin of the pattern the fix replicated, not a re-flag.

- [ ] **Consent pages, reviewed 22/10/2026 (functional.md FR-015, adr/0006):**
      `Parent/ParentConsent.razor` lists each linked child in a `<section>` with an `<h2>`, states
      the consent status in words ("Approved", "None in place", "Awaiting review", "Rejected"), not
      colour (1.4.1), and shows a rejected form's comments as plain text next to the status. One
      form is open at a time, in its own `<section aria-labelledby>` with an `<h2>` and `<h3>`
      groups; every text/date input has a literal `id` matched by a `<label for>` (1.3.1, 4.1.2,
      3.3.2), the three consents are `InputCheckbox`es wrapped in their own `<label>` (the
      established pattern), optional fields say "(optional)" in the label, and `[Required]` fields
      have specific messages ("Enter your child's full name.") shown in `ValidationSummary
      role="alert"` (3.3.1, 3.3.3). Service errors use the `role="alert"` paragraph and success the
      `role="status"` paragraph (4.1.3). `Safeguarding/ConsentRecords.razor` gains a "Pending
      submissions" table (real `<th>` cells) with a "Review" `<button>` per row; the review panel is
      a `<section aria-labelledby>` with an `<h2>`, a `<dl>` of the submitted values (name/date of
      birth shown beside what is on record), and one `InputTextArea id="decisionComments"` with a
      `<label for>` that says comments are required to reject (3.3.2). Rejecting without comments
      surfaces the service's message through the existing `role="alert"` paragraph. The paper-form
      heading became "Record a paper consent form" with a one-line explanation.
      `Safeguarding/StudentConsent.razor` (teacher, read-only) is a table with `<th>` headers and a
      per-row link whose text names the student ("Details for Alex Student", 2.4.4); the detail
      view uses an `<h1>` naming the student, `<h2>` sections and `<dl>` lists, and a "Back to
      student consent" link. All controls are native buttons, links and inputs (2.1.1). Not
      handled: focus is not moved to the review panel or form when it opens below the list — it
      follows in DOM order, the same accepted trade-off as the lesson-files rows.
- [ ] **Colour contrast (1.4.3/1.4.11):** `wwwroot/app.css` (the Blazor project template's default
      stylesheet, not yet restyled for VLMS) gives text and UI-component colours a 4.5:1 (text) /
      3:1 (UI component) contrast ratio against their background. No VLMS-specific branding/colour
      palette exists yet (same placeholder status as `wwwroot/icons/icon.svg` — see `STATE.md`'s
      PWA entry) — re-check this item specifically once real branding lands, not just at this
      review.
- [x] **Keyboard operability (2.1.1) / focus order (2.4.3) — closed (STATE.md "WCAG 'Keyboard
      operability (2.1.1) / focus order (2.4.3)' checklist review", corrected by a subsequent
      checker-FAIL round-trip fix — see the correction paragraph after the `ReconnectModal.razor`
      sub-bullet below before relying on this item).** Genuine item-by-item review of every
      `EditForm`/`<form>` page, not a hash-refresh — every current page read directly, not assumed
      compliant because it's "standard Blazor markup". **No defect found among the pages reviewed
      below; no code change made in that pass** — but that pass's page inventory never considered
      `Layout/MainLayout.razor` (not a routable page, so it fell outside the "every current page"
      framing this review actually used), and `MainLayout.razor` did have a real 2.1.1 defect, found
      by a checker review and fixed afterwards — see the correction below. Checked, across every
      `.razor` file under `src/Vlms.Web/Components/`, for the concrete ways this codebase (or the
      Blazor template it started from) could have broken native keyboard operability: a repo-wide
      grep for `tabindex` returned zero matches (nothing overrides the browser's default tab order
      anywhere); a repo-wide grep for `@onclick` found every hit attached to a real `<button>`
      element (`DbsChecks.razor`, `TeacherProposals.razor`, `ApproverProposals.razor`,
      `CompletionReversals.razor`, `ConsentRecords.razor`, `MarkAsLeft.razor`) — no `onclick`-only
      `<div>`/`<span>` standing in for an interactive control anywhere; a repo-wide grep for
      `.focus(` across every `.razor`/`.js`/`.cs` file found zero hits — nothing in this codebase's
      own code programmatically steals or redirects focus. Checked `wwwroot/app.css` for anything
      that visually hides the focus indicator (this item's own instruction, framed around the
      2.4.7-adjacent risk even though the bullet itself is 2.1.1/2.4.3): the only `outline: none`
      rule in the file is scoped to `h1:focus` — the Blazor Web App template's own default rule for
      suppressing the visual ring on the page-title heading that Blazor's enhanced-navigation script
      programmatically focuses for screen-reader page-change announcements, not an element reachable
      by pressing Tab — so it does not suppress the focus indicator on any keyboard-operable control
      (button, link, or form field); no page-scoped `.razor.css` file in this codebase declares
      `outline` at all (grepped). **All eight `EditForm` pages checked directly**
      (`Curriculum/TeacherProposals.razor`, `Guardianship/GuardianLinks.razor`,
      `Guardianship/AcceptInvite.razor`, `Registration/RegisterStudent.razor`,
      `Safeguarding/ConsentRecords.razor`, `Safeguarding/DbsChecks.razor`,
      `Progress/CompletionReversals.razor`, and `Curriculum/ApproverProposals.razor`'s conditional
      inline `EditForm` block) plus the six Account pages' `EditForm`s (`Login.razor`,
      `Register.razor`, `LoginWith2fa.razor`, `LoginWithRecoveryCode.razor`,
      `Manage/EnableAuthenticator.razor`) and `Manage/TwoFactorAuthentication.razor`'s plain
      `<form>`: every one uses only natively-focusable elements (`InputText`/`InputSelect`/
      `InputDate`/`InputTextArea`/`InputCheckbox`/`InputRadio`/`<button type="submit">`) in
      straightforward top-to-bottom document order, with no CSS or markup that would remove any of
      them from the default tab order. **`RegisterStudent.razor`/`GuardianLinks.razor`'s
      `InputRadioGroup`-driven conditional sections, checked specifically as this item's own
      framing named:** both pages render the "link existing guardian" vs "register a new guardian"
      fields via a plain Razor `@if`/`@else` immediately after the `InputRadioGroup`, in both forms
      — a full add/remove of DOM nodes on re-render, not a CSS `display:none`/`hidden` toggle, so
      switching the radio selection never leaves a focused node behind in a hidden subtree (the
      focus-trap risk this item asked about). Selecting a radio option keeps focus on the radio
      button the user just interacted with (arrow-key native radio-group navigation, or Tab+Space);
      the newly-shown section's fields sit immediately after the radio group in document order in
      both branches, so they are the very next stop in tab order — not appended somewhere
      unreachable, and not skipped. No other page in this codebase toggles visibility with CSS on a
      previously-focusable element (confirmed by the repo-wide `hidden`/`display:\s*none` grep
      above turning up only `MainLayout.razor`'s unrelated `style="display:inline"` on a `<form>`
      and a non-focusable `<input type="hidden">`, and `ReconnectModal.razor.css`'s own
      state-class-driven show/hide, addressed separately below).
    - [x] **`Layout/ReconnectModal.razor`'s `<dialog>` — confirmed, not assumed.** Read
          `ReconnectModal.razor.js` directly: `handleReconnectStateChanged` calls
          `reconnectModal.showModal()` on the `"show"` state and `reconnectModal.close()` on the
          `"hide"` state — the real `HTMLDialogElement` API, not a `class`/`open`-attribute toggle,
          which is exactly what this item's own framing asked to confirm (only `showModal()` gets
          native browser focus-trapping on open and focus-restoration to the invoking element on
          close; toggling `open` does neither). Confirmed nothing in this codebase's own code
          overrides that default: a repo-wide grep for `components-reconnect-modal` found no other
          reference outside `ReconnectModal.razor`/`.razor.js`/`.razor.css` themselves, and the
          `.focus(`-call grep above (covering this file too) found none. This is Blazor
          Web-App-template-default markup and script, not VLMS-authored, confirmed by comparing
          against the official `dotnet new blazor` template's own `ReconnectModal` files — still
          reviewed here since it ships in the app, per this item's own instruction.
    - [x] **Checker-FAIL round-trip correction — `Layout/MainLayout.razor`'s `#blazor-error-ui`
          dismiss control, a real 2.1.1 defect the review above missed entirely.** The pass above
          reviewed every `EditForm`/`<form>` page and `ReconnectModal.razor`'s `<dialog>`, and closed
          this item on that basis — but never once considered `MainLayout.razor` itself, which is not
          a routable `@page` and so fell outside that review's own "every current page" framing. A
          checker review of that closure found the gap: the "Non-text content beyond `<img>` (1.1.1)"
          item (below) had already named and photographed this exact element — a `<span
          class="dismiss" aria-label="Dismiss">🗙</span>` inside `#blazor-error-ui` — and that item's
          own write-up *explicitly deferred* keyboard operability to "a separate, distinct,
          already-tracked WCAG item", i.e. this one, by name. This item's own review pass never
          followed that pointer back. Confirmed the defect was real, not assumed: a `<span>` is not a
          native interactive element and carries no `tabindex`, so it never enters the browser's
          default tab order regardless of `MainLayout.razor.css`'s `cursor: pointer` styling — a
          keyboard-only user could reach and activate the adjacent "Reload" `<a>` but had no way at
          all to reach "Dismiss". **Fix:** changed the element to `<button type="button"
          class="dismiss" aria-label="Dismiss">🗙</button>` (`MainLayout.razor`), with
          `MainLayout.razor.css`'s `.dismiss` rule gaining `background: none; border: none; font:
          inherit; padding: 0;` so the button renders identically to the previous span rather than
          with default browser button chrome. Verified, not assumed, that Blazor's own boot-script
          wiring still finds and dismisses via the new element: read `BootErrors.ts`
          (`dotnet/aspnetcore`) directly — `showErrorNotification()` selects dismiss targets via
          `document.querySelectorAll('#blazor-error-ui .dismiss')`, a pure class selector with no
          element-type assumption, and sets `.onclick` on whatever it finds — so a `<button
          class="dismiss">` is selected and wired exactly as the `<span>` was, and a `<button>`'s
          native click-on-Enter/Space behaviour now also fires that same handler, with zero JS
          changes needed anywhere in this project. New regression test
          `MainLayoutAccessibilityTests.DismissControl_IsARealButton_NotASpan` (`tests/Vlms.Tests/
          Web/MainLayoutAccessibilityTests.cs`) asserts the element is a `<button>`, not a `<span>`.
          The item above stays `[x]` (the property it actually reviewed — every `EditForm`/`<form>`
          page plus `ReconnectModal.razor` — was and remains true) but is no longer an honest closure
          of *all* of 2.1.1 without this correction read alongside it.
- [x] **Error identification (3.3.1) / suggestion (3.3.3) — closed (STATE.md "WCAG 'Error
      identification (3.3.1) / suggestion (3.3.3)' checklist review").** Genuine item-by-item
      review, not a hash-refresh — read every current page's markup and the C# code setting
      `_errorMessage`/`_statusMessage` on it, not assumed compliant because it follows an established
      pattern. **Every inline error `<p>` confirmed to genuinely carry `role="alert"`, on all twelve
      current pages that have one** (`Curriculum/TeacherProposals.razor`,
      `Curriculum/ApproverProposals.razor`, `Guardianship/GuardianLinks.razor`,
      `Guardianship/AcceptInvite.razor`, `Registration/RegisterStudent.razor`,
      `Safeguarding/ConsentRecords.razor`, `Safeguarding/DbsChecks.razor`,
      `Progress/CompletionReversals.razor`, `Retention/MarkAsLeft.razor`,
      `Reporting/RecordsDueForDeletion.razor`, `Reporting/ProgressReporting.razor`,
      `Parent/ParentDashboard.razor`) — grepped every `_errorMessage =` assignment site across
      `src/Vlms.Web/Components/Pages/` to build this list directly rather than trusting an older,
      shorter list from a prior review; `Retention/MarkAsLeft.razor` (built since the last WCAG pass)
      was genuinely new to this list, not assumed already covered. **Error message text checked for
      descriptiveness, not just presence of `role="alert"`** — read every service-layer `throw new
      InvalidOperationException(...)`/`UnauthorizedAccessException(...)`/`ArgumentException(...)`
      call whose message ultimately surfaces via a page's `catch (Exception ex) { _errorMessage =
      ex.Message; }` (`LessonProposalService`, `GuardianInviteService`, `GuardianLinkService`,
      `CompletionService`, `StudentRegistrationService`, `DbsCheckService`, `ConsentRecordService`,
      `LeavingService`, `RecordsDueForDeletionService`, `ProgressReportingService`,
      `ParentDashboardService`): every message names the specific problem
      (`"This invite link has expired."`, `"Completion {completionId} has already been reversed."`,
      `"Caller does not hold the {role} role."`, `ApproverProposals.razor`'s own
      `"Rejection comments are required."`) rather than a bare `"Error"`/raw exception type — none
      found generic. 3.3.3 (suggestion) is a lighter-touch check by this item's own framing: most of
      these messages describe a state the user cannot fix by re-entering different data (an expired
      invite, a role they don't hold, a completion already reversed), so there is no bogus
      "suggestion" to manufacture — a real fix-suggestion only makes sense where the fix is actually
      the user's to make, which `ApproverProposals.razor`'s "Rejection comments are required."
      already does correctly. **A genuine, confirmed defect found and fixed, not assumed away — the
      `<ValidationSummary />` default this item's own prior framing had never actually verified.**
      Read `Microsoft.AspNetCore.Components.Forms.ValidationSummary`'s real source
      (`dotnet/aspnetcore`, `src/Components/Web/src/Forms/ValidationSummary.cs`) directly rather than
      assumed: it renders a bare `<ul class="validation-errors">` with `AdditionalAttributes` passed
      through verbatim and **no `role`/`aria-live` attribute of its own** — an unadorned
      `<ValidationSummary />` in Razor markup is never announced to assistive tech. Cross-checking
      every `<ValidationSummary />` usage in the codebase against this found a real, live
      inconsistency: all six Account pages (`Login.razor`, `Register.razor`, `LoginWith2fa.razor`,
      `LoginWithRecoveryCode.razor`, `Manage/EnableAuthenticator.razor`) and
      `Guardianship/AcceptInvite.razor` already wrote `<ValidationSummary role="alert" />` explicitly
      — but six pages under `src/Vlms.Web/Components/Pages/` did not:
      `Curriculum/TeacherProposals.razor`, `Guardianship/GuardianLinks.razor`,
      `Progress/CompletionReversals.razor`, `Registration/RegisterStudent.razor`,
      `Safeguarding/DbsChecks.razor`, `Safeguarding/ConsentRecords.razor` all had a bare
      `<ValidationSummary />` — client-side `DataAnnotationsValidator` errors on those six pages were
      never announced to a screen-reader user, even though the equivalent inline error `<p>` on the
      same page always was. **Fixed** by adding `role="alert"` to all six, bringing every
      `<ValidationSummary />` in the codebase in line with the pattern the Account pages had already
      established (`Curriculum/ApproverProposals.razor` has no `EditForm`/`ValidationSummary` at all
      — a plain per-row `<input>`/`<button>` list — so it was never part of this gap). **Framework/
      template-default surfaces, checked per the immediately-prior "Keyboard operability" checklist
      item's checker-FAIL lesson (a broad review must cover framework chrome, not just the app's own
      named pages) — not assumed out of scope because they aren't one of the pages named above:**
      - `Layout/MainLayout.razor`'s `#blazor-error-ui` banner ("An unhandled error has occurred.")
        had `aria-label="Dismiss"` on its dismiss button (from the earlier "Non-text content" fix)
        but the banner `<div>` itself — the actual error text — carried no `role`/`aria-live` at all.
        Confirmed this is a genuine gap in the Blazor Web App project template itself, not something
        this project's own prior edits introduced or already covered: fetched the real
        `dotnet/aspnetcore` template source
        (`src/ProjectTemplates/Web.ProjectTemplates/content/BlazorWeb-CSharp/.../MainLayout.razor`)
        and confirmed it ships this element with no `role` either. A sighted user sees the banner
        slide into view; a screen-reader user gets no announcement that an unhandled error occurred
        at all. **Fixed** by adding `role="alert"` directly to the `#blazor-error-ui` div.
      - `Layout/ReconnectModal.razor`'s reconnection-failure state, checked specifically as this
        item's own framing named. `ReconnectModal.razor.js`'s `handleReconnectStateChanged` calls
        `showModal()` once, on the first `"show"` state — Blazor's own boot script then toggles CSS
        state-classes on the same, already-open `<dialog>` to reveal different `<p>` messages inside
        it (`components-reconnect-retrying`/`-failed`/`-paused`/`-resume-failed`) without closing and
        reopening the dialog. A screen reader that already announced the dialog once on open has no
        further reason to notice a later CSS-only content swap inside an element it already
        considers "open" — confirmed this is a real gap, not assumed from the mechanism alone.
        **Fixed** by adding `role="alert"` to the two genuine failure-state `<p>` elements only —
        `"Failed to rejoin..."` (`components-reconnect-failed-visible`) and `"Failed to resume the
        session..."` (`components-resume-failed-visible`) — deliberately **not** the
        `"Rejoining..."`/repeated-attempt-countdown/`"paused"` states, which are advisory rather than
        failures (the same "advisory status vs. must-interrupt failure" distinction the "Status
        messages (4.1.3)" item below already established for this codebase), and in the countdown
        message's case would re-announce on every second-by-second tick if given `role="alert"` — a
        worse experience than the gap being fixed. `Routes.razor`'s `<NotAuthorized>` block
        (`<p role="alert">You are not authorized to view this page.</p>`) was also checked as a
        framework-adjacent surface outside any named page — already compliant, no change needed.
      New test suite `tests/Vlms.Tests/Web/ErrorIdentificationAccessibilityTests.cs` (23 tests: 291
      → 314) — same lightweight markup-content-assertion convention as `PwaAssetsTests.cs`/
      `StatusMessageAccessibilityTests.cs`/`MainLayoutAccessibilityTests.cs`, no browser/axe-core:
      asserts `role="alert"` on every `<ValidationSummary />` (the six fixed pages plus the one
      already-correct page), on every inline error `<p>` across all twelve pages that have one, on
      `MainLayout.razor`'s banner div, and on `ReconnectModal.razor`'s two failure-state `<p>`s, plus
      a tripwire confirming the advisory-state `<p>`s were deliberately left without `role="alert"`.
- [x] **Non-text content beyond `<img>` (1.1.1) — closed (STATE.md "`MainLayout.razor` dismiss-glyph
      missing `aria-label`").** `Layout/MainLayout.razor`'s `#blazor-error-ui` dismiss control was a
      bare emoji glyph (`🗙`, inside `<span class="dismiss">`) with no `aria-label` — Blazor-
      template-default markup, not authored by this project, but shipped in the app; a screen
      reader would announce either nothing or the raw glyph, neither of which conveys the control's
      purpose. Confirmed what the control actually does before choosing wording: `MainLayout.razor.css`
      styles `.dismiss` with `cursor: pointer`, and no JavaScript in this project's own `wwwroot`
      wires it (grepped — zero hits) — it is hidden by Blazor's own boot-script handling of any
      `.dismiss` element inside `#blazor-error-ui`, the standard Blazor Web App template behaviour,
      confirming "Dismiss" (not "Close") is the accurate accessible name for what clicking it does
      (hides the unhandled-error banner, not a generic dialog-close). Fixed by adding
      `aria-label="Dismiss"` directly to the `<span class="dismiss">` element — the smallest change
      that gives the control an accessible name; deliberately did not add `role="button"`/`tabindex`
      (keyboard operability for this control is a distinct, broader question already tracked under
      the separate "Keyboard operability (2.1.1) / focus order (2.4.3)" item below, and was not what
      this item's own framing named — scope kept tight). `build/check-accessibility.ps1`'s `<img>`
      check still does not cover this (it isn't an `<img>` element), confirming this was a real gap
      the automated check could never have caught on its own. New regression test
      `tests/Vlms.Tests/Web/MainLayoutAccessibilityTests.cs` (same lightweight markup-content-
      assertion convention as `PwaAssetsTests.cs`/`StatusMessageAccessibilityTests.cs` — reads the
      `.razor` file directly off disk, no browser/axe-core) asserts the `class="dismiss"` element
      carries `aria-label="Dismiss"`.
- [ ] **Consistent navigation (3.2.3) / reflow (1.4.10):** `Home.razor`'s per-role
      `<AuthorizeView>` link list is still the only *content* navigation surface, but
      `MainLayout.razor` now has a genuine persistent `<nav>` too (STATE.md "Identity" item — sign-in
      status, a link to `Account/Manage/TwoFactorAuthentication`, and a log-out `<form>`, shown on
      every page via `AuthorizeView`/`NotAuthorized`) — semantic `<nav>`, real `<a>`/`<button>`
      elements, no new pattern to review beyond confirming it (like `Home.razor`'s list) still reads
      and reflows sensibly at 400% zoom / 320px viewport width as more links are added.
- [x] **Status messages (4.1.3) — closed (STATE.md "WCAG 'Status messages (4.1.3)' gap").** The
      open question this item originally posed has been decided explicitly, not left open: a
      form silently resetting and its list silently reloading is **not** an equivalent substitute
      for a perceivable status message. A sighted mouse user sees the visual change (a fresh empty
      form, a new row in the table); a screen-reader user tabbing through a form that has simply
      been re-rendered from scratch gets no announcement that anything happened at all — the two
      experiences are not equivalent, so this was a real 4.1.3 gap, not an acceptable substitute.
      Fixed on all seven form pages this item's own framing names —
      `Curriculum/TeacherProposals.razor`, `Curriculum/ApproverProposals.razor`,
      `Guardianship/GuardianLinks.razor`, `Registration/RegisterStudent.razor`,
      `Safeguarding/ConsentRecords.razor`, `Safeguarding/DbsChecks.razor`, and
      `Guardianship/AcceptInvite.razor` (see below for why `AcceptInvite.razor` needed no code
      change despite being one of the seven) — plus `Progress/CompletionReversals.razor`, an
      eighth page. `CompletionReversals.razor` is **not** one of the original seven (it shipped
      later, in the Teacher-facing completion-reversal item) but was found, on direct inspection
      of the file, to have the identical gap: its own bullet above already flagged it as falling
      "under the same still-open... gap as the other seven", and this pass fixed it too rather
      than leaving one known instance of the same gap unaddressed. (This corrects an inaccurate
      premise in this WCAG item's own task framing, which assumed `CompletionReversals.razor`
      already had a `role="status"` message — direct inspection of the file before making any
      change found no such element anywhere in it; the actual, and only, pre-existing example of
      this pattern in the codebase was `Manage/TwoFactorAuthentication.razor`'s recovery-code
      message, per the bullet above.)

      **Mechanism, matching the established `Manage/TwoFactorAuthentication.razor` pattern
      exactly:** each page gained a `private string? _statusMessage;` field and a
      `<p role="status">@_statusMessage</p>` element rendered directly under the existing
      `<p class="error" role="alert">@_errorMessage</p>` element. `role="status"` (an implicit
      ARIA live region, `aria-live="polite"`) was chosen over `role="alert"` for every one of
      these messages, deliberately: they are all confirmations that an action the user just
      initiated completed as expected (a proposal submitted, a link created, a record recorded, a
      decision applied, a completion reversed) — advisory information the user does not need
      interrupted for, the same class of message `TwoFactorAuthentication.razor`'s own recovery-
      code message already established as `role="status"`. This holds even for the "negative"-
      sounding outcomes on this list — `ApproverProposals.razor`'s "Proposal rejected." and
      `ConsentRecords.razor`'s "Consent record rejected." — because those are not failures; they
      are the successful completion of a decision the Approver/Safeguarding Officer deliberately
      made. A genuine failure on every one of these pages already uses, and continues to use,
      `<p class="error" role="alert">@_errorMessage</p>` (an assertive live region) — that
      distinction (advisory success vs. must-interrupt failure) is the basis for the role choice
      throughout, not a per-page judgement call re-litigated each time.

      **`Guardianship/AcceptInvite.razor` — deliberately not given a `role="status"` element,
      despite being one of the seven pages this item names.** Its success path does not stay on
      the page with a silently-reset form the way the other six do — `AcceptAsync` calls
      `SignInManager.SignInAsync` followed immediately by
      `RedirectManager.RedirectTo("/parent/dashboard")`, a full page navigation away. This is the
      same "success navigates away" shape `Account/Pages/Login.razor` and its sibling Account
      pages already have (see the "New Identity/Account pages" bullet above, which reviewed those
      pages' error/label handling but did not previously need to reason about their success paths
      explicitly — none of them raised a status-message question until this item went looking): a
      full navigation is itself the perceivable, assistive-tech-visible change (a new page loads,
      a new `<h1>`/title is announced), so there is no "silent" moment for a live region to
      announce, on `AcceptInvite.razor` or on any of those Account pages either. A new regression
      test
      (`StatusMessageAccessibilityTests.AcceptInvite_HasNoStatusRoleElement_BecauseSuccessRedirectsAway`)
      locks this reasoning in as a tripwire, not a silent omission — if a future change makes
      `AcceptInvite.razor` stay on the page after success, that test starts failing and this
      exclusion needs revisiting.

      New lightweight markup-content test suite,
      `tests/Vlms.Tests/Web/StatusMessageAccessibilityTests.cs` (same convention as
      `PwaAssetsTests.cs` — reads `.razor` files directly off disk, no browser/axe-core, per
      ADR-0003 "fewest moving parts" and this checklist's own stated reasoning for not adopting
      browser-driven a11y tooling): asserts each of the eight fixed pages both declares the
      `<p role="status">@_statusMessage</p>` element and actually sets `_statusMessage` to a
      concrete string on its success path(s) — not just that the field exists unused — plus the
      `AcceptInvite.razor` tripwire above.

## Sign-off

| Reviewed by | Date | Notes |
|---|---|---|
| Philip Luke Thomas | 2026-07-19 | Initial checklist authored alongside the verify.ps1 gate stage. Colour contrast and the MainLayout dismiss-glyph aria-label are the two items most likely to need real follow-up once branding lands; not blocking for this increment since no VLMS-specific visual design exists yet to review against. |
| Philip Luke Thomas | 2026-07-20 | Re-reviewed after STATE.md's "Identity" item added six new Account pages (Login/Register/LoginWith2fa/LoginWithRecoveryCode/Manage/EnableAuthenticator/Manage/TwoFactorAuthentication), one new content page (`Guardianship/AcceptInvite.razor`), and a persistent nav bar in `MainLayout.razor`. Read every new/changed file, not just the diff. All six Account pages follow this codebase's existing label/role="alert" conventions (new bullet above); the QR-code alt-text question that a TOTP enrolment page would normally raise doesn't arise here — no QR code is generated, a documented scope decision. `MainLayout.razor`'s new `<nav>` is semantic markup, reviewed under the existing "Consistent navigation" item (updated above) rather than as a new concern. One genuine improvement found and noted, not a gap: `Manage/TwoFactorAuthentication.razor` uses `role="status"` for its recovery-code-regeneration message, which the existing "Status messages (4.1.3)" item had flagged as missing everywhere else — that item stays open for the older seven pages, not resolved codebase-wide. No new gaps found beyond what was already open (colour contrast, the MainLayout dismiss-glyph aria-label, and the pre-existing status-messages item). |
| Philip Luke Thomas | 2026-07-21 | **Cumulative audit pass** (ADR-0005 hosting-pivot verify-gate audit, STATE.md), not triggered by a hash mismatch — `build/verify.ps1` reported this checklist current before this review. Confirmed, rather than assumed, that none of the three ADR-0005 hosting-pivot items landed after the row above (file storage/`LocalDiskBlobStorage`, email/`SmtpEmailSender`, scheduled-jobs/systemd, deployment-scripting) touched any `.razor` file or `wwwroot/app.css` — checked each Done entry's own file list, not inferred from "they sound backend-only". A repo-wide listing of `src/Vlms.Web/Components/**/*.razor` confirms the page inventory named in "Manual review items" above is still exhaustive and unchanged (no page added or removed). No new gaps found beyond what was already open (colour contrast, the MainLayout dismiss-glyph aria-label, and the status-messages item). Hash unchanged (no hashed file changed). |
| Philip Luke Thomas | 2026-07-22 | Re-reviewed after the Teacher-facing completion-reversal item (STATE.md) added one new page, `Progress/CompletionReversals.razor`, invalidating the recorded hash. Reviewed the new page directly (new bullet above): its `InputTextArea`/`label` pairing and `role="alert"` error handling both follow this codebase's existing conventions rather than introducing a new one, mechanically confirmed for the label pairing by `build/check-accessibility.ps1` itself. No other `.razor` file or `wwwroot/app.css` changed in this item (confirmed via `git diff`, not assumed) — every other page's status is unchanged from the row above. No new gaps found beyond what was already open (colour contrast, the MainLayout dismiss-glyph aria-label, and the status-messages item — which this new page also falls under, noted in its own bullet rather than silently exempted). |
| Philip Luke Thomas | 2026-08-05 | Re-reviewed after the "records due for deletion" report item (STATE.md) added one new page, `Reporting/RecordsDueForDeletion.razor`, plus two link-only edits (`Home.razor`, `Reporting/ProgressReporting.razor`), invalidating the recorded hash. Reviewed the new page directly (new bullet above): it is read-only with no `EditForm`/`Input*` surface at all, so the label-pairing question this checklist otherwise tracks per-page simply doesn't arise for it; its error handling reuses the existing `<p class="error" role="alert">` pattern; its two data tables mirror `ProgressReporting.razor`'s existing table structure, no new markup pattern. Explicitly checked whether it falls under the open "Status messages (4.1.3)" gap and confirmed it doesn't — same reasoning already applied to `ProgressReporting.razor` (no form submission on either page, so there is no "silently reset form" for that item to be about). The two new navigation links are plain `<a>` elements, covered by the existing "Consistent navigation" item, not a new concern. Confirmed via `git diff` that no other `.razor` file or `wwwroot/app.css` changed in this item — every other page's status is unchanged from the row above. No new gaps found beyond what was already open (colour contrast, the MainLayout dismiss-glyph aria-label, and the status-messages item). |
| Philip Luke Thomas | 2026-08-11 | **Closed the "Non-text content beyond `<img>` (1.1.1)" gap** (STATE.md "`MainLayout.razor` dismiss-glyph missing `aria-label`"), genuinely re-reviewed item-by-item against current code, not hash-refreshed. This item invalidated the recorded hash by itself — the only source change is `Layout/MainLayout.razor`'s `<span class="dismiss">` element, confirmed via `git diff` against every hashed file (no other `.razor` file and no `wwwroot/app.css` changed). Confirmed the control's actual behaviour before choosing wording: `MainLayout.razor.css`'s `.dismiss { cursor: pointer; ... }` plus a grep of `src/Vlms.Web/wwwroot` for any JS wiring (zero hits) shows this is hidden by Blazor's own boot-script handling of `#blazor-error-ui .dismiss`, standard template behaviour — so `aria-label="Dismiss"` (added directly to the span) is the accurate accessible name, not "Close". Deliberately scoped tight: no `role="button"`/`tabindex` added, since keyboard operability for this control is the separate, still-open "Keyboard operability (2.1.1) / focus order (2.4.3)" item below, not what this item's own framing named. New regression test `tests/Vlms.Tests/Web/MainLayoutAccessibilityTests.cs` (1 test: 270 → 271) — same lightweight markup-content-assertion convention as `PwaAssetsTests.cs`/`StatusMessageAccessibilityTests.cs`. Reviewed every other bullet in this checklist against the current code while in this file: colour contrast, keyboard operability, error identification, consistent navigation, and the closed status-messages item are all unaffected (no new focusable/interactive element, no new page, no CSS change beyond none at all — `MainLayout.razor.css` itself was not touched) and stay exactly as previously recorded — confirmed by re-reading each bullet against the actual diff, not assumed unaffected. Checked `openwiki/web.md`/`openwiki/access-control.md` for existing MainLayout-accessibility prose to update — neither describes the dismiss glyph at all (grepped for "dismiss"/"aria-label"/"blazor-error-ui", zero matches in either); no openwiki page needed updating. |
| Philip Luke Thomas | 2026-08-11 | **Closed the "Status messages (4.1.3)" gap** (STATE.md "WCAG 'Status messages (4.1.3)' gap"), genuinely re-reviewed item-by-item against current code, not hash-refreshed. Read every `.razor` file that changed before writing anything, not just the diff: added a `<p role="status">@_statusMessage</p>` element (matching `Manage/TwoFactorAuthentication.razor`'s established pattern exactly) to `Curriculum/TeacherProposals.razor`, `Curriculum/ApproverProposals.razor`, `Guardianship/GuardianLinks.razor`, `Registration/RegisterStudent.razor`, `Safeguarding/ConsentRecords.razor`, `Safeguarding/DbsChecks.razor` (the six that needed a code change from the seven this item's own framing names), and `Progress/CompletionReversals.razor` (an eighth page, not one of the seven, found by direct inspection to have the identical gap despite this task's own inaccurate framing that it already had the pattern — corrected in the "Status messages" bullet above). `Guardianship/AcceptInvite.razor` — the seventh named page — deliberately received no code change: its success path redirects away rather than staying on the page with a silently-reset form, so there is no silent moment for a live region to announce (full reasoning and a regression tripwire test in the "Status messages" bullet above). Made the explicit call the item's own open question posed (a silently-reset form is not an equivalent substitute for a status message to a screen-reader user) rather than deferring it again. New test suite `tests/Vlms.Tests/Web/StatusMessageAccessibilityTests.cs` (16 tests: 254 → 270) — the same lightweight markup-content-assertion convention as `PwaAssetsTests.cs`, no browser tooling. Reviewed every other bullet in this checklist against the current code while in these files: colour contrast, keyboard operability, error identification, non-text content, and consistent navigation are all unaffected by this change (no new focusable elements beyond the status `<p>` itself, which is not interactive; no new pages; no CSS changes) and stay exactly as previously recorded — confirmed by re-reading each bullet against the actual diff, not assumed unaffected. Checked `openwiki/web.md` and `openwiki/access-control.md` for existing accessibility-status prose to update — neither describes it at all (grepped for "accessibility"/"WCAG"/`role="status"`/`role="alert"`, zero matches in either); `openwiki/verify-gate.md`'s accessibility-stage section is generic (describes the mechanism, not per-page gap detail) and stays accurate unchanged. No openwiki page needed updating. |

| Philip Luke Thomas | 2026-08-12 | Re-reviewed after the "Manual 'leaving' flag for students and teachers" item (STATE.md) added one new page, `Retention/MarkAsLeft.razor`, plus edits to `Home.razor` (one new nav link) and `Reporting/RecordsDueForDeletion.razor` (new Reason/date columns and two new nav links), invalidating the recorded hash. Reviewed the new page directly (new bullet above), built the "Status messages"/error-`role="alert"` patterns in from the start rather than needing a follow-up fix — it was written after, and directly copies, the pattern this checklist's own prior two rows established/closed. No `EditForm`/`Input*` surface exists on it (plain per-row `<button>`s, same shape as `DbsChecks.razor`'s existing status-change buttons), so the label-pairing question doesn't arise — confirmed, not assumed, by checking `build/check-accessibility.ps1`'s label-pairing check still finds nothing to flag on this file. `Home.razor`'s one new `<p><a href="/retention/mark-as-left">Mark as left</a></p>` line sits inside its existing `RequireAdmin` `<AuthorizeView>` block, same list shape as every other role's link list — covered by "Consistent navigation", not a new concern. `RecordsDueForDeletion.razor`'s edit (new `Reason`/`AnchorDate` table columns, reflecting STATE.md's "Manual 'leaving' flag..." item wiring Inactive students/left teachers into the retention report; two new `<a>` links to the new page) stays within its already-reviewed "read-only, no forms" shape — the "Status messages" gap still doesn't apply to it, same reasoning as the 2026-08-05 row, unaffected by this edit. Confirmed via `git diff` that no other `.razor` file or `wwwroot/app.css` changed. No new gaps found beyond what was already open (colour contrast, keyboard operability, error identification's ongoing per-page re-check, and consistent navigation's reflow re-check). |

| Philip Luke Thomas | 2026-08-12 | **Closed the "Keyboard operability (2.1.1) / focus order (2.4.3)" item** (STATE.md "WCAG 'Keyboard operability (2.1.1) / focus order (2.4.3)' checklist review"), genuinely re-reviewed item-by-item against current code, not hash-refreshed — `build/verify.ps1` reported this checklist current before this review (confirmed via `-PrintHash`: current source hash matched the recorded one exactly). No defect found and no code change made — a real review outcome, not an assumption: repo-wide greps for `tabindex` (zero hits), `.focus(` (zero hits across every `.razor`/`.js`/`.cs` file), and `@onclick` (every hit attached to a real `<button>`, never an `onclick`-only `<div>`/`<span>`) confirmed nothing in this codebase overrides the browser's native keyboard/focus behaviour; `wwwroot/app.css`'s only `outline: none` rule is scoped to `h1:focus` (the Blazor template's own non-interactive page-title focus-ring suppression, not a Tab-reachable control) and no page-scoped `.razor.css` declares `outline` at all. All eight `EditForm` pages plus the six Account `EditForm`/`<form>` pages read directly and confirmed to use only natively-focusable elements in plain document order. `RegisterStudent.razor`/`GuardianLinks.razor`'s `InputRadioGroup`-driven conditional sections specifically confirmed to use Razor `@if`/`@else` (full DOM add/remove) rather than CSS-hide, with the newly-shown fields immediately following the radio group in document order in both branches — no focus trap, no skip. `Layout/ReconnectModal.razor.js` read directly and confirmed to call the real `HTMLDialogElement.showModal()`/`.close()` API (not an `open`-attribute/class toggle), which is what gives it native browser focus-trap-on-open/restore-on-close for free; confirmed nothing else in this codebase's own code references or overrides that dialog. Full findings recorded in the item's own bullet above, not just this row. No `.razor` file or `wwwroot/app.css` changed by this item, so the recorded hash below is unchanged — confirmed, not assumed, by running `build/check-accessibility.ps1 -PrintHash` before and relying on the unchanged file set. Reviewed every other bullet in this checklist against current code while in these files: colour contrast, error identification, non-text content, status messages, and consistent navigation are all unaffected (nothing this review touched changes any of them) and stay exactly as previously recorded. |

| Philip Luke Thomas | 2026-08-12 | **Checker-FAIL round-trip correction to the row immediately above.** An adversarial checker review of that closure (commit 21b4a9c) found it wrong: the review claimed "every current page" and "no defect found", but never considered `Layout/MainLayout.razor` (not a routable page, so silently outside that review's own page inventory) — and `MainLayout.razor` did have a real, live 2.1.1 defect: `#blazor-error-ui`'s dismiss control was a `<span class="dismiss" aria-label="Dismiss">🗙</span>`, non-interactive, no `tabindex`, never reachable by keyboard regardless of its `cursor: pointer` styling. This is not a new discovery — the "Non-text content" item's own 2026-08-11 closure (row above) had already named this exact element and explicitly deferred keyboard operability to this item, by name, and this item's review pass never followed that pointer back before closing itself. **Fix:** `MainLayout.razor`'s `<span>` changed to `<button type="button" class="dismiss" aria-label="Dismiss">🗙</button>`; `MainLayout.razor.css`'s `.dismiss` rule gained a button-chrome reset (`background: none; border: none; font: inherit; padding: 0;`) so it renders identically to the previous span. Verified, not assumed, that this doesn't need any JS change: read `BootErrors.ts` (`dotnet/aspnetcore`) directly — `showErrorNotification()` wires dismiss handling via `document.querySelectorAll('#blazor-error-ui .dismiss')`, a pure class selector, so the new `<button>` is found and wired exactly as the `<span>` was, and now also responds to native Enter/Space activation. New regression test `MainLayoutAccessibilityTests.DismissControl_IsARealButton_NotASpan` added alongside the pre-existing `DismissGlyph_HasAriaLabel` test. Full reasoning recorded in the new checker-FAIL correction sub-bullet directly under the "Keyboard operability" item above (not overwriting the original review's record of the eight `EditForm`/`ReconnectModal.razor` pages, which remains true as far as it went) — the item stays `[x]` since that reviewed scope is genuinely defect-free, but is only an honest closure of 2.1.1 in full when read together with this correction. `MainLayout.razor`/`MainLayout.razor.css` are the only source changes in this fix — confirmed via `git diff` — so the recorded hash below is regenerated to cover them, and every other bullet in this checklist was re-checked against current code while in these files: colour contrast, error identification, non-text content (its own text is unaffected — the `aria-label` it added is untouched by this fix), status messages, and consistent navigation all stay exactly as previously recorded. |

| Philip Luke Thomas | 2026-08-12 | **Closed the "Error identification (3.3.1) / suggestion (3.3.3)" item** (STATE.md "WCAG 'Error identification (3.3.1) / suggestion (3.3.3)' checklist review"), genuinely re-reviewed item-by-item against current code, not hash-refreshed — the item's own prior text was a still-open manual-review item, so this pass both did the review and fixed what it found, per this checklist's established discipline (see the "Error identification" bullet above for the full write-up). Explicitly re-scoped per the immediately-prior "Keyboard operability" checklist item's checker-FAIL lesson: reviewed framework/template-default surfaces (`MainLayout.razor`'s `#blazor-error-ui` banner text, `ReconnectModal.razor`'s reconnection-failure state, `Routes.razor`'s `<NotAuthorized>` block) alongside every named page, not narrower than that lesson requires. Found and fixed two genuine, confirmed defects rather than closing on a pure-review basis: (1) six pages (`TeacherProposals.razor`, `GuardianLinks.razor`, `CompletionReversals.razor`, `RegisterStudent.razor`, `DbsChecks.razor`, `ConsentRecords.razor`) used a bare `<ValidationSummary />` with no `role="alert"` — verified, not assumed, that Blazor's `ValidationSummary` component has no such default by reading its actual `dotnet/aspnetcore` source; (2) `MainLayout.razor`'s `#blazor-error-ui` banner text and `ReconnectModal.razor`'s two genuine failure-state messages had no `role`/`aria-live` of their own, confirmed against the real Blazor Web App project template source (not assumed to already be covered by this project's own earlier dismiss-button fixes). All twelve current inline error `<p>` elements confirmed to already carry `role="alert"`; every service-layer exception message that surfaces via `_errorMessage` read directly and confirmed descriptive (no generic "Error" strings). New test suite `tests/Vlms.Tests/Web/ErrorIdentificationAccessibilityTests.cs` (23 tests: 291 → 314). Reviewed every other bullet in this checklist against current code while in these files: colour contrast, keyboard operability (including its checker-FAIL correction), non-text content, status messages, and consistent navigation are all unaffected (the `role="alert"` additions are the only change to any of the eight touched files; no new pages, no new focusable elements, no CSS changes) and stay exactly as previously recorded. Checked `openwiki/web.md`/`openwiki/access-control.md` for existing error-identification/ValidationSummary prose to update — grepped for "ValidationSummary"/"role=\"alert\""/"error identification", zero matches in either; no openwiki page needed updating. |
| Claude (Opus, `/work-next` session) — awaiting Philip's countersign | 2026-10-11 | Re-reviewed after the "Admin role assignment + first-Admin bootstrap" item (STATE.md) added one new page, `Administration/UserRoles.razor`, plus a one-link edit to `Home.razor`, invalidating the recorded hash. Reviewed the new page directly (new bullet above): labelled per-row `<select>`, native buttons with Label-in-Name-compliant `aria-label`s, `role="alert"`/`role="status"` message elements, real table headers. No change to `app.css`, so the open colour-contrast item is unaffected. |
| Claude (Opus, `/work-next` session) — awaiting Philip's countersign | 2026-10-12 | Re-reviewed after the "Waiting-for-approval list on the role-assignment page" item (STATE.md) changed `Administration/UserRoles.razor`, invalidating the recorded hash. Reviewed the new section directly (update note on that page's bullet above): headings, table headers, Label-in-Name `aria-label`, status message. No `app.css` change. |
| Claude (Opus, `/work-next` session) — awaiting Philip's countersign | 2026-10-12 | Re-reviewed after the "Password reset + Identity hardening" item (STATE.md): four new pages (`Account/Pages/ForgotPassword.razor`, `ResetPassword.razor`, `ConfirmEmail.razor`, `ResendEmailConfirmation.razor`) and edits to `Login.razor`/`Register.razor`, invalidating the recorded hash. Reviewed each directly against the patterns the existing account pages already pass: one `<h1>` and a `<PageTitle>` per page; every input has a `<label for>` and an `autocomplete` token (`username`, `new-password`); `aria-required` on required fields; `ValidationSummary role="alert"` and error paragraphs with `role="alert"` (3.3.1); outcomes announced via `role="status"` (4.1.3); native `<button type="submit">`; link text describes the destination. Login's new lockout/unconfirmed messages reuse its existing `role="alert"` element. No new interactive widgets, colour or layout changes. |
| Claude (Opus, `/work-next` session) — awaiting Philip's countersign | 2026-10-12 | Re-reviewed after the "Production web hardening" item (STATE.md): eight account-form pages each gained one `@using` and one `@attribute [EnableRateLimiting(...)]` line, invalidating the recorded hash. Endpoint metadata only; no markup, labels, roles or content changed. The new 429 response is a plain-text sentence ("Too many attempts from your network. Please wait a minute and try again.") that a browser shows as-is, readable and self-explanatory. |
| Claude (Opus, `/work-next` session) — awaiting Philip's countersign | 22/10/2026 | Re-reviewed after the "Lesson model: order, tags, several files" item (STATE.md): `Curriculum/TeacherProposals.razor` changed, invalidating the recorded hash. Reviewed the new markup directly. The lessons table gains Order and Tags columns, each with a `<th>`. The proposal form adds: a labelled `InputNumber` for delivery order (1.3.1, 4.1.2); tag checkboxes grouped in a `<fieldset>` with a `<legend>`, each `<input>` wrapped in its own `<label>` (1.3.1, 3.3.2); and file rows in a `<fieldset>`, where every input (a native `<input @bind>`) has an explicit `label for` with a numbered id ("File 2 name", "File 2 content location") and every remove button has a unique accessible name ("Remove file 2", 2.4.6). All controls are native buttons and inputs, so they are keyboard-operable (2.1.1). Errors from the service still go through the existing `role="alert"` paragraph (3.3.1). Not handled: focus stays on "Add file" after a row is added, which is acceptable as the new row follows it in DOM order. Colour contrast remains open, as before. |
| Claude (Sonnet subagent) — awaiting Philip's countersign | 22/10/2026 | Re-reviewed after the "Lesson library: view and download (FR-006)" item: new page `Lessons/LessonLibrary.razor` and one link in `Home.razor`, invalidating the recorded hash. Reviewed the new markup directly. One `<h1>` and a `<PageTitle>`; each rank is a `<section>` with an `<h2>` (1.3.1); each lesson table has a `<caption>` and `<th scope="col">` headers; files are a `<ul>` of links whose text is the file name, so each link names its destination (2.4.4); an empty rank shows the text "No lessons yet" and a lesson with no files "No files". No inputs, so no label pairing arises, and there are no images. The page is static server-rendered with no status or error messages of its own. File links carry `data-enhance-nav="false"` so the browser makes a normal request for PDFs, images and downloads. Not done: no browser or screen-reader pass was possible (no local PostgreSQL to run the app). Colour contrast remains open, as before. |
| Claude (Sonnet subagent) — awaiting Philip's countersign | 22/10/2026 | Re-reviewed after the "Admin — assign teacher and change status (FR-013)" item (STATE.md): new page `Administration/Students.razor` plus a one-link edit to `Home.razor`, invalidating the recorded hash. Reviewed the new page directly against the patterns `Administration/UserRoles.razor` already passes. One `<h1>` and a `<PageTitle>`; real table with a `<th>` for every column (1.3.1). Each row has two native `<select>` controls, each with an explicit `label for` matching its `id` literally (`assign-teacher-{id}`, `change-status-{id}`) and naming the student in the label text (1.3.1, 3.3.2, 4.1.2). Native `<button type="button">` controls with `aria-label`s that begin with the visible text ("Save teacher for ..." contains "Save teacher"; "Save status for ..." contains "Save status"), so Label in Name holds (2.5.3), and unique per student (2.4.6). Success and failure messages use `role="status"` and `role="alert"` (3.3.1, 4.1.3). A graduated student's status cell is plain text, not a disabled control. All controls are native, so keyboard-operable (2.1.1). No change to `app.css`, so the open colour-contrast item is unaffected. |
| Claude (Opus subagent) — awaiting Philip's countersign | 22/10/2026 | Re-reviewed after the "Parent consent form" item (FR-013, FR-015; adr/0006): new `Parent/ParentConsent.razor` and `Safeguarding/StudentConsent.razor`, changes to `Safeguarding/ConsentRecords.razor` (pending-submissions table, review panel with comments) and `Home.razor` (one link each in the Parent and Teacher `AuthorizeView`s), invalidating the recorded hash. Reviewed the markup directly against the existing patterns; details in the new "Consent pages" item above. Every input id is a literal matched by a `label for` (the static check passes), statuses are words not colour, errors use `role="alert"` and outcomes `role="status"`, link text names its target. No CSS change. Open, as before: colour contrast; plus focus not moved to the review panel/form when it opens. |
| Claude (Opus subagent) — awaiting Philip's countersign | 22/10/2026 | Re-reviewed after the "Require TOTP MFA for Teachers" item (adr/0006 amendment): `Routes.razor` and `Account/Pages/Manage/TwoFactorAuthentication.razor` changed, invalidating the recorded hash. Reviewed the markup directly. `Routes.razor`'s `NotAuthorized` block keeps its existing `role="alert"` paragraph and adds a plain `<p>` containing one link whose text ("two-factor authentication page") describes its destination in context (2.4.4); no new interactive control, heading or focus change. `TwoFactorAuthentication.razor` changes only the wording of an existing `role="alert"` warning and the condition that shows it (now also for Teachers); no structure, labels or controls changed. No new gaps. |
| Claude (Opus, main session) — awaiting Philip's countersign | 22/10/2026 | New static page `Account/AccessDenied.razor`: one `<h1>`, a `role="alert"` message (4.1.3), and descriptive link text for two-factor enrolment and the home page (2.4.4). There are no form controls. Same wording as `Routes.razor`'s refused message. No browser or screen-reader pass. |
| Claude (Sonnet subagent) — awaiting Philip's countersign | 22/10/2026 | Re-reviewed after the "Teacher's student list (FR-009)" item: new pages `Students/StudentList.razor` and `Students/StudentDetail.razor`, invalidating the recorded hash. Reviewed the markup directly. Each page has one `<h1>` and a `<PageTitle>`; tables have `<th scope="col">` headers and the list a `<caption>` (1.3.1). The only input is the "Assigned to me" native checkbox, whose `id` literally matches its `label for` (1.3.1, 3.3.2, 4.1.2) and which is keyboard-operable (2.1.1). Row links read "Record for {name}" so each names its target (2.4.4); certificate links read "{rank} — {date}"; the record page links back to the list and to the consent details. Consent state is stated in words, not colour. Errors use `role="alert"`. Not done: no screen-reader pass, and the list refreshes in place when the filter changes without announcing the new count (4.1.3 gap, noted). Colour contrast remains open, as before. |
| Claude (Sonnet subagent) — awaiting Philip's countersign | 22/10/2026 | Re-reviewed after the "Record lesson completion (FR-008)" item: new page `Progress/RecordCompletions.razor`, invalidating the recorded hash. Reviewed the markup directly. One `<h1>` and a `<PageTitle>`. Every input is native with an `id` literally matched by a `label for` (`lessonId`, `assignedToMe`, `student{id}`, `completionNote`) (1.3.1, 3.3.2, 4.1.2). The lesson `<select>` groups lessons by rank in `<optgroup label>`s. Student checkboxes sit in a `<fieldset>` with a `<legend>`. A student blocked by lapsed consent, or already completed, has a disabled checkbox plus visible text giving the reason, so state is never colour alone (1.4.1). The per-student result is a `<ul role="status">` in words (recorded, already completed, blocked, failed) with the reason (3.3.1, 4.1.3); service errors use `role="alert"`. The submit button is native and its text states how many students it records. All controls are native, so keyboard-operable (2.1.1). No CSS change. Not done: no browser or screen-reader pass; focus is not moved to the result after submitting. Colour contrast remains open, as before. |
| Claude (agent), awaiting Philip's countersign | 22/10/2026 | Re-reviewed after the "Rank badge images (FR-014)" item, which invalidated the recorded hash: new page `Administration/RankBadges.razor`, a one-link edit to `Home.razor`, and one `<img>` added to `Parent/ParentDashboard.razor`. Reviewed the markup directly against `Administration/Students.razor`. One `<h1>` and a `<PageTitle>`; real table with a `<th>` for every column (1.3.1). Every image has alt text naming the rank, "<Rank name> badge" (1.1.1). The file input has an `id` held in one variable and a `label for` that matches it literally (3.3.2). The Upload button's `aria-label` starts with its visible text (2.5.3) and it is disabled until a file is chosen (4.1.2). Status and error text use `role="status"` and `role="alert"` (3.3.1, 4.1.3). Rank names are text, not colour. No CSS change. Open, as before: colour contrast. Keyboard and screen-reader behaviour of the file input were not tested by hand. |
| Claude (Sonnet subagent) — awaiting Philip's countersign | 22/10/2026 | Re-reviewed after the "Certificate view and download" item (FR-010): `Parent/ParentDashboard.razor` now shows a View and a Download link per certificate, which invalidated the recorded hash. Both are native `<a>` elements, so keyboard-operable (2.1.1). Each carries an `aria-label` that begins with its visible text ("View certificate for ..." contains "View"; "Download certificate for ..." contains "Download"), so Label in Name holds (2.5.3), and names the lesson so links are unique and understandable out of context (2.4.4). View opens in a new tab and says so in its label (3.2.5 is not engaged: it is a user-initiated link, and the warning is given). `data-enhance-nav="false"` so the browser, not Blazor enhanced navigation, fetches the PDF. The PDF itself is the existing generated document and has not been checked for tagged-PDF accessibility (open, with the certificate designs to come). No new inputs, no CSS change; the open colour-contrast item is unaffected. |
| Claude (Sonnet subagent) — awaiting Philip's countersign | 22/10/2026 | Re-reviewed after the "Lesson uploads and Approver review (FR-007)" item: `Curriculum/TeacherProposals.razor` and `Curriculum/ApproverProposals.razor` changed, invalidating the recorded hash. Reviewed the markup directly. **Teacher form:** the typed name/key inputs are gone. Files are an ordered list; each row names the file and says "new upload" or "current file" in words; each button has a unique accessible name ("Move file 2 up", "Move file 2 down", "Remove file 2", 2.4.6) and the first and last move buttons are disabled rather than hidden. The upload control is a native `InputFile` with a literal id (`fileUpload`) matched by `label for`, described by a help paragraph (`aria-describedby`) listing the accepted types and size limit (3.3.2, 1.3.1). Upload outcomes use `role="status"` and errors `role="alert"` (4.1.3, 3.3.1). "My proposals" gains a Files column of links whose text is the file name plus "(opens in a new tab)" (2.4.4). **Approver page:** the raw `<pre>` JSON is replaced by one `<section aria-labelledby>` per proposal with an `<h2>`, and a table with a `<caption>`, column and row `<th scope>` headers, and a Difference column saying "Changed" or "Same"; added and removed files are labelled "[Added]" and "[Removed]" in text, with the removed ones also struck through, and changed rows have a background, so colour is never the only cue (1.4.1). The rejection-comments input is a native input with a literal `id` per proposal and a matching `label for` ("Rejection comments for proposal 7"); approve and reject buttons name the proposal. Everything is native and keyboard-operable (2.1.1). Not done: no browser or screen-reader test of the upload control; the highlight colour (#fff3bf on black text) was not measured against the contrast tool; focus is not moved after approving or rejecting. |
| Claude (agent), awaiting Philip's countersign | 22/10/2026 | Re-reviewed after the "Student accounts and student page" item (FR-011): new pages `Parent/StudentSignIns.razor`, `Administration/StudentSignIns.razor` and `MyProgress/StudentHome.razor`, and edits to `Account/Pages/Login.razor`, `Account/Pages/ForgotPassword.razor`, `Administration/UserRoles.razor` and `Parent/ParentDashboard.razor`, invalidating the recorded hash. Reviewed the markup directly. Each new page has one `<h1>` and a `<PageTitle>`; the parent page has an `<h2>` per child, the student page `<h2>`s for Rank, Completed lessons, Badges and Certificates (1.3.1, 2.4.6). Every input is native with an `id` literally matched by a `label for` (`username{id}`, `password{id}`, `confirm{id}`), and each label names the child, so repeated fields are distinguishable (1.3.1, 3.3.2, 4.1.2); passwords use `autocomplete="new-password"` (1.3.5). Repeated buttons carry an `aria-label` that begins with the visible text and names the child ("Create sign-in for ...", "Reset password for ...") (2.5.3, 2.4.6). Errors use `role="alert"` and success text `role="status"` (3.3.1, 4.1.3); the username rule is stated in text before the form (3.3.2). The Admin page is a real table with `<caption>` and `<th scope>` (1.3.1). The student page's progress is given in words as well as a `<progress>` with an `aria-label` (1.1.1, 1.4.1); badge `<img>`s have alt text "<Rank> badge"; certificate links reuse the parent dashboard's View/Download pattern (labels begin with visible text, new tab announced). Login's field is now labelled "Email or student username" (`for`/`id` = `username`) with `autocomplete="username"`. No CSS change. Not done: no browser, keyboard or screen-reader pass; focus is not moved to the status message after an action. Colour contrast remains open, as before. |
| Claude (Opus, main session) — awaiting Philip's countersign | 22/10/2026 | Merge of student sign-ins (FR-011) with navigation (FR-012): `NavMenu.razor` gains two links, "Student sign-ins" to `/parent/student-sign-ins` under `RequireParent` and to `/administration/student-sign-ins` under `RequireAdmin`. Each matches its page's own policy, which `NavigationCoverageTests` checks. The `/student` exemption in that test is removed now that the page exists. Same list markup and `aria-current` pattern as the neighbouring links; no other change. |
| Claude (agent), awaiting Philip's countersign | 22/10/2026 | Re-reviewed after the "Student sign-in rules" item (FR-011, adr/0007 decisions 9–13): `Account/Pages/Login.razor`, `Parent/StudentSignIns.razor` and `Administration/StudentSignIns.razor` changed, invalidating the recorded hash. Reviewed the markup directly. **Login:** one plain paragraph after the Remember me checkbox says students stay signed in on the device and to log out on a shared one (3.3.2); the new turned-off-sign-in message uses the existing `role="alert"` error paragraph (4.1.3) and says what to do (3.3.3). **Parent page:** a paragraph states the student password rule (3.3.2), and a child whose sign-in is off gets a plain-text sentence under their `<h2>`, not a colour or icon (1.4.1). No new inputs. **Admin page:** the table gains `<th scope="col">` headers "Status" and "Sign-in" (1.3.1); status is the words "Active" or "Turned off" (1.4.1); each row has one native `<button>` whose visible text ("Turn off" / "Turn on") starts its accessible name ("Turn off sign-in for Alex"), so names are unique per row and contain the visible label (2.4.6, 2.5.3); outcomes use the existing `role="status"` and `role="alert"` paragraphs (4.1.3). No new inputs, so the `label for` check is unaffected. |
| Claude (agent), awaiting Philip's countersign | 22/10/2026 | Re-reviewed after the "Change password while signed in" item (FR-011): new page `Account/Pages/Manage/ChangePassword.razor` and a `Layout/MainLayout.razor` header link, which invalidated the recorded hash. Reviewed the markup directly. Every one of the three inputs has a visible `<label for>` whose value literally matches the input `id` (`oldPassword`, `newPassword`, `confirmPassword`) and a correct `autocomplete` token (`current-password`, `new-password`) (1.3.5, 3.3.2); the page has a `<h1>` and `<PageTitle>` (2.4.2, 2.4.6); validation errors render in `<ValidationSummary role="alert">` and Identity errors in `<p class="error" role="alert">` (3.3.1, 4.1.3), with the success message in `<p role="status">` (4.1.3); the error text names the rule that failed (Identity's own descriptions, 3.3.3); the page has no pasting restriction (3.3.8 accessible authentication: password managers work); the student message is in `role="alert"` text. The header link is a native `<a>` in the existing header list. No images, no new colour or CSS. |
| Claude (agent), awaiting Philip's countersign | 22/10/2026 | Re-reviewed after the "Consent review shows differences" item (FR-015): `Safeguarding/ConsentRecords.razor` review section changed, invalidating the recorded hash. Reviewed the markup directly. A changed name or date of birth is marked by the bold text "Changed:" followed by "was X on record, now Y on form", plus the `review-changed` background; colour is never the only cue (1.4.1) and a screen reader reads the text (1.3.1). A summary paragraph with `role="alert"` ("Check before approving ... Changed: name, date of birth") announces the difference when the review opens (4.1.3). Unchanged values read "(same as record)". The in-force comparison is a real `<table>` with `<th scope="row">` row headers and column headers, and a "Result" column that says "Changed" or "Same" in words (1.3.1, 1.4.1); it sits under an `<h3>` inside the existing labelled section (2.4.6). The `review-changed` colours (#fff3bf with #000 text) are the ones already reviewed for the curriculum approval page (1.4.3). No new inputs, so the label/id rule is unaffected; the buttons are unchanged. |
| Claude (agent), awaiting Philip's countersign | 22/10/2026 | Re-reviewed after the "Distinct button and field names" item (WCAG 2.4.6, 2.5.3), and the plain-English page text change, which invalidated the recorded hash. Reviewed the markup directly. Repeated per-row buttons now carry an `aria-label` that starts with the visible text and names the child, student, teacher or submission: `Parent/ParentConsent.razor` (Complete the consent form / Update and resubmit), `Safeguarding/ConsentRecords.razor` (Review), `Safeguarding/DbsChecks.razor` (Mark {status}), `Retention/MarkAsLeft.razor` (both tables), `Progress/CompletionReversals.razor` (Reverse), `Curriculum/TeacherProposals.razor` (Propose edit, Propose retirement, Resubmit). **Parent sign-ins** (`Parent/StudentSignIns.razor`): a child's sign-in error now renders beside that child's form with `role="alert"`, and each of its username and password inputs carries `aria-describedby` pointing at it; the top-of-page error remains for a failed load only. Every input keeps a `label for` whose value matches its `id`. Introductory text on the listed pages no longer shows internal names. No browser or screen-reader pass. |
| Claude (Opus, main session) — awaiting Philip's countersign | 22/10/2026 | Merge of "Consent review shows differences" and "Plain-English page text / distinct button names" into `master`. The only source file both changed is `Safeguarding/ConsentRecords.razor`: git merged it without conflict (the first changed the review section, the second only the intro paragraph and the Review button's `aria-label`). Checked the merged file keeps both changes; no authorisation, policy or data-access change. Gate re-run on `master` after merge. |
| Claude (agent), awaiting Philip's countersign | 22/10/2026 | Re-reviewed after "Guardian invite for an existing account" (FR-004): `Guardianship/AcceptInvite.razor` and `Administration/UserRoles.razor` changed, invalidating the recorded hash. Reviewed the markup directly. **Accept invite:** `<PageTitle>` and `<h1>` match the path ("Set up your account" for a new account, "Accept your invite" otherwise) (2.4.2, 2.4.6). The sign-in prompt is a plain paragraph plus a native link whose text says what it does, "Sign in to accept" (2.4.4). The wrong-account refusal and all errors use the existing `<p class="error" role="alert">`. The accepted message is a `<p role="status">` (4.1.3; `StatusMessageAccessibilityTests` updated, since this path now stays on the page) and contains a native link, "your dashboard". The accept form is a single native `<button type="submit">Accept invite</button>` with no inputs, so it needs no labels. The new-account form is unchanged (labels still match ids). **User roles:** the waiting-for-approval table gains a "Current roles" `<th>` column with matching cells (1.3.1); the intro paragraph explains the wider list (3.3.2). No images, no new colour or CSS. |
| Claude (Opus, main session) — awaiting Philip's countersign | 22/10/2026 | Merge of "Invite for an existing account" into `master` after the plain-English text item. Conflict only in `Administration/UserRoles.razor`'s introduction paragraph: kept the invite item's meaning (the waiting list includes guardians who already hold other roles; granting Parent adds to existing roles) in the plain-English wording, without the requirement reference. No authorisation, policy or data-access change from the merge itself. Gate re-run on `master` after merge. |
| Claude (agent), awaiting Philip's countersign | 22/10/2026 | Re-reviewed after "Consent submission alerts and parent emails": new component `Pages/PendingConsentNotice.razor` (a `p role="status"` holding one link whose text states the count and what it is, for example "2 consent forms are waiting for review"; no input, no colour-only meaning, no new id collisions) shown on the home page to Admins and Safeguarding Officers. No other markup changed. |
| Claude (agent), awaiting Philip's countersign | 22/10/2026 | Re-reviewed after the "Separate consent fields" item (FR-001, adr/0008 decision 5), which changed `Parent/ParentConsent.razor`, `Safeguarding/ConsentRecords.razor` and `Safeguarding/StudentConsent.razor`. Reviewed the markup directly. **Inputs (1.3.1, 3.3.2, 4.1.2):** the two new text areas (`dietaryRequirements`, `specialEducationalNeeds`) and the two new checkboxes (`transportConsent`, `offsiteTripConsent`) each have a visible `label for` that matches the input `id` (the checkbox labels also wrap the input, as the existing ones did); the parent form's labels say what the field is for ("Dietary requirements, including allergies (optional)", "Special educational needs (optional)"). **Length (3.3.1):** the text areas carry `maxlength=4000` and the form models carry `StringLength`, so an over-long entry shows a validation message in the existing `ValidationSummary` rather than failing silently. **Multi-line (1.4.4, 1.4.10):** `rows=4` text areas resize with the text and reflow; no fixed pixel sizes. **Review and view pages (1.3.1):** the new values are extra `dt`/`dd` pairs and list items in the existing description lists, so structure is unchanged. No heading, landmark, colour or focus handling changed. |
| Claude (Opus, main session) — awaiting Philip's countersign | 22/10/2026 | Merge of "Separate consent fields" into `master` after "Consent submission alerts and parent emails". Both changed `ParentConsentService.cs` and `ConsentRecordService.cs`; git merged them without conflict (fields versus a notification call after the save). The new emails read no `ConsentSensitiveDetails` field, so the split adds nothing to them. Conflicts only in the checklists, test plan and journeys; both sides kept. No authorisation, policy or data-access change from the merge. Gate re-run on `master` after merge. |
| Claude (agent), awaiting Philip's countersign | 22/10/2026 | Re-reviewed after "Resend a guardian invite" (FR-018): `Guardianship/GuardianLinks.razor` gained an "Account" column. Each link row whose guardian has no account shows, to Admins only (`AuthorizeView Policy="RequireAdmin"`), a native `<button type="button">` with visible text "Resend invite" and `aria-label` "Resend invite to {guardian name}", so the accessible name starts with the visible text (2.5.3) and is unique per guardian even when a guardian appears on several rows (2.4.6, 4.1.2); other rows show the plain text "Has an account" (Admin without an account to chase) or "No account yet" (Teachers). The column has a `<th>` header (1.3.1). The result appears in the existing `role="status"` paragraph on success and `role="alert"` on failure (4.1.3), above the table; the page reloads its data in place and focus stays on the page, with no change of context (3.2.2). No new form field, so no new label or id. Native button: keyboard operable (2.1.1), default focus indicator unchanged (2.4.7). Result: no failure found. Not yet checked in a browser. |
| Claude (Opus, main session) — awaiting Philip's countersign | 22/10/2026 | Merge of "Resend guardian invite" into `master` after "Separate consent fields" and "Consent submission alerts and parent emails". No source file overlaps; conflicts only in the checklists and test plan (TC renumbered 034 to 036), both sides kept. Resend is Admin only in the service and the button (`AuthorizeView Policy="RequireAdmin"`), per FR-018 as amended 22/10/2026. Gate re-run on `master` after merge. |
| Claude (agent), awaiting Philip's countersign | 23/10/2026 | Re-reviewed after "Parent registration with two-stage approval": new static-SSR pages `Registration/Apply.razor`, `MyApplications.razor`, `Review.razor`, `Approve.razor` and component `ApplicationSummary.razor`; links added to `NavMenu.razor` and `Home.razor`; `.preserve-lines` and `.visually-hidden` added to `app.css`; text changes in `RegisterStudent.razor` and `GuardianLinks.razor`. Reviewed the markup directly. 1.3.1/4.1.2: every input has a `<label for>` matching its id, including the per-child inputs on the apply page (`childName@(n)` and so on, native inputs as required in loops) and the per-student radio buttons on the approval page (`existing@(studentId)`); each child's block is a `<fieldset>` with a numbered `<legend>`; the existing-student radio group is a `<fieldset>`/`<legend>`; summary data is a `<dl>`; tables have `scope="col"` headers, and the action column's header text is visually hidden rather than empty. 2.4.6/2.5.3: per-row links name the child (`aria-label` starting with the visible text: "Review the application for ...", "Open the application for ...", "Answer and resubmit the application for ..."); "Remove the last child" names the child number. 3.3.1/3.3.2/4.1.3: required fields have `aria-required`, labels say which text is optional, errors render in `role="alert"`, confirmations in `role="status"`. 1.4.10/1.4.12: no fixed widths added; `.preserve-lines` only keeps typed line breaks. 3.2.2: "Add another child" is a submit button that redisplays the form with entered values kept; no change of context on input. Not yet checked with a screen reader or in a browser (UAT). |

Reviewed-hash: 5f3208634f45679a4a2525b07375ca40b1004da0bc071eae8cba7a84c400dad4
