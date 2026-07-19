# The `build/verify.ps1` gate

`pwsh -File build/verify.ps1` (full) / `-Fast` (quick). Stages, in order: `build` (`dotnet build
-warnaserror`), `test` (`dotnet test`), `secrets` (gitleaks or a regex fallback), then — full runs
only — `access-control (ASVS 5.0 V8)` and `accessibility (WCAG 2.2 AA)`. Every stage is designed to
be genuinely falsifiable (`loop-harness:verify-gate-authoring`'s standard) — none of them print a
placeholder and always exit 0.

## The two full-only stages

`build/check-access-control.ps1` and `build/check-accessibility.ps1` (invoked from `verify.ps1`,
runnable standalone too). Each does two things:

1. **A mechanical scan** of the current source tree for a narrow set of things that genuinely can be
   checked without a human — see each script's own header comment for exactly what and why. Neither
   script re-runs `dotnet test` (already the `test` stage's job) — instead each adds checks that
   stage doesn't cover, e.g. a regression guard on specific test files' method counts (a deleted test
   file still makes `dotnet test` exit 0 with fewer tests; these scripts catch that).
2. **A content-hash currency check** on a paired manual-review checklist
   (`docs/governance/asvs-access-control-checklist.md`, `docs/quality/wcag-2.2-aa-checklist.md`) —
   see "Keeping a checklist current" below.

**Chapter-numbering correction:** `STATE.md`'s item wording (and the ADR-0004 Consequences section)
named this "ASVS 5.0 V1" — that's the ASVS 4.0.3 numbering, where access control was V4. ASVS 5.0
renumbered its chapters; V1 is now "Encoding and Sanitization" and access control/authorization is
**V8**. Checked against OWASP's own 5.0.0 table of contents before writing anything, per CLAUDE.md's
"verify, don't invent" rule — see `docs/governance/asvs-access-control-checklist.md`'s own note.

### `access-control (ASVS 5.0 V8)`

- Every routable page under `Components/Pages/` (has an `@page` directive) has an
  `@attribute [Authorize(...)]`, except a named allow-list (`Home.razor` — gated per-link via
  `<AuthorizeView>`, not page-level; `Error.razor`/`NotFound.razor` — nothing sensitive).
- Zero `.IgnoreQueryFilters()` call sites anywhere under `src/` — the only sanctioned bypass of the
  ADR-0004 whole-entity query filters, and it must stay absent by default. The regex requires a
  leading `.` so it only matches real call syntax, not the `<c>IgnoreQueryFilters()</c>` (no leading
  dot) wording already used in doc comments across this codebase.
- `SensitiveDataAccessControlTests.cs` / `ConsentRecordServiceTests.cs` / `DbsCheckServiceTests.cs` /
  `GuardianLinkServiceTests.cs` (`quality/test-plan.md` TC-007/008/011/012) each still have at least
  as many `[Fact]`/`[Theory]` methods as when this stage was written (a floor, not a ceiling).

### `accessibility (WCAG 2.2 AA)`

Checked first, not guessed: there is no established, genuinely CI-runnable static/build-time
accessibility linter for Razor/Blazor markup (the real tooling — axe-core, Accessibility Insights —
needs a live browser driving a running app; disproportionate new machinery for a solo, no-CI-yet
project per this stage). So the automated slice is narrow and markup-only:

- Every `<img>` has an `alt` attribute.
- Every `InputText`/`InputTextArea`/`InputDate`/`InputNumber`/`InputSelect` that declares an `id`
  has a matching `<label for="id">` in the same file — this codebase's forms consistently use that
  pairing (see `Registration/RegisterStudent.razor`), so this is a real check of an existing
  convention, not a general accessible-name solver.
- `App.razor`'s root `<html>` element declares a `lang` attribute.

## Keeping a checklist current

Both checklists carry a `Reviewed-hash: <sha256>` line at the bottom
(`build/lib-checklist-currency.ps1` — shared by both scripts). The hash is computed over a named set
of source files (see each script's `$hashedFiles` build-up): authorization/page files for the
access-control checklist, all Razor markup + `app.css` for the accessibility one. If those files
change without the checklist being re-reviewed, the recorded hash no longer matches what's on disk
and the stage fails — a checklist that nobody has re-touched since the underlying code moved is
exactly the "gate that always passes" failure mode `verify-gate-authoring` warns against, and this
is how it's avoided without needing CI or a live browser.

**To update a checklist after changing relevant code:** work through the checklist's own items
against the current codebase, update its Sign-off table, then run
`pwsh -File build/check-access-control.ps1 -PrintHash` (or the accessibility equivalent) and paste
the printed `Reviewed-hash:` line over the old one. Don't paste a hash without actually reviewing —
the mechanism only proves the checklist was *touched* at the current code state, not that the review
was thorough; that part is still on the human.

## Verified falsifiable, not just written

Before committing, each of these was deliberately broken and confirmed to fail the relevant stage,
then restored and confirmed green again: a page's `[Authorize]` attribute removed; a real
`.IgnoreQueryFilters()` call added; a `[Fact]` attribute deleted from
`SensitiveDataAccessControlTests.cs`; an `<img>` added with no `alt`; a `<label for="...">` removed
from `RegisterStudent.razor`. Each of those five also demonstrated the checklist-currency check
firing as a second, independent failure (since the edited file was in the relevant hash set).
