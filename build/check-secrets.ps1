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
#   load-bearing path (not a token stand-in), kept current with the credential SHAPES this
#   stack ACTUALLY uses today. gitleaks stays the preferred path if ever installed; the
#   fallback message names it so.
#
# --- 2026-10-13: gitleaks installed (STATE.md "Real secret scanning") -----------------------------
# gitleaks 8.30.1 is now installed on the development machine (winget Gitleaks.Gitleaks) and is the
# primary scanner. The regex patterns below are NOT retired: probed against planted values, gitleaks'
# default rules caught the Entra client secret, AWS key id and connection-string password, but MISSED
# the JSON config-key password ("Password": "...") and the PostgreSQL `PASSWORD '...'` role syntax.
# So the regex set now runs in both cases — as a supplement after a clean gitleaks run, and as the
# whole scan when gitleaks is absent (another checkout). gitleaks is looked up on PATH, then in the
# winget links directory, because a shell started before the install has a stale PATH.
#
# --- 2026-07-21 currency pass (ADR-0005 hosting-pivot verify-gate audit, STATE.md) ------------
# Re-examined every pattern against the post-pivot stack (PostgreSQL / self-rolled ASP.NET Core
# Identity / local disk / SMTP — Azure SQL / Entra External ID / Azure Blob Storage / Azure
# Communication Services are all gone) rather than leaving the 59a069a/a78cc18 pattern set on
# autopilot:
#   REMOVED as genuinely dead weight, not just "unlikely" — verified by grepping the whole repo
#   (src/, deploy/, csproj files) and confirming zero remaining references, not assumed from the
#   ADR alone:
#     - "Azure Storage / Communication Services key" (AccountKey=/accesskey= — Azure.Storage.Blobs
#       and Azure.Communication.Email are both fully removed package references; LocalDiskBlobStorage
#       and the email sender replaced them. No code path in this repository can ever produce this
#       shape again.)
#     - "Entra/Graph client secret value" and "explicit client secret assignment" (client_secret=...)
#       — removed when Microsoft.Identity.Web/Microsoft.Graph went. RE-ADDED 2026-10-12 in new form
#       (see the pattern list): sending mail through Microsoft Graph brought back a client secret.
#   KEPT, confirmed still relevant: "AWS access key id" (AKIA...) — MORE relevant now than before
#   the pivot, since deploy/backup/pg-backup.sh genuinely uses the AWS CLI (`aws s3 cp`) against an
#   S3-compatible off-box backup target; "PEM private key block" — generically still plausible
#   (e.g. an accidentally-committed TLS/SSH key), not stack-specific either way; "SQL /
#   connection-string password" (password=/pwd=) — PostgreSQL connection strings still use this
#   exact shape (verified against src/Vlms.Web/appsettings.json's own
#   "Password=PLACEHOLDER" connection-string value).
#   ADDED, found missing by deliberately checking whether the existing patterns actually fire
#   against this stack's real config, not assuming they do:
#     - "JSON config-key password/pwd" — appsettings.json's own `"Smtp": { "Password": "PLACEHOLDER"
#       }` uses JSON colon syntax, which the pre-existing password=/pwd= pattern (anchored on a
#       literal `=`) never matched at all — confirmed by testing the exact pattern against the exact
#       file content before concluding this was a real hole, not assumed. A real hardcoded SMTP
#       password in appsettings.json would previously have scanned completely clean.
#     - "AWS secret access key" (AWS_SECRET_ACCESS_KEY=...) — the S3-compatible backup credential
#       pair pg-backup.sh documents; the pre-existing AKIA pattern only covers the access key ID
#       half of that pair, never the secret itself.
#     - "PostgreSQL CREATE ROLE ... PASSWORD" — deploy/postgres/00-create-roles-and-database.sql's
#       `CREATE ROLE ... WITH LOGIN PASSWORD 'value'` syntax (space + single-quote, not `=`) also
#       fell outside the pre-existing password pattern; confirmed by testing against the file's
#       real content.
#   File-type coverage widened for the same reason: the pre-pivot Include list (*.cs, *.json,
#   *.yaml, *.yml) predates deploy/ entirely and silently scanned none of it — deploy/*.sql,
#   deploy/backup/*.sh, deploy/systemd|backup/*.service|*.timer, and deploy/Caddyfile (no
#   extension) are now all included. Verified this doesn't introduce false positives by running the
#   widened scan against this repo's actual deploy/ files (all PLACEHOLDER-only, as documented in
#   each file's own header comment).
#
# False-positive discipline: this repo's own config uses the literal sentinel PLACEHOLDER for
# every not-yet-provisioned credential (appsettings.json Smtp/ConnectionStrings, deploy/postgres/
# role passwords — "PLACEHOLDER_CHANGE_ME_OWNER"/"PLACEHOLDER_CHANGE_ME_APP"). The shape patterns
# require a real-length/high-entropy value (e.g. a 30+ char base64 key) or a non-trivial quoted
# string, which PLACEHOLDER-prefixed sentinels still match on shape (deliberately — a real key
# pasted next to a placeholder-looking name must still be caught) but are then filtered out because
# the matched credential VALUE still contains the literal PLACEHOLDER sentinel. The filter is
# scoped to the matched value (not the whole line) and is case-sensitive against the uppercase
# sentinel — so a real key pasted onto the same physical line as an incidental mention of the word
# "placeholder" is still caught, while intentional placeholder config passes cleanly.
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

