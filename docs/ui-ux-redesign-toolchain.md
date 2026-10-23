# UI/UX redesign: Claude Code toolchain

**Status:** in progress. Decisions 3 and 4 approved and installed 23/10/2026 (`.mcp.json`). Decision 5 approved 23/10/2026: test project scaffolded, tests and gate split still to do. Decision 6: Verify packages installed under the OpenSource exemption (Philip, 23/10/2026; renew by 2027-10). Decisions 7 and 8 applied 23/10/2026. The rest await Philip's decision.
**Date:** 23/10/2026
**Related:** `docs/ui-ux-redesign-questionnaire.md` (answers 41–50)

## Summary

The design-guidance side is already well covered. What's missing is rendering and checking: seeing pages at phone, tablet and desktop sizes in a repeatable way, and testing WCAG 2.2 against the rendered page. Three additions close that, none adds npm to the repository, and a fourth is optional later:

1. **Playwright MCP** (essential), so Claude can render and inspect the running app itself.
2. **Browser tests in C#**: `Microsoft.Playwright` plus `Deque.AxeCore.Playwright` as NuGet test packages (essential), run by the gate.
3. **Visual regression** with `Verify.Playwright` (useful), added once the design system settles.
4. **SVGO** (optional), run occasionally from the command line to shrink SVG files.

Disable Figma. Keep everything else that's installed.

Two points push back on earlier decisions:

- **The accessibility gate stage.** `build/check-accessibility.ps1` says in its header that Playwright was rejected as "a disproportionate amount of new moving parts". A redesign of all ~40 pages changes that. Colour contrast is the open WCAG item, a browser-based checker tests it automatically, and a checklist signed by a person won't keep up with 40 restyled pages. This reverses a recorded decision, so it needs a new ADR (0009).
- **The `design` plugin's accessibility audit uses WCAG 2.1 AA, not 2.2.** It has none of the 2.2 criteria. Two of them matter here: 2.5.8 Target Size (Minimum), and 3.3.8 Accessible Authentication, which applies to the TOTP sign-in. Don't treat that skill as the WCAG authority.

## 1. What's already installed

Checked on 23/10/2026: `.claude/settings.json`, `~/.claude/settings.json`, `installed_plugins.json` and `claude mcp list`. The Chrome extension is connected ("Browser 1", Windows). Node v25 and .NET SDK 10.0.402 are on the machine.

