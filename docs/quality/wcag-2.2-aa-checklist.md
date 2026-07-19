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
`Guardianship/GuardianLinks.razor`, `Registration/RegisterStudent.razor`,
`Safeguarding/ConsentRecords.razor`, `Safeguarding/DbsChecks.razor`,
`Parent/ParentDashboard.razor`, `Reporting/ProgressReporting.razor`, plus the shared
`Layout/MainLayout.razor`/`Layout/ReconnectModal.razor`.

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
      `<AuthorizeView>` link list is the only navigation surface (no persistent nav menu exists yet)
      — confirm it still reads and reflows sensibly at 400% zoom / 320px viewport width as more
      links are added.
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

Reviewed-hash: 42baa5d3ab50b232499b07100a5ae79aade94a90744fed328d9a13d3b3471d09
