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
`Safeguarding/DbsChecks.razor`, `Parent/ParentDashboard.razor`,
`Reporting/ProgressReporting.razor`, `Reporting/RecordsDueForDeletion.razor`,
`Progress/CompletionReversals.razor`, plus the shared
`Layout/MainLayout.razor`/`Layout/ReconnectModal.razor` — and (STATE.md "Identity" item —
`src/Vlms.Web/Components/Account/Pages/`): `Login.razor`, `Register.razor`, `LoginWith2fa.razor`,
`LoginWithRecoveryCode.razor`, `Manage/EnableAuthenticator.razor`,
`Manage/TwoFactorAuthentication.razor`.

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

- [ ] **Colour contrast (1.4.3/1.4.11):** `wwwroot/app.css` (the Blazor project template's default
      stylesheet, not yet restyled for VLMS) gives text and UI-component colours a 4.5:1 (text) /
      3:1 (UI component) contrast ratio against their background. No VLMS-specific branding/colour
      palette exists yet (same placeholder status as `wwwroot/icons/icon.svg` — see `STATE.md`'s
      PWA entry) — re-check this item specifically once real branding lands, not just at this
      review.
- [ ] **Keyboard operability (2.1.1) / focus order (2.4.3):** every `EditForm` (all seven form pages
      above use `EditForm`/`DataAnnotationsValidator`/`ValidationSummary`) can be fully completed
      and submitted using only the keyboard, in a sensible tab order — includes
      `RegisterStudent.razor`/`GuardianLinks.razor`'s `InputRadioGroup`-driven conditional sections
      (confirm the hidden/shown fields don't trap or skip focus).
    - [ ] `Layout/ReconnectModal.razor`'s `<dialog>` — confirm focus moves into it when the
          SignalR circuit drops and returns to the triggering context on reconnect/dismiss (this is
          Blazor template-default markup, not VLMS-authored, but still in scope since it's shipped).
- [ ] **Error identification (3.3.1) / suggestion (3.3.3):** every page's `<ValidationSummary />`
      and inline `role="alert"` error `<p>` (see `RegisterStudent.razor`'s `_errorMessage` pattern,
      reused across the Safeguarding/Guardianship/Registration pages) describes the problem in text,
      not colour/icon alone, and is announced to assistive tech (confirm `role="alert"` is present
      on every such element — `RegisterStudent.razor` has it; re-check the others each review as
      more pages are added).
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

Reviewed-hash: ba3933d576c9f86aa2dab05b6d5351af92caf40c05fd3bb08f2e900e21056a8d
