# Secret-scanning — build/verify.ps1 stage (runs in both -Fast and full).
#
# Extracted into its own script (same convention as check-access-control.ps1 /
# check-accessibility.ps1) so the stage is independently runnable and therefore
# independently falsifiable — see loop-harness:verify-gate-authoring. A secret scan
# that can never return non-zero on this repo is not a gate.
#
# gitleaks vs. hand-rolled regex — a deliberate, documented choice (not defaulted):
#   gitleaks is the intended real scanner and is used when present. It is NOT installed
#   in this environment, and this project is a solo-developer build whose architecture
#   principle is "fewest moving parts" (adr/0003 reasoning, applied elsewhere to reject
#   heavier tooling — e.g. axe-core/browser drivers for the accessibility stage). Making a
#   green gate depend on a newly-installed external binary — one the Stop hook would run on
#   every turn end, and every future checkout/CI would have to install — is exactly the kind
#   of moving part that principle rejects. So the regex fallback is treated as a first-class,
#   load-bearing path (not a token stand-in) and is tightened to cover the credential SHAPES
#   this stack actually uses (adr/0001): Azure Storage AccountKey=, Azure Communication
#   Services accesskey=, SQL Password=/pwd=, and Entra/Graph client secrets — alongside the
#   AWS-key and PEM-block patterns it already had. gitleaks stays the preferred path if ever
#   installed; the fallback message names it so.
#
# False-positive discipline: this repo's own config uses the literal sentinel PLACEHOLDER for
# every not-yet-provisioned credential (appsettings.json AzureAd, Vlms.Jobs appsettings.json
# CommunicationServices — including "accesskey=PLACEHOLDER"). The shape patterns require a
# real-length/high-entropy value (e.g. a 30+ char base64 key), which PLACEHOLDER never reaches,
# and any matched credential VALUE still containing the literal PLACEHOLDER sentinel is filtered
# out. The filter is scoped to the matched value (not the whole line) and is case-sensitive
# against the uppercase sentinel — so a real key pasted onto the same physical line as an
# incidental mention of the word "placeholder" is still caught, while intentional placeholder
# config passes cleanly.
#
# Path exclusion: the scan skips this repo's own test files (which legitimately hold fake,
# secret-shaped strings — FakeEmailSender, FakeCurrentUserContext, test fixtures) by anchoring
# the exclusion to actual path SEGMENTS (a tests/ directory, or a *fixture* filename) rather
# than a bare substring — so a real key in e.g. src/.../latest-import.json (contains "test") is
# NOT silently skipped.

$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path "$PSScriptRoot/..").Path

function RelativePath($fullName) {
    return $fullName.Substring($repoRoot.Length + 1)
}

# --- Preferred path: gitleaks, if installed --------------------------------------------------
if (Get-Command gitleaks -ErrorAction SilentlyContinue) {
    gitleaks detect --no-git --redact --exit-code 1
    if ($LASTEXITCODE -ne 0) { exit 1 }
    Write-Host "secret scan (gitleaks): clean"
    exit 0
}

# --- Fallback path: tightened regex ---------------------------------------------------------
# Each entry: a label (for evidence output) and the pattern. Patterns are matched per line;
# a match whose credential value contains the literal PLACEHOLDER sentinel is treated as
# intentional placeholder config and skipped. The SQL/password pattern matches both the
# unquoted connection-string form (Password=Sup3rSecret123;) and the quoted C#-assignment form
# (password = "Sup3rSecretHardcoded") — the opening quote is optional.
$patterns = @(
    @{ Label = "AWS access key id";                    Pattern = 'AKIA[0-9A-Z]{16}' }
    @{ Label = "PEM private key block";                Pattern = 'BEGIN (RSA |EC |DSA |OPENSSH )?PRIVATE KEY' }
    @{ Label = "Azure Storage / Communication Services key"; Pattern = '(?i)(AccountKey|accesskey)\s*=\s*[A-Za-z0-9+/]{30,}={0,2}' }
    @{ Label = "SQL / connection-string password";     Pattern = '(?i)\b(password|pwd)\s*=\s*[''"]?[^;''"\s]{3,}' }
    @{ Label = "Entra/Graph client secret value";      Pattern = '[a-zA-Z0-9_~.]{3}\dQ~[a-zA-Z0-9_~.\-]{31,34}' }
    @{ Label = "explicit client secret assignment";    Pattern = '(?i)client_?secret\W{1,4}[A-Za-z0-9._~+/\-]{8,}' }
)

$scanFiles = Get-ChildItem -Path $repoRoot -Recurse -Include *.cs, *.json, *.yaml, *.yml -ErrorAction SilentlyContinue |
    Where-Object {
        $_.FullName -notmatch '[\\/](\.git|node_modules|bin|obj)[\\/]' -and
        $_.FullName -notmatch '(?i)[\\/]tests?[\\/]' -and
        $_.FullName -notmatch '(?i)[\\/][^\\/]*fixtures?[^\\/]*\.[^\\/]+$'
    }

$hits = @()
foreach ($entry in $patterns) {
    $matches = $scanFiles | Select-String -Pattern $entry.Pattern
    foreach ($m in $matches) {
        if ($m.Matches[0].Value -clike '*PLACEHOLDER*') { continue }   # intentional placeholder config — scoped to the matched credential VALUE, and case-sensitive against the literal uppercase sentinel this repo uses (never the whole line)
        $hits += [pscustomobject]@{
            Label = $entry.Label
            Path  = RelativePath $m.Path
            Line  = $m.LineNumber
        }
    }
}

if ($hits.Count -gt 0) {
    Write-Host "secret scan (regex fallback): FAILED — possible committed secret(s)" -ForegroundColor Red
    foreach ($h in $hits) {
        # Report location and matched credential type only — never echo the value itself.
        Write-Host (" - {0}:{1}  [{2}]" -f $h.Path, $h.Line, $h.Label) -ForegroundColor Red
    }
    Write-Error "possible secret"
    exit 1
}

Write-Host "secret scan (regex fallback — install gitleaks for a real scan): clean ($($scanFiles.Count) files, $($patterns.Count) credential-shape patterns)"
exit 0