# --- Primary path: gitleaks, if installed ----------------------------------------------------
$gitleaks = (Get-Command gitleaks -ErrorAction SilentlyContinue).Source
if (-not $gitleaks -and $env:LOCALAPPDATA) {
    $wingetLink = Join-Path $env:LOCALAPPDATA "Microsoft\WinGet\Links\gitleaks.exe"
    if (Test-Path $wingetLink) { $gitleaks = $wingetLink }
}
if ($gitleaks) {
    & $gitleaks dir $repoRoot --redact --no-banner --log-level warn --exit-code 1
    if ($LASTEXITCODE -ne 0) {
        Write-Host "secret scan (gitleaks): FAILED - possible committed secret(s)" -ForegroundColor Red
        exit 1
    }
}

# --- Project credential-shape regex (supplement to gitleaks, or the whole scan without it) ----
# Each entry: a label (for evidence output) and the pattern. Patterns are matched per line;
# a match whose credential value contains the literal PLACEHOLDER sentinel is treated as
# intentional placeholder config and skipped. The SQL/connection-string password pattern matches
# both the unquoted connection-string form (Password=Sup3rSecret123;) and the quoted
# C#-assignment form (password = "Sup3rSecretHardcoded") — the opening quote is optional.
$patterns = @(
    @{ Label = "AWS access key id";                    Pattern = 'AKIA[0-9A-Z]{16}' }
    @{ Label = "AWS secret access key";                Pattern = '(?i)AWS_SECRET_ACCESS_KEY\s*=\s*[A-Za-z0-9/+]{30,}' }
    @{ Label = "PEM private key block";                Pattern = 'BEGIN (RSA |EC |DSA |OPENSSH )?PRIVATE KEY' }
    @{ Label = "SQL / connection-string password";     Pattern = '(?i)\b(password|pwd)\s*=\s*[''"]?[^;''"\s]{3,}' }
    @{ Label = "JSON config-key password/pwd";         Pattern = '(?i)"(password|pwd)"\s*:\s*"[^"]{3,}"' }
    @{ Label = "PostgreSQL CREATE/ALTER ROLE password"; Pattern = "(?i)\bpassword\s+'[^']{3,}'" }
    # Re-added 2026-10-12 (STATE.md "Send email via Microsoft Graph"): the app now has an Entra client
    # secret (GraphMail:ClientSecret). Value shape from gitleaks' own azure-ad-client-secret rule
    # (config/gitleaks.toml), boundaries dropped; plus the config-key form, so a secret pasted into
    # appsettings or an env file is caught whatever its shape.
    @{ Label = "Entra client secret value";            Pattern = '[a-zA-Z0-9_~.]{3}\dQ~[a-zA-Z0-9_~.-]{31,34}' }
    @{ Label = "client secret config key";             Pattern = '(?i)"?client_?secret"?\s*[:=]\s*"?[^"\s;]{8,}' }
)

# *.sql/*.sh/*.service/*.timer/Caddyfile — added in the 2026-07-21 currency pass: deploy/ (the
# ADR-0005 deployment-scripting item) predates the original Include list and was previously scanned
# not at all.
$scanFiles = Get-ChildItem -Path $repoRoot -Recurse -Include *.cs, *.json, *.yaml, *.yml, *.sql, *.sh, *.service, *.timer, Caddyfile -ErrorAction SilentlyContinue |
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
    Write-Host "secret scan (project patterns): FAILED — possible committed secret(s)" -ForegroundColor Red
    foreach ($h in $hits) {
        # Report location and matched credential type only — never echo the value itself.
        Write-Host (" - {0}:{1}  [{2}]" -f $h.Path, $h.Line, $h.Label) -ForegroundColor Red
    }
    Write-Error "possible secret"
    exit 1
}

$patternSummary = "$($scanFiles.Count) files, $($patterns.Count) credential-shape patterns"
if ($gitleaks) {
    Write-Host "secret scan (gitleaks): clean; project patterns: clean ($patternSummary)"
} else {
    Write-Host "secret scan (regex fallback — install gitleaks for a real scan): clean ($patternSummary)"
}
exit 0