| Capability | What it gives | Value for this redesign | Limitations | Duplicated by | Verdict |
|---|---|---|---|---|---|
| **frontend-design** (skill) | Guidance on visual direction, typefaces, type scale and line length; pushes away from template looks | High. The current problem is a bland template look. Its "start from the subject matter" approach suits the Byzantine / Anglo-Saxon direction. | Leans towards marketing pages ("the hero is the first thing viewers see"). Gives guidance only and can't check anything. | Nothing | **Keep.** Use it for the direction and type system. |
| **VectorLab UX skills** (24 skills) | Specific rules for spacing (4/8 pt grid), type scale, colour (60-30-10), viewports (375–1280 px), keyboard and focus, forms, labels, error messages, empty states, motion, reduced motion, page patterns, and a `ux-audit` that runs them all | High. Its README says it doesn't depend on any framework (plain CSS and DOM), so it suits Razor. `ux-audit` makes a good critique pass for each page. | Its taste is SaaS-oriented: `anti-slop` and `colour-palette` push towards restraint, and deliberate Byzantine ornament may conflict with that. Its icon guide assumes npm packages; here the SVGs would be copied into `wwwroot`. Some rules (ban "Submit", blur-first validation) may conflict with existing test text. | Partly the `design` plugin | **Keep.** Treat `anti-slop` as advice: ornament is intended here. |
| **design** plugin (synced) | `design-critique`, `design-system`, `ux-copy`, `accessibility-review`, user research | Medium. Critique and design-system documentation are useful. `ux-copy` overlaps VectorLab's verbs, errors and labels skills. | **`accessibility-review` targets WCAG 2.1 AA.** It also adds 5 unauthenticated connectors (Slack, Linear and others) that do nothing here. | VectorLab (copy), axe (accessibility) | **Keep** critique and design-system. **Don't use** `accessibility-review` as the WCAG check. |
| **figma** (plugin + MCP) | Two-way Figma: build design-system libraries, design to code, Code Connect | Low. Solo developer, and a Figma file would be a second source of truth beside the Razor/CSS. Code Connect has no Blazor mapping. | Not authenticated yet. **The free Starter plan allows 6 MCP tool calls a month.** A paid Pro/Org plan with a Dev or Full seat gets 200 a day ([Figma rate limits](https://developers.figma.com/docs/figma-mcp-server/rate-limits-access/)). It also loads about 17 skill descriptions into every session. | Designing in code with real rendering (below) | **Disable** in project settings unless a paid Figma seat already exists. |
| **loop-harness** | `/work-next`, the `STATE.md` queue, the verify gate, the codebase wiki | Essential. The gate decides when work is done. | None for this work | Nothing | **Keep.** The new browser tests become part of the gate. |
| **Claude in Chrome** | Drives Philip's real Chrome: screenshots, `read_page`, `resize_window`, GIF recording, console | Medium. Good for live sessions Philip watches, and GIFs for review. | Works in Philip's own Chrome profile and sign-ins. `resize_window` resizes the real window, which doesn't emulate a phone (no pixel density, touch or mobile behaviour). Past walks in `STATE.md` record it as not connected. Can't run in the gate. | Playwright MCP | **Keep, as a secondary tool** for watched sessions. |
| **Microsoft Learn MCP** | Official Blazor and ASP.NET documentation | High for checking Blazor rendering, CSS isolation and static assets | None | Nothing | **Keep** |
| **Artifact tool** (built in) | Private hosted HTML pages | Useful for mood boards, palette and type samples, and redesign proposals for approval | It's a separate page, not the Blazor app, so it's not proof the design works in the app | The in-app style guide | **Keep** for proposals only |
| `run` skill (built in) | Starts the app | Starting `Vlms.Web` before browser work | None | Nothing | Keep |
| GitKraken, Lucid, Mermaid, M365, Gmail and others | Not relevant | None | None | None | Ignore |

**Do the installed design skills make more design plugins unnecessary?** Yes. Between them, frontend-design, VectorLab and design cover visual composition, typography, colour, spacing, responsive rules, interaction, page patterns, copy and critique. There is no design gap worth another plugin.

## 2. What's genuinely missing

1. **Repeatable rendering at set viewports.** No installed tool can render the app at 375 / 768 / 1280 px with real phone emulation, in a clean session Claude controls without using Philip's browser profile.
2. **WCAG checks against the rendered page.** Today's gate checks only `<img alt>`, label/for pairs and `<html lang>` in the source. Nothing checks contrast (1.4.3 / 1.4.11), reflow at 320 px (1.4.10), text spacing (1.4.12), focus visibility, 2.4.11 Focus Not Obscured, 2.5.8 target size or accessible names after rendering.
3. **Visual regression.** A change to a design token reaches every page, and nothing would catch the pages nobody looked at.
4. **Visual design-system documentation.** No catalogue of components and their states.
5. **SVG optimisation.** Claude can write and edit SVG as text directly. The only gap is minification.

Not gaps: design tokens (CSS custom properties are enough), component tooling, and anything framework-specific.

## 3. Candidate tools

| Candidate | Type | Source | Maturity | Problem it solves | Blazor/.NET fit | New dependencies | Security / privacy | Overlap | Verdict |
|---|---|---|---|---|---|---|---|---|---|
| **Playwright MCP** | MCP server | [microsoft/playwright-mcp](https://github.com/microsoft/playwright-mcp); also listed in `claude-plugins-official` | Microsoft; npm 0.0.83 (28/09/2026) | Gap 1. Isolated in-memory profile (`--isolated`), `--viewport-size`, `--device` emulation, `browser_take_screenshot`, and structured page snapshots ([docs](https://playwright.dev/mcp/snapshots)) | Works with any framework; just drives `localhost:5074`. Blazor Server's live connection works normally. | Node ≥18 on the dev machine, run via `npx`. Nothing in the repo. Can use the installed Chrome. | The package from npm runs with the user's rights, so pin the version. Screenshots of local test data go to the model, so never point it at production (real children's data). `--allowed-origins` is not a security boundary. | Claude in Chrome (partly) | **Essential** |
| **Microsoft.Playwright** (NuGet) | Test library | [NuGet](https://www.nuget.org/packages/Microsoft.Playwright) | 1.64.1, 72 M downloads | Lets the gate drive a real browser from xUnit. Scripted WCAG checks; aria snapshots via `ToMatchAriaSnapshotAsync` ([assertions](https://playwright.dev/dotnet/docs/test-assertions)) | Native .NET. `WebApplicationFactory.UseKestrel()` is new in `Mvc.Testing` 10.0 ([Learn](https://learn.microsoft.com/dotnet/api/microsoft.aspnetcore.mvc.testing.webapplicationfactory-1.usekestrel?view=aspnetcore-10.0)), so the existing `VlmsWebFactory` (SQLite in memory) can serve a real port. No PostgreSQL needed in tests. | NuGet only. It **bundles its own Node driver**, but there's no npm or `package.json`. Browsers are a one-off download per machine (`playwright.ps1 install chromium`). | Downloads browser binaries from Microsoft's CDN | Nothing | **Essential** |
| **Deque.AxeCore.Playwright** (NuGet) | Test library | [NuGet](https://www.nuget.org/packages/Deque.AxeCore.Playwright), [README](https://github.com/dequelabs/axe-core-nuget/blob/develop/packages/playwright/README.md) | Deque (makers of axe); 4.13.0 | Automated WCAG rules: contrast, names, ARIA, landmarks, labels. Filter by WCAG level with `RunOnly` tags (`wcag2a`, `wcag2aa`, `wcag21aa`, `wcag22aa`) | Native .NET (.NET Standard 2.1). README shows NUnit, but it's just an `IPage` extension, so xUnit works. | NuGet only. MIT, with a dependency under the MPL-2.0 licence. | Runs locally | Replaces `design:accessibility-review` as the authority | **Essential** |
| **Verify.Playwright** (NuGet) | Snapshot testing | [Verify.HeadlessBrowsers](https://github.com/VerifyTests/Verify.HeadlessBrowsers) | 3.1.2, 444 K downloads, maintained (updated 23/10/2026) | Gap 3. `Verify(page)` saves HTML and a PNG as approved baselines | Native .NET, targets net10.0. Needs `Verify.Xunit` because the repo is on xunit 2.9.3. | NuGet only | Baseline images are committed to a **public** repo, so they must use only synthetic seed data | Nothing | **Useful, add later** (see §6) |
| ↳ *Correction, 23/10/2026* | | | | | | | **Verify now charges a maintenance fee.** Found at install: Verify 33.x fails the build (SponsorCheck SC021) until one MSBuild property is set: a GitHub sponsorship, a time-bounded licence, or a claimed exemption. Exemptions offered: `OpenSource` (open-source projects with no revenue) or `SmallRevenue` (under US$10,000 a year), each renewed at most 12 months ahead. The alternative is `Verify_SponsorshipLicenseIgnored`, which the package states puts the build in breach of its licence. Claiming an exemption is Philip's decision. Alternative if declined: a small in-house pixel comparison over Playwright `ScreenshotAsync` images. | | |
| **Chrome DevTools MCP** | MCP server | [ChromeDevTools/chrome-devtools-mcp](https://github.com/ChromeDevTools/chrome-devtools-mcp) | Google; npm 1.12.0 | `lighthouse_audit` (accessibility and best practices), `get_css_styles`, `emulate`, PWA install tools ([tool reference](https://github.com/ChromeDevTools/chrome-devtools-mcp/blob/main/docs/tool-reference.md)) | Works with any framework | Requires a Node **LTS** release (Node 25 on this machine isn't LTS) | **Usage statistics on by default** (`--no-usage-statistics`); sends trace URLs to Google's CrUX API (`--no-performance-crux`) | Playwright MCP (browser); axe (Lighthouse's accessibility checks are axe-based) | **Optional.** Only worth it for PWA install or performance work. |
| **SVGO** | Command line, run via npx | [svg/svgo](https://github.com/svg/svgo) | ~22 k stars; 4.1.0 | Minifies ornament and badge SVGs | Run once over files in `wwwroot` | Node via `npx`, not in the repo | Runs locally | Hand optimisation | **Optional** |
| Inkscape | Desktop app | inkscape.org | Mature | For Philip to trace or edit complex ornament by hand | Not applicable | winget install | Runs locally | Claude editing SVG as text | **Optional** (Philip's tool) |
| Accessibility Insights for Web / NVDA | Browser extension / screen reader | Microsoft / NV Access | Mature | For Philip to run the checklist passes by hand (screen reader, assisted assessment) | Not applicable | Browser extension / desktop app | Runs locally | None (manual checks stay manual) | **Useful, for Philip.** WCAG 2.2 coverage of Accessibility Insights not confirmed. |
| bUnit | Test library | NuGet | Mature | Tests Razor components without a browser | Native | NuGet | None | Browser tests, for rendered markup | **Do not install.** It doesn't render CSS, so it doesn't help a visual redesign. |
| Style Dictionary / token pipelines | npm build | Amazon | Mature | Builds tokens for several platforms | Poor | **An npm build pipeline** | None | CSS custom properties | **Do not install** |
| Storybook / Blazor equivalents | npm / immature ports | Various | — | Component catalogue | Poor | npm | None | A dev-only style-guide page inside the app | **Do not install** |
| Tailwind, shadcn and other React-oriented kits | — | — | — | — | Breaks the "no npm" constraint | npm | — | — | **Do not install** |
| Figma Pro | SaaS | Figma | — | Design canvas | No Blazor mapping | Account, paid seat | Designs held by a third party | Designing in code | **Do not install** unless already in use |

## 4. Recommended toolchain

| Stage | Tool |
|---|---|
| Design direction, typography, colour | frontend-design, then VectorLab (`typography`, `colour-palette`, `spacing`) |
| Page structure and interaction | VectorLab (`page-patterns`, `surfaces`, `forms`, `keyboard`, `viewports`) |
| Design system | CSS custom properties in `app.css`, `*.razor.css`, plus a **dev-only `/dev/style-guide` page** of real Razor components in every state. This page is the visual documentation; `design:design-system` audits it. |
| Proposals for approval | Artifact pages for mood board and palette; then the style-guide page itself |
| Render and inspect | **Playwright MCP**: Claude drives `localhost:5074` at 375, 768 and 1280 px, plus 320 px for reflow, and reads the screenshots |
| Critique | VectorLab `ux-audit`, `design:design-critique`, `ux-copy` |
| Accessibility (automatic) | **Browser tests**: axe with WCAG 2.2 AA tags, plus scripted checks for reflow at 320 px, 24 px target sizes, focus visible / not obscured, keyboard tab order, and text spacing |
| Accessibility (by hand) | The existing signed checklist, now limited to what tools can't judge: screen-reader flow, cognitive load, quality of wording |
| Regression | Aria snapshots (stable, check structure) now; Verify screenshots once the tokens settle |
| Watched sessions | Claude in Chrome |
| Done | `build/verify.ps1` exits 0 |

How they work together: the skills set the rules, Playwright MCP lets Claude see the result while iterating, and the browser tests make the gate enforce what can be measured. A design rule like "24 px targets" or "4.5:1 contrast" becomes a test that fails, not something Claude judges itself.

## 5. Installation plan (not run)

**Already installed, no action:** frontend-design, VectorLab, design, loop-harness, Claude in Chrome, Microsoft Learn MCP, Node 25, .NET 10 SDK.

**Needs installation, with no new build dependency in the repo:**

```powershell
# A. Playwright MCP, project scope, pinned version, isolated, using installed Chrome; output to gitignored artifacts/
claude mcp add --scope project playwright -- npx -y @playwright/mcp@0.0.83 `
  --browser chrome --isolated --headless --viewport-size 1280x800 `
  --allowed-origins "http://localhost:5074;https://localhost:7173" --output-dir artifacts/ui
# Optional second instance with phone emulation (touch, pixel density, mobile viewport):
claude mcp add --scope project playwright-mobile -- npx -y @playwright/mcp@0.0.83 `
  --browser chrome --isolated --headless --device "Pixel 7" --output-dir artifacts/ui
```

This writes `.mcp.json`, which gets committed. It holds no secrets, but the repo is public. Drop `--headless` to watch the browser.

**Needs installation, NuGet only (a `STATE.md` item, built through the normal loop):**

```powershell
dotnet new xunit -o tests/Vlms.UiTests        # separate project, so -Fast can skip it cleanly
dotnet add tests/Vlms.UiTests package Microsoft.Playwright --version 1.64.1
dotnet add tests/Vlms.UiTests package Deque.AxeCore.Playwright --version 4.13.0
dotnet build tests/Vlms.UiTests
pwsh tests/Vlms.UiTests/bin/Debug/net10.0/playwright.ps1 install chromium   # one-off per machine
# Later, visual regression phase:
dotnet add tests/Vlms.UiTests package Verify.Xunit
dotnet add tests/Vlms.UiTests package Verify.Playwright --version 3.1.2
```

Gate change: run `Vlms.UiTests` in full mode only, as a new stage next to accessibility. `-Fast` excludes it. `VlmsWebFactory` gains a `UseKestrel(0)` variant. This needs ADR-0009 to reverse the rationale in `check-accessibility.ps1`. The exact setup for sharing the factory between the two test projects is to be confirmed when the item is built.

**Config change:** in `.claude/settings.json`, set `"figma@claude-plugins-official": false`.

**Needs an external account:** none of the recommended tools. Figma would need a paid seat; Chrome DevTools MCP needs no account but sends telemetry unless turned off.

**Uses npm/Node** (on the dev machine only; never in the repo or the build): Playwright MCP, and SVGO / Chrome DevTools MCP if chosen. `Microsoft.Playwright` bundles a Node driver inside the NuGet package. That's internal to the package and needs no npm.

## 6. Proposed design workflow

Changes from the outline in questionnaire answer 45: two safety-net steps up front, design tokens before pages, and visual regression held back until after the vertical slice.

1. **Repository and content inventory.** Read `openwiki/`, the questionnaire answers, and the list of all 43 `@page` routes by role. Record which tests assert on page text or element ids. Written up as a `docs/` file.
2. **Freeze a safety net.** Before any visual change, add the browser-test project and take **aria snapshots** of every route for each role. These record structure, not pixels, so the redesign can't silently drop a landmark, label or heading. Add the axe checks. Expect existing contrast failures, and record them as the starting point rather than hiding them.
3. **UX audit of the current UI.** Playwright MCP walks every route as Student, Parent, Teacher, Safeguarding Officer, Approver and Admin at 375, 768 and 1280 px. VectorLab `ux-audit` and `design-critique` produce a ranked findings list.
4. **Visual research and direction.** frontend-design sets the direction from the subject matter: Byzantine, Anglo-Saxon, the ten ranks, children aged 8–18. Two or three directions go on Artifact pages (palette, type, ornament, one sample screen) for Philip to choose from. This respects questionnaire answers 23–26 on sacred icons: real icons only, never decoration.
5. **Design system in code.** Tokens (colour checked for contrast at the token level, type scale, spacing, radius, motion and reduced motion), then components and their states, all on `/dev/style-guide`. axe and the scripted checks run against the style guide, so every component passes before it reaches a page. Fonts are hosted locally in `wwwroot` (no CDN; this is a children's app with a strict Content Security Policy).
6. **Vertical slice.** `/student` and `/progress/record` are built, rendered at all viewports, critiqued, fixed, and passed through the gate. **Philip approves** before the rest proceeds (questionnaire 46).
7. **Turn on visual regression.** Only now, so baselines don't churn while the tokens are still moving. Screenshot baselines for about 10 key pages × 3 viewports, generated on the Windows dev machine only, from synthetic seed data.
8. **Roll out by area**, one `STATE.md` item per area. Each page goes through: implement → render with Playwright MCP → critique against the skills → automated accessibility checks → aria and visual diffs reviewed and approved on purpose → gate → ASVS and WCAG checklist rows.
9. **Hand-check pass** by Philip: NVDA screen reader, 400% zoom, a real phone, and the PWA install, against the reduced checklist.
10. **Polish:** motion, empty states, error wording; re-render; gate.

## Decision table

| # | Decision | Recommendation | Philip's decision |
|---|---|---|---|
| 1 | Keep frontend-design, VectorLab, design, loop-harness, Microsoft Learn MCP | Yes (no action) | |
| 2 | Keep Claude in Chrome as a secondary tool for watched sessions | Yes (no action) | |
| 3 | Install Playwright MCP (project scope, pinned 0.0.83, isolated) | **Essential** | Approved; installed 23/10/2026 |
| 4 | Add the second Playwright MCP instance with Pixel 7 emulation | Useful | Approved; installed 23/10/2026 |
| 5 | Add `tests/Vlms.UiTests` with `Microsoft.Playwright` and `Deque.AxeCore.Playwright` as a full-gate stage, plus ADR-0009 reversing the Playwright rejection in `check-accessibility.ps1` | **Essential** | Approved 23/10/2026. Done: `tests/Vlms.UiTests` (xunit 2.9.3, `Microsoft.Playwright` 1.64.1, `Deque.AxeCore.Playwright` 4.13.0) in `Vlms.slnx`; Chromium 1248 installed; `.gitleaks.toml` allowlists only Playwright's bundled driver under `bin/` (a planted key elsewhere still fails the scan); full gate verify OK, 1027/1027. To do: ADR-0009, `VlmsWebFactory` with `UseKestrel(0)`, first axe and WCAG tests, full-only gate stage with `-Fast` skipping `Vlms.UiTests`. |
| 6 | Add `Verify.Playwright` visual regression after the vertical slice | Useful (deferred) | Approved 23/10/2026: Philip claimed the OpenSource exemption (no revenue). `Verify.Xunit` 31.12.5 and `Verify.Playwright` 3.1.2 in `tests/Vlms.UiTests`, with `Verify_SponsorshipExemption=OpenSource` until 2027-10 (the build fails after that until renewed). Baselines still wait until after the vertical slice (§6 step 7). |
| 7 | Disable the Figma plugin | Yes, unless a paid Figma seat exists | Applied 23/10/2026: `"figma@claude-plugins-official": false` in `.claude/settings.json`. |
| 8 | Chrome DevTools MCP | Optional; skip for now | Installed 23/10/2026 at Philip's request, telemetry off: project-scope `.mcp.json`, `chrome-devtools-mcp@1.12.0 --isolated --headless --no-usage-statistics --no-performance-crux`, plus env `CHROME_DEVTOOLS_MCP_NO_USAGE_STATISTICS=1` and `CHROME_DEVTOOLS_MCP_NO_UPDATE_CHECKS=1`. Runs on Node 25 (not LTS); flags confirmed with `--help`. |
| 9 | SVGO via npx when needed | Optional | |
| 10 | Don't use `design:accessibility-review` as the WCAG authority | Yes | |
| 11 | Style Dictionary, Storybook, bUnit, Tailwind and React-oriented kits | Do not install | |

## Sources

- [Playwright MCP](https://github.com/microsoft/playwright-mcp) · [Playwright MCP snapshots](https://playwright.dev/mcp/snapshots)
- [Playwright .NET assertions](https://playwright.dev/dotnet/docs/test-assertions) · [Microsoft.Playwright on NuGet](https://www.nuget.org/packages/Microsoft.Playwright)
- [WebApplicationFactory.UseKestrel (Microsoft Learn)](https://learn.microsoft.com/dotnet/api/microsoft.aspnetcore.mvc.testing.webapplicationfactory-1.usekestrel?view=aspnetcore-10.0)
- [Deque.AxeCore.Playwright on NuGet](https://www.nuget.org/packages/Deque.AxeCore.Playwright) · [README](https://github.com/dequelabs/axe-core-nuget/blob/develop/packages/playwright/README.md) · [axe-core 4.5 WCAG 2.2 support](https://www.deque.com/blog/axe-core-4-5-first-wcag-2-2-support-and-more/)
- [Verify.HeadlessBrowsers](https://github.com/VerifyTests/Verify.HeadlessBrowsers) · [Verify.Playwright on NuGet](https://www.nuget.org/packages/Verify.Playwright)
- [Chrome DevTools MCP](https://github.com/ChromeDevTools/chrome-devtools-mcp) · [tool reference](https://github.com/ChromeDevTools/chrome-devtools-mcp/blob/main/docs/tool-reference.md)
- [Figma MCP rate limits](https://developers.figma.com/docs/figma-mcp-server/rate-limits-access/) · [plans and permissions](https://developers.figma.com/docs/figma-mcp-server/plans-access-and-permissions/)
- [SVGO star history](https://www.star-history.com/svg/svgo)
- [TestingBot: Playwright MCP](https://testingbot.com/resources/articles/playwright-mcp) (secondary source)
