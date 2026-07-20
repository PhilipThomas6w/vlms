# The `build/verify.ps1` gate

`pwsh -File build/verify.ps1` (full) / `-Fast` (quick). Stages, in order: `build` (`dotnet build
-warnaserror`), `test` (`dotnet test`), `secrets` (`build/check-secrets.ps1` — gitleaks if installed,
else a hardened regex fallback), then — full runs only — `access-control (ASVS 5.0 V8)` and
`accessibility (WCAG 2.2 AA)`. Every stage is designed to be genuinely falsifiable
(`loop-harness:verify-gate-authoring`'s standard) — none of them print a placeholder and always exit
0.

## The `secrets` stage (`build/check-secrets.ps1`, runs in both `-Fast` and full)

gitleaks is not installed in this environment (confirmed, not assumed), and this is a solo-developer
"fewest moving parts" build — making the gate the Stop hook runs on every turn end depend on a
newly-provisioned external binary is exactly the kind of moving part `adr/0003`'s reasoning rejects
elsewhere (e.g. no axe-core/browser driver for the accessibility stage). So the regex fallback is a
first-class, load-bearing path, not a placeholder — gitleaks stays the preferred path if ever
installed.

Credential shapes matched, checked against real Azure/SQL/Entra formats rather than guessed: Azure
Storage `AccountKey=`, Azure Communication Services `accesskey=` (one 30+ base64-char pattern covers
both — real ACS keys are 44 chars, Storage 88), SQL `Password=`/`pwd=` (both quoted and unquoted
connection-string forms), Entra/Graph client secret values (gitleaks' own `azure-ad-client-secret`
shape) plus explicit `ClientSecret=`/`client_secret:` assignments, and the pre-existing AWS-key/PEM
patterns. False positives are filtered by scoping the `PLACEHOLDER` sentinel check to the matched
credential *value* (case-sensitive), not the whole line — verified clean against this repo's own
`appsettings.json`/`Vlms.Jobs/appsettings.json` placeholders and the local-dev `Trusted_Connection`
string. Test/fixture files are excluded by real path segments (`tests/` directories, `*fixture*`
filenames), not a bare substring — a file like `latest-import.json` is deliberately **not** excluded
just because it contains "test".

**Checker-FAIL round-trip (commit `a78cc18`, after the initial tightening in `59a069a`) found three
real holes**, all since fixed: (1) the new unquoted-only password pattern had *dropped* coverage for
a quoted hardcoded password (`password = "..."`) that the pre-tightening regex used to catch — fixed
by making the opening quote optional; (2) the `PLACEHOLDER` filter was whole-line and
case-insensitive, so a real key sharing a line with any casing of "placeholder" scanned clean — fixed
by scoping it to the matched value, case-sensitively; (3) the `test`/`fixture` exclusion was a bare
substring (matching "la**test**"), not path-segment-anchored — fixed as described above. Each hole
was verified by deliberate break/restore (a throwaway probe reproducing the exact failure mode,
confirmed to flip the stage from 0 to 1 and back), the same discipline the two full-only stages below
use.

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
