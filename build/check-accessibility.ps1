# WCAG 2.2 AA accessibility check — build/verify.ps1 full-only stage.
#
# Verified before building this (not guessed, per CLAUDE.md "verify, don't invent"): there is no
# established, genuinely CI-runnable static/build-time accessibility linter for Razor/Blazor markup
# comparable to eslint-plugin-jsx-a11y for JSX. The real tooling in this space (axe-core, Deque's
# axe DevTools, Microsoft's Accessibility Insights) all works by inspecting a rendered DOM — it
# needs a live browser (Playwright/Selenium) driving a running instance of the app. For a
# Server-interactive Blazor app with no CI pipeline yet and one solo maintainer, standing up a
# Playwright + browser-install + hosted-app harness inside every `verify.ps1 -Full` run is a
# disproportionate amount of new moving parts for this project's stage (ADR-0003's "fewest moving
# parts" reasoning applies equally here) — and it still wouldn't run in -Fast, so it buys nothing
# for the common case. Decision: automate what's genuinely checkable from the markup alone without a
# browser, and gate everything else (contrast, focus order, zoom/reflow, screen-reader flow) behind
# a human-signed checklist, same currency mechanism as check-access-control.ps1.
#
# Automated below (genuinely falsifiable):
#   1. Every <img> element has an alt attribute (WCAG 1.1.1 Non-text Content).
#   2. Every Blazor Input* component that declares an id also has a matching <label for="id">
#      somewhere in the same file (WCAG 1.3.1 Info and Relationships / 4.1.2 Name, Role, Value) —
#      this codebase's forms consistently use the id/label-for pairing (see e.g.
#      Registration/RegisterStudent.razor), so this is a real, non-decorative check of that pattern,
#      not an attempt to fully solve accessible-name computation in general.
#   3. The root <html> element declares a lang attribute (WCAG 3.1.1 Language of Page).
#
# What's NOT mechanically checkable here — colour contrast, focus order, keyboard-only operation,
# 400% zoom/reflow, screen-reader announcement quality, error-message clarity — stays a human-signed
# checklist (docs/quality/wcag-2.2-aa-checklist.md), gated for currency the same way.

param([switch]$PrintHash)

$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path "$PSScriptRoot/..").Path
. "$PSScriptRoot/lib-checklist-currency.ps1"

$failures = @()

function RelativePath($fullName) {
    return $fullName.Substring($repoRoot.Length + 1)
}

$componentsRoot = Join-Path $repoRoot "src/Vlms.Web/Components"
$razorFiles = Get-ChildItem -Path $componentsRoot -Recurse -Filter "*.razor"

# --- 1. <img> must have alt ------------------------------------------------------------------
foreach ($file in $razorFiles) {
    $content = Get-Content -Raw -LiteralPath $file.FullName
    foreach ($imgMatch in [regex]::Matches($content, '<img\b[^>]*>')) {
        if ($imgMatch.Value -notmatch '\balt\s*=') {
            $failures += "<img> with no alt attribute in $(RelativePath $file.FullName): $($imgMatch.Value)"
        }
    }
}

# --- 2. Labelled Input* components -------------------------------------------------------------
$labelledInputTags = @("InputText", "InputTextArea", "InputDate", "InputNumber", "InputSelect")
foreach ($file in $razorFiles) {
    $content = Get-Content -Raw -LiteralPath $file.FullName
    $labelFors = [regex]::Matches($content, '<label\b[^>]*\bfor\s*=\s*"([^"]+)"') |
        ForEach-Object { $_.Groups[1].Value }

    foreach ($tag in $labelledInputTags) {
        foreach ($inputMatch in [regex]::Matches($content, "<$tag\b[^>]*>")) {
            $idMatch = [regex]::Match($inputMatch.Value, '\bid\s*=\s*"([^"]+)"')
            if (-not $idMatch.Success) { continue }
            $id = $idMatch.Groups[1].Value
            if ($labelFors -notcontains $id) {
                $failures += "<$tag id=`"$id`"> has no matching <label for=`"$id`"> in $(RelativePath $file.FullName)"
            }
        }
    }
}

# --- 3. <html lang="..."> ------------------------------------------------------------------------
$appRazor = Join-Path $componentsRoot "App.razor"
if (-not (Test-Path -LiteralPath $appRazor)) {
    $failures += "App.razor not found at expected path: $(RelativePath $appRazor)"
} else {
    $appContent = Get-Content -Raw -LiteralPath $appRazor
    if ($appContent -notmatch '<html\b[^>]*\blang\s*=\s*"[^"]+"') {
        $failures += "no <html lang=`"...`"> found in $(RelativePath $appRazor)"
    }
}

# --- 4. Manual-review checklist currency (content-hash gate) ------------------------------------
$hashedFiles = @()
$hashedFiles += $razorFiles
$appCssPath = Join-Path $repoRoot "src/Vlms.Web/wwwroot/app.css"
if (Test-Path -LiteralPath $appCssPath) { $hashedFiles += Get-Item $appCssPath }

if ($PrintHash) {
    $hash = Get-ChecklistSourceHash -Files $hashedFiles -RepoRoot $repoRoot
    Write-Host "Reviewed-hash: $hash"
    Write-Host "($($hashedFiles.Count) files hashed — paste the line above into docs/quality/wcag-2.2-aa-checklist.md after reviewing it)"
    exit 0
}

$checklistPath = Join-Path $repoRoot "docs/quality/wcag-2.2-aa-checklist.md"
$checklistResult = Test-ChecklistCurrency -ChecklistPath $checklistPath -SourceFiles $hashedFiles -RepoRoot $repoRoot
if (-not $checklistResult.IsCurrent) {
    $failures += $checklistResult.Message
}

if ($failures.Count -gt 0) {
    Write-Host "WCAG 2.2 AA accessibility check: FAILED" -ForegroundColor Red
    foreach ($f in $failures) { Write-Host " - $f" -ForegroundColor Red }
    exit 1
}

Write-Host "WCAG 2.2 AA accessibility check: passed — $($razorFiles.Count) Razor files scanned (img alt, labelled inputs, html lang), checklist current ($($hashedFiles.Count) source files hashed)"
exit 0
