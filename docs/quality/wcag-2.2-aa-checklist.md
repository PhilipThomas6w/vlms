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
`Reporting/ProgressReporting.razor`, plus the shared `Layout/MainLayout.razor`/
`Layout/ReconnectModal.razor` — and, new this review (STATE.md "Identity" item —
`src/Vlms.Web/Components/Account/Pages/`): `Login.razor`, `Register.razor`, `LoginWith2fa.razor`,
`LoginWithRecoveryCode.razor`, `Manage/EnableAuthenticator.razor`,
`Manage/TwoFactorAuthentication.razor`.

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
      uses `<p role="status">` — this is a genuine improvement on the "Status messages (4.1.3)" gap
      already flagged below for the older seven pages (still open there), not something to
      re-flag here.

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
- [ ] **Non-text content beyond `<img>` (1.1.1):** `Layout/MainLayout.razor`'s dismiss control uses
      a bare emoji glyph (`🗙`, inside `<span class="dismiss">`) with no `aria-label` — this is
      Blazor-template-default markup, not built by this project, but ships in the app; confirm
      whether a screen reader announces something usable for it, and whether it needs an
      `aria-label="Dismiss"` added. `build/check-accessibility.ps1`'s `<img>` check does not cover
      this (it isn't an `<img>` element), which is exactly why it's listed here rather than assumed
      covered.
- [ ] **Consistent navigation (3.2.3) / reflow (1.4.10):** `Home.razor`'s per-role
      `<AuthorizeView>` link list is still the only *content* navigation surface, but
      `MainLayout.razor` now has a genuine persistent `<nav>` too (STATE.md "Identity" item — sign-in
      status, a link to `Account/Manage/TwoFactorAuthentication`, and a log-out `<form>`, shown on
      every page via `AuthorizeView`/`NotAuthorized`) — semantic `<nav>`, real `<a>`/`<button>`
      elements, no new pattern to review beyond confirming it (like `Home.razor`'s list) still reads
      and reflows sensibly at 400% zoom / 320px viewport width as more links are added.
- [ ] **Status messages (4.1.3):** confirm success paths (e.g. `RegisterStudent.razor` clearing the
      form and reloading the guardian list after a successful submit) surface a perceivable
      confirmation to assistive tech, not just a silently-reset form — currently no explicit success
      message exists on any of the seven form pages; decide whether that's an accessibility gap
      worth a follow-up `STATE.md` item, or acceptable given the page's own reload-and-clear
      behaviour is itself a visible change.

## Sign-off

| Reviewed by | Date | Notes |
|---|---|---|
| Philip Luke Thomas | 2026-07-19 | Initial checklist authored alongside the verify.ps1 gate stage. Colour contrast and the MainLayout dismiss-glyph aria-label are the two items most likely to need real follow-up once branding lands; not blocking for this increment since no VLMS-specific visual design exists yet to review against. |
| Philip Luke Thomas | 2026-07-20 | Re-reviewed after STATE.md's "Identity" item added six new Account pages (Login/Register/LoginWith2fa/LoginWithRecoveryCode/Manage/EnableAuthenticator/Manage/TwoFactorAuthentication), one new content page (`Guardianship/AcceptInvite.razor`), and a persistent nav bar in `MainLayout.razor`. Read every new/changed file, not just the diff. All six Account pages follow this codebase's existing label/role="alert" conventions (new bullet above); the QR-code alt-text question that a TOTP enrolment page would normally raise doesn't arise here — no QR code is generated, a documented scope decision. `MainLayout.razor`'s new `<nav>` is semantic markup, reviewed under the existing "Consistent navigation" item (updated above) rather than as a new concern. One genuine improvement found and noted, not a gap: `Manage/TwoFactorAuthentication.razor` uses `role="status"` for its recovery-code-regeneration message, which the existing "Status messages (4.1.3)" item had flagged as missing everywhere else — that item stays open for the older seven pages, not resolved codebase-wide. No new gaps found beyond what was already open (colour contrast, the MainLayout dismiss-glyph aria-label, and the pre-existing status-messages item). |
| Philip Luke Thomas | 2026-07-21 | **Cumulative audit pass** (ADR-0005 hosting-pivot verify-gate audit, STATE.md), not triggered by a hash mismatch — `build/verify.ps1` reported this checklist current before this review. Confirmed, rather than assumed, that none of the three ADR-0005 hosting-pivot items landed after the row above (file storage/`LocalDiskBlobStorage`, email/`SmtpEmailSender`, scheduled-jobs/systemd, deployment-scripting) touched any `.razor` file or `wwwroot/app.css` — checked each Done entry's own file list, not inferred from "they sound backend-only". A repo-wide listing of `src/Vlms.Web/Components/**/*.razor` confirms the page inventory named in "Manual review items" above is still exhaustive and unchanged (no page added or removed). No new gaps found beyond what was already open (colour contrast, the MainLayout dismiss-glyph aria-label, and the status-messages item). Hash unchanged (no hashed file changed). |

Reviewed-hash: 515a3d8fc5ec63f288d36f1c4996484f28efd68dec6eb7a8adba1adc9d4bdc77
