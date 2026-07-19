# OWASP ASVS 5.0 access-control review — build/verify.ps1 full-only stage.
#
# Correction versus how this stage was originally named in STATE.md/verify.ps1: ASVS 5.0
# renumbered its chapters from 4.0.3 — Authorization is chapter V8 in 5.0, not V1 (V1 is now
# "Encoding and Sanitization"). Checked against OWASP's own 5.0.0 table of contents rather than
# assumed. This stage targets V8 (Authorization), with supporting checks against V14 (Data
# Protection — the whole-entity restriction on DbsCheck/ConsentSensitiveDetails) and V16 (Security
# Logging — the read-audit trail), matching adr/0004-sensitive-data-access-control.md.
#
# What's mechanically checkable and automated below (genuinely falsifiable — see
# loop-harness:verify-gate-authoring):
#   1. Every routable Razor page has an [Authorize] attribute, except a named, deliberate allow-list.
#   2. No call to IgnoreQueryFilters() exists anywhere in src/ — the only sanctioned bypass of the
#      ADR-0004 query filters on DbsCheck/ConsentSensitiveDetails, and it must stay absent.
#   3. The named access-control test suites (quality/test-plan.md TC-007/008/011/012) still exist
#      and still have at least as many test methods as when this stage was written — a regression
#      guard `dotnet test` alone cannot provide, since a deleted test file just makes that stage
#      pass with fewer tests, not fail.
#
# What's NOT mechanically checkable here — business-logic-level access control review, defense-in-
# depth completeness, whether new code introduces a new access path that isn't yet covered by any
# of the above — stays a human-signed checklist (docs/governance/asvs-access-control-checklist.md),
# gated for currency via lib-checklist-currency.ps1's content-hash mechanism: change any file in the
# hashed set without updating the checklist's recorded hash, and this stage fails.

param([switch]$PrintHash)

$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path "$PSScriptRoot/..").Path
. "$PSScriptRoot/lib-checklist-currency.ps1"

$failures = @()

function RelativePath($fullName) {
    return $fullName.Substring($repoRoot.Length + 1)
}

# --- 1. Every routable page has [Authorize], except a deliberate, named allow-list ---------------
$pagesRoot = Join-Path $repoRoot "src/Vlms.Web/Components/Pages"
# Home.razor: a shell page with no sensitive content of its own — every link on it is individually
# gated by <AuthorizeView Policy="..."> per role, not a single page-level policy (see the page).
# Error.razor / NotFound.razor: framework error pages, nothing sensitive to protect.
$allowListedPages = @("Home.razor", "Error.razor", "NotFound.razor")
$pageFiles = Get-ChildItem -Path $pagesRoot -Recurse -Filter "*.razor"

foreach ($file in $pageFiles) {
    $content = Get-Content -Raw -LiteralPath $file.FullName
    if ($content -notmatch '(?m)^\s*@page\b') { continue }
    if ($allowListedPages -contains $file.Name) { continue }
    if ($content -notmatch '@attribute\s*\[Authorize') {
        $failures += "missing [Authorize] attribute on routable page: $(RelativePath $file.FullName)"
    }
}

# --- 2. IgnoreQueryFilters() must not appear anywhere -----------------------------------------
$srcRoot = Join-Path $repoRoot "src"
$csFiles = Get-ChildItem -Path $srcRoot -Recurse -Filter "*.cs" |
    Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' }

foreach ($file in $csFiles) {
    $content = Get-Content -Raw -LiteralPath $file.FullName
    if ($content -match '\.IgnoreQueryFilters\s*\(') {
        $failures += "IgnoreQueryFilters() call found — must stay absent by default (adr/0004 decision #2): $(RelativePath $file.FullName)"
    }
}

# --- 3. Access-control test suites must still exist with at least their original test count -----
# (test-plan.md TC-007/011 -> SensitiveDataAccessControlTests; TC-008 -> Consent/DbsCheck service
# denial tests; TC-012 -> GuardianLinkServiceTests). Counts are the number present when this stage
# was written (2026-07-19) — a floor, not a ceiling; adding more tests never fails this check.
$testRoot = Join-Path $repoRoot "tests/Vlms.Tests/Infrastructure"
$expectedMinimums = @{
    "SensitiveDataAccessControlTests.cs" = 5
    "ConsentRecordServiceTests.cs"       = 10
    "DbsCheckServiceTests.cs"            = 8
    "GuardianLinkServiceTests.cs"        = 13
}

foreach ($testFileName in $expectedMinimums.Keys) {
    $testFilePath = Join-Path $testRoot $testFileName
    if (-not (Test-Path -LiteralPath $testFilePath)) {
        $failures += "access-control test file missing entirely: tests/Vlms.Tests/Infrastructure/$testFileName"
        continue
    }
    $testContent = Get-Content -Raw -LiteralPath $testFilePath
    $actualCount = ([regex]::Matches($testContent, '\[Fact\]|\[Theory\]')).Count
    $expected = $expectedMinimums[$testFileName]
    if ($actualCount -lt $expected) {
        $failures += "tests/Vlms.Tests/Infrastructure/$testFileName has only $actualCount test method(s), expected at least $expected — a test appears to have been removed"
    }
}

# --- 4. Manual-review checklist currency (content-hash gate) ------------------------------------
$hashedFiles = @()
$hashedFiles += Get-ChildItem (Join-Path $repoRoot "src/Vlms.Infrastructure/Authorization") -Filter "*.cs"
$hashedFiles += Get-Item (Join-Path $repoRoot "src/Vlms.Infrastructure/VlmsDbContext.cs")
$hashedFiles += Get-Item (Join-Path $repoRoot "src/Vlms.Infrastructure/Auditing/SensitiveDataAuditInterceptor.cs")
$hashedFiles += Get-Item (Join-Path $repoRoot "src/Vlms.Web/Program.cs")
$hashedFiles += $pageFiles

if ($PrintHash) {
    $hash = Get-ChecklistSourceHash -Files $hashedFiles
    Write-Host "Reviewed-hash: $hash"
    Write-Host "($($hashedFiles.Count) files hashed — paste the line above into docs/governance/asvs-access-control-checklist.md after reviewing it)"
    exit 0
}

$checklistPath = Join-Path $repoRoot "docs/governance/asvs-access-control-checklist.md"
$checklistResult = Test-ChecklistCurrency -ChecklistPath $checklistPath -SourceFiles $hashedFiles
if (-not $checklistResult.IsCurrent) {
    $failures += $checklistResult.Message
}

if ($failures.Count -gt 0) {
    Write-Host "OWASP ASVS 5.0 access-control review (V8 Authorization): FAILED" -ForegroundColor Red
    foreach ($f in $failures) { Write-Host " - $f" -ForegroundColor Red }
    exit 1
}

Write-Host "OWASP ASVS 5.0 access-control review (V8 Authorization): passed — $($pageFiles.Count) pages checked, 0 IgnoreQueryFilters() calls, $($expectedMinimums.Count) test suites at/above floor, checklist current ($($hashedFiles.Count) source files hashed)"
exit 0
