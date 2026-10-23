# 0009 - Browser-based WCAG checks in the verify gate

## Status

Accepted (Philip, 23/10/2026). Reverses the rationale recorded in the header of `build/check-accessibility.ps1` (written for the original build), which kept browser tooling out of the gate. The script's checks are unchanged and still run.

## Context

`build/check-accessibility.ps1` automates only what can be read from Razor markup (`<img alt>`, `<label for>` pairing, `<html lang>`) and gates everything else behind a human-signed checklist (`quality/wcag-2.2-aa-checklist.md`). Its header judged a Playwright-based harness "a disproportionate amount of new moving parts" for a solo-maintained project with no CI.

That judgement no longer holds:

- The UI/UX redesign (`docs/ui-ux-redesign-toolchain.md`) touches about 40 pages. Every one changes colours, spacing and layout.
- Colour contrast (WCAG 1.4.3 text, 1.4.11 non-text) and reflow (1.4.10, 320 CSS px) can only be tested on a rendered page with real computed styles. `test-plan.md` has carried "colour contrast: still open" since the delivery gate.
- A human checklist re-reviewed across 40 redesigned pages will not keep up, and the checklist-currency gate only proves someone re-read the files, not that the result is accessible.
- The moving parts are smaller than assumed: `Microsoft.AspNetCore.Mvc.Testing` 10.0 can serve a real Kestrel port (`WebApplicationFactory<TEntryPoint>.UseKestrel(int)`, [Microsoft Learn](https://learn.microsoft.com/dotnet/api/microsoft.aspnetcore.mvc.testing.webapplicationfactory-1.usekestrel?view=aspnetcore-10.0)), so no separate hosted instance is needed. The existing test host already replaces PostgreSQL with SQLite and the email sender.

## Decision

1. `tests/Vlms.UiTests` (xUnit, `Microsoft.Playwright`, `Deque.AxeCore.Playwright`) runs in the **full** gate as the `ui-tests (browser WCAG)` stage, skipped by `-Fast`. The `test` stage runs only `tests/Vlms.Tests`.
2. The app is hosted by a trimmed copy of the end-to-end factory (`UiWebFactory`) on a dynamic Kestrel port; one Chromium and one app are shared by all tests through an xUnit collection fixture.
3. axe-core runs with `RunOnly` tags `wcag2a`, `wcag2aa`, `wcag21aa`, `wcag22aa`. No rule is disabled globally.
4. A reflow check at 320 CSS px asserts `document.documentElement.scrollWidth <= window.innerWidth`.
5. **Known violations are recorded, not hidden.** `tests/Vlms.UiTests/Baseline/known-violations.json` lists, per page, the axe rule ids and the pages that overflow at 320 px that existed when the tests were added. A violation not in the baseline fails the test. A baseline entry that no longer reproduces also fails, so the list can only shrink as the redesign lands.
6. A machine without the Playwright browser fails the stage with the install command; it never skips:
   `pwsh tests/Vlms.UiTests/bin/Debug/net10.0/playwright.ps1 install chromium`.
7. Scope grows with the redesign: the first tests cover the anonymous pages only. Signed-in pages need seeded accounts (and TOTP for staff roles) and are added as each redesigned page lands.

## What stays manual

Automated rules cover roughly the mechanically decidable part of WCAG. These remain on the human checklist (`quality/wcag-2.2-aa-checklist.md`, with its currency gate):

- Screen-reader announcement quality and reading order (2.4.6, 1.3.2, 4.1.3 in practice).
- Whether focus order and focus visibility make sense for the task (2.4.3, 2.4.7), beyond "an element is focusable".
- Keyboard-only operation of whole journeys, including focus return after dialogs.
- Error-message clarity and the plain-English reading of page text (3.3.x, 3.1.x).
- 400% zoom and text-spacing overrides judged by eye (1.4.4, 1.4.12), and anything axe reports as "incomplete" (needs review).
- Pages and states the browser tests do not yet reach (signed-in pages, token-driven pages such as confirm email, accept invite and two-factor).

## Baseline at 23/10/2026 (anonymous pages, Chromium, 1280 px for axe)

Pages tested: `/`, `/Account/Login`, `/Account/Register`, `/Account/ForgotPassword`, `/Account/ResendEmailConfirmation`, `/Account/ResetPassword?code=...`, `/not-found`.

| Page | Rule | Impact | Detail |
|---|---|---|---|
| `/not-found` | `document-title` (WCAG 2.4.2) | serious | The page has no `<title>`. |
| `/Account/ForgotPassword` | `target-size` (WCAG 2.5.8) | serious | The submit button is under 24 px and lacks spacing from its neighbour. |

No colour-contrast violation was found on these pages, and none of the seven pages overflows horizontally at 320 px, so `reflow320` is empty. These two violations are left for the redesign; no application markup or CSS was changed by this decision.

## Consequences

- The full gate needs Chromium installed once per machine, and takes roughly 15 seconds longer.
- The tests pull `Vlms.Web`, `Vlms.Domain` and `Vlms.Infrastructure` into a second test project, and a trimmed copy of the factory must be kept in line with `VlmsWebFactory` if the host's service wiring changes.
- `Verify.Xunit` and `Verify.Playwright` are referenced for later screenshot snapshots; they are unused so far.
- `.gitleaks.toml` allowlists Playwright's bundled driver in `bin/`.
