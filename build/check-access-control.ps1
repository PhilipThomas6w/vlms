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
# Scans all of Components/, not just Components/Pages — the [Authorize] guarantee is about
# @page-annotated files (the actual routing surface), not about which subdirectory convention
# happens to hold them today.
$componentsRoot = Join-Path $repoRoot "src/Vlms.Web/Components"
# Home.razor: a shell page with no sensitive content of its own — every link on it is individually
# gated by <AuthorizeView Policy="..."> per role, not a single page-level policy (see the page).
# Error.razor / NotFound.razor: framework error pages, nothing sensitive to protect.
# Login.razor / Register.razor / LoginWith2fa.razor / LoginWithRecoveryCode.razor / AcceptInvite.razor
# (STATE.md "Identity" item): every sign-in/account-creation entry point is necessarily reachable by
# a caller who is not yet authenticated — an [Authorize] attribute here would make it impossible to
# ever sign in at all. Each renders/accepts no sensitive data of its own (Login/Register/2FA pages
# only take credentials the caller already knows; AcceptInvite validates a signed, single-use,
# expiring token before doing anything — see GuardianInviteService's doc comment). Manage/*.razor
# pages (EnableAuthenticator/TwoFactorAuthentication) are NOT on this list — they carry a plain
# [Authorize] (any signed-in user), which the check below already accepts.
# ForgotPassword.razor / ResetPassword.razor / ConfirmEmail.razor / ResendEmailConfirmation.razor
# (STATE.md "Password reset + Identity hardening"): reached by someone who can't sign in yet. Each
# answers identically for unknown emails, and the reset/confirm pages act only on a single-use,
# expiring code that was emailed to the account's confirmed address (AccountEmailService).
$allowListedPages = @(
    "Home.razor", "Error.razor", "NotFound.razor",
    "Login.razor", "Register.razor", "LoginWith2fa.razor", "LoginWithRecoveryCode.razor", "AcceptInvite.razor",
    "ForgotPassword.razor", "ResetPassword.razor", "ConfirmEmail.razor", "ResendEmailConfirmation.razor",
    "Privacy.razor"  # public privacy notice (UK GDPR Art 13), static text, no data
)
$pageFiles = Get-ChildItem -Path $componentsRoot -Recurse -Filter "*.razor"

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

# --- 2b. Raw SQL only where docs/adr/0010-chapters.md sanctions it -------------------------------
# Raw SQL is the other way past a query filter. Three reads are sanctioned, each returning no
# person-level row: the whole-table id allocation (VlmsDbContext.NextIdAsync), the national
# administrator's anonymous counts (NationalTotalsService) and an invite's chapter id after its
# token is verified (GuardianInviteService). Migrations are DDL and are not scanned.
$rawSqlAllowList = @(
    "src/Vlms.Infrastructure/VlmsDbContext.cs",
    "src/Vlms.Infrastructure/Chapters/NationalTotalsService.cs",
    "src/Vlms.Infrastructure/Guardianship/GuardianInviteService.cs"
)
$sourceFiles = Get-ChildItem -Path $srcRoot -Recurse -Include "*.cs", "*.razor" |
    Where-Object { $_.FullName -notmatch '[\\/](bin|obj|Migrations)[\\/]' }
foreach ($file in $sourceFiles) {
    $relative = (RelativePath $file.FullName) -replace '\\', '/'
    $content = Get-Content -Raw -LiteralPath $file.FullName
    if ($content -match '\.(SqlQuery|SqlQueryRaw|FromSql[A-Za-z]*|ExecuteSql[A-Za-z]*)\s*[<(]' -and $rawSqlAllowList -notcontains $relative) {
        $failures += "raw SQL outside the sanctioned files (adr/0010) — it bypasses the chapter and sensitive-data query filters: $relative"
    }
    # The chapter scope override (invite acceptance) may be entered by GuardianInviteService only.
    if ($content -match '\.EnterForVerifiedInvite\s*\(' -and $relative -ne "src/Vlms.Infrastructure/Guardianship/GuardianInviteService.cs") {
        $failures += "ChapterScopeOverride.EnterForVerifiedInvite called outside GuardianInviteService (adr/0010): $relative"
    }
}

# --- 3. Access-control test suites must still exist with at least their original test count -----
# (test-plan.md TC-007/011 -> SensitiveDataAccessControlTests; TC-008 -> Consent/DbsCheck service
# denial tests; TC-012 -> GuardianLinkServiceTests). Counts are the number present when this stage
# was written (2026-07-19) — a floor, not a ceiling; adding more tests never fails this check.
$testRoot = Join-Path $repoRoot "tests/Vlms.Tests/Infrastructure"
$expectedMinimums = @{
    # Raised 22/10/2026 for adr/0006 (FR-015): the Teacher/linked-Parent access matrix on
    # ConsentSensitiveDetails, rejection-needs-comments and approval-updates-Student. The parent
    # consent form's own suite is new, at its count when written.
    "SensitiveDataAccessControlTests.cs" = 19
    "ConsentRecordServiceTests.cs"       = 16
    "ParentConsentServiceTests.cs"       = 17
    "DbsCheckServiceTests.cs"            = 8
    "GuardianLinkServiceTests.cs"        = 13
    # STATE.md "Admin role assignment + first-Admin bootstrap" (2026-10-11): role grant/revoke and
    # the first-Admin bootstrap are access-control surfaces in their own right.
    "RoleAssignmentServiceTests.cs"      = 19
    "FirstAdminBootstrapServiceTests.cs" = 6
    # STATE.md "Password reset + Identity hardening" (2026-10-12): reset/confirmation are the
    # unauthenticated route back into an account.
    "AccountEmailServiceTests.cs"        = 9
    # FR-013 student administration: assign teacher / change status are Admin-only.
    "StudentAdministrationServiceTests.cs" = 13
    # FR-010 certificate view/download: owner, linked-parent and staff access, everything else refused.
    "CertificateAccessServiceTests.cs"  = 8
    # FR-011 student sign-ins (adr/0007): who may create or reset a sign-in, the confirmation rule,
    # and the student page's own-record scoping. Raised RoleAssignmentServiceTests for its two new guards.
    # Raised 22/10/2026 for the sign-in rules (adr/0007 decisions 9-13): password rules, lockout
    # clearing, disable/restore, persistent student cookie, parent of several children.
    "StudentAccountServiceTests.cs"     = 42
    "StudentPageServiceTests.cs"        = 4
    # FR-016 role changes take effect immediately: multi-role unions, mid-scope grant/revoke/MFA
    # change, security-stamp rotation.
    "RoleChangeTests.cs"                = 13
    "StudentApplicationServiceTests.cs" = 38
    # FR-021 chapters (adr/0010): the reflection classification, every entity per chapter, the
    # write rules, roles per chapter, national administrator least privilege, notifications.
    "ChapterSeparationTests.cs"         = 27
    # FR-020 attendance register: who takes and reads it, this chapter's active students only,
    # the consent alert's recipients and content.
    # Raised 24/10/2026 for FR-022 (one register per class per meeting day, opened from the event).
    "AttendanceServiceTests.cs"         = 15
    # FR-022 calendar: who reads it, Admin-only events kept to their chapter, class teachers; the
    # private subscribe link served without signing in.
    "CalendarServiceTests.cs"           = 10
    "CalendarFeedServiceTests.cs"       = 6
    # adr/0012 app-hosted lessons: the Teacher's Information for staff only, per role; lesson and
    # Teacher's Information pictures by id; the converted lesson's offline details.
    "LessonPageServiceTests.cs"         = 10
    # adr/0013 lesson suggestions: who may suggest, review, publish now, read history and change the
    # setting; one open suggestion; self-approval; the stale-base guard; pictures to the author only.
    "LessonSuggestionServiceTests.cs"   = 25
    # adr/0014 retention purge: Admin-only, active chapter, still-due recheck, typed name, what a
    # deletion removes, the parent and staff account rules, the deletion log; the reminder and the
    # audit-log purge (system principal only, rows older than 6 years only). Raised 27/10/2026 for
    # adr/0014 settled after build: an account named only as the one who acted is deleted and the
    # reference cleared; every foreign key to an account is accounted for; the other keep-rules hold.
    "RetentionDeletionServiceTests.cs"  = 34
    "RetentionJobsTests.cs"             = 7
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

# Request-level end-to-end suites (real Vlms.Web pipeline via WebApplicationFactory): anonymous
# redirects, sign-in, the per-role page matrix and the lesson-file endpoint. Same floor semantics.
$endToEndRoot = Join-Path $repoRoot "tests/Vlms.Tests/EndToEnd"
$endToEndMinimums = @{
    "AccessEndToEndTests.cs"     = 8
    "RoleMatrixEndToEndTests.cs" = 5
    "ParentRegistrationEndToEndTests.cs" = 3
}
foreach ($testFileName in $endToEndMinimums.Keys) {
    $testFilePath = Join-Path $endToEndRoot $testFileName
    if (-not (Test-Path -LiteralPath $testFilePath)) {
        $failures += "end-to-end test file missing entirely: tests/Vlms.Tests/EndToEnd/$testFileName"
        continue
    }
    $testContent = Get-Content -Raw -LiteralPath $testFilePath
    $actualCount = ([regex]::Matches($testContent, '\[Fact\]|\[Theory\]')).Count
    if ($actualCount -lt $endToEndMinimums[$testFileName]) {
        $failures += "tests/Vlms.Tests/EndToEnd/$testFileName has only $actualCount test method(s), expected at least $($endToEndMinimums[$testFileName]) — a test appears to have been removed"
    }
}

# --- 4. Manual-review checklist currency (content-hash gate) ------------------------------------
$hashedFiles = @()
$hashedFiles += Get-ChildItem (Join-Path $repoRoot "src/Vlms.Infrastructure/Authorization") -Filter "*.cs"
$hashedFiles += Get-Item (Join-Path $repoRoot "src/Vlms.Infrastructure/VlmsDbContext.cs")
$hashedFiles += Get-Item (Join-Path $repoRoot "src/Vlms.Infrastructure/Auditing/SensitiveDataAuditInterceptor.cs")
$hashedFiles += Get-Item (Join-Path $repoRoot "src/Vlms.Web/Program.cs")
$hashedFiles += Get-Item (Join-Path $repoRoot "src/Vlms.Infrastructure/Identity/RoleAssignmentService.cs")
$hashedFiles += Get-Item (Join-Path $repoRoot "src/Vlms.Infrastructure/Identity/FirstAdminBootstrapService.cs")
$hashedFiles += Get-Item (Join-Path $repoRoot "src/Vlms.Infrastructure/Identity/AccountEmailService.cs")
$hashedFiles += Get-Item (Join-Path $repoRoot "src/Vlms.Infrastructure/Identity/VlmsIdentityPolicy.cs")
# FR-011 student sign-ins (adr/0007).
$hashedFiles += Get-Item (Join-Path $repoRoot "src/Vlms.Infrastructure/Identity/StudentAccountService.cs")
$hashedFiles += Get-Item (Join-Path $repoRoot "src/Vlms.Infrastructure/Identity/VlmsUserConfirmation.cs")
$hashedFiles += Get-Item (Join-Path $repoRoot "src/Vlms.Infrastructure/Identity/StudentUsername.cs")
$hashedFiles += Get-Item (Join-Path $repoRoot "src/Vlms.Infrastructure/Identity/AppUserProvisioningService.cs")
$hashedFiles += Get-Item (Join-Path $repoRoot "src/Vlms.Infrastructure/Engagement/StudentPageService.cs")
# FR-016: the current-user context every role check and query filter reads, stamp rotation, and the
# circuit's security-stamp revalidation.
$hashedFiles += Get-Item (Join-Path $repoRoot "src/Vlms.Infrastructure/Security/IdentityCurrentUserContext.cs")
$hashedFiles += Get-Item (Join-Path $repoRoot "src/Vlms.Infrastructure/Identity/SecurityStamps.cs")
$hashedFiles += Get-Item (Join-Path $repoRoot "src/Vlms.Web/Components/Account/IdentityRevalidatingAuthenticationStateProvider.cs")
# adr/0007 decisions 9-13: student password rule, disable flag, persistent student cookie.
$hashedFiles += Get-Item (Join-Path $repoRoot "src/Vlms.Infrastructure/Identity/StudentSignInPolicy.cs")
$hashedFiles += Get-Item (Join-Path $repoRoot "src/Vlms.Infrastructure/Identity/VlmsIdentityUser.cs")
$hashedFiles += Get-Item (Join-Path $repoRoot "src/Vlms.Web/Components/Account/VlmsSignInManager.cs")
$hashedFiles += Get-Item (Join-Path $repoRoot "src/Vlms.Infrastructure/Registration/StudentApplicationService.cs")
# FR-021 chapters (adr/0010): filters' write side, membership, scope override, national services,
# the switch endpoint, the anonymous-lookup context and the invite acceptance that uses the override.
$hashedFiles += Get-ChildItem (Join-Path $repoRoot "src/Vlms.Infrastructure/Chapters") -Filter "*.cs"
$hashedFiles += Get-Item (Join-Path $repoRoot "src/Vlms.Infrastructure/Security/AccountLookupUserContext.cs")
$hashedFiles += Get-Item (Join-Path $repoRoot "src/Vlms.Infrastructure/Guardianship/GuardianInviteService.cs")
# FR-020 attendance register: role checks, chapter scope, the Safeguarding Officer alert.
$hashedFiles += Get-ChildItem (Join-Path $repoRoot "src/Vlms.Infrastructure/Attendance") -Filter "*.cs"
$hashedFiles += Get-Item (Join-Path $repoRoot "src/Vlms.Web/ChapterSwitchEndpoint.cs")
# FR-022 calendar: event and calendar role checks, the subscribe link and its anonymous endpoint.
$hashedFiles += Get-ChildItem (Join-Path $repoRoot "src/Vlms.Infrastructure/Calendar") -Filter "*.cs"
$hashedFiles += Get-Item (Join-Path $repoRoot "src/Vlms.Web/CalendarFeedEndpoint.cs")
$hashedFiles += Get-Item (Join-Path $repoRoot "src/Vlms.Web/HealthEndpoint.cs")
# FR-006/FR-024 lesson library and app-hosted lessons (adr/0012): the library role check, the
# staff-only Teacher's Information, and the file, picture and offline-page endpoints.
$hashedFiles += Get-Item (Join-Path $repoRoot "src/Vlms.Infrastructure/Curriculum/LessonLibraryService.cs")
$hashedFiles += Get-Item (Join-Path $repoRoot "src/Vlms.Web/LessonFileEndpoint.cs")
$hashedFiles += Get-Item (Join-Path $repoRoot "src/Vlms.Web/LessonPageEndpoint.cs")
# adr/0013 lesson suggestions: the service's role checks and the picture upload and picture endpoints.
$hashedFiles += Get-Item (Join-Path $repoRoot "src/Vlms.Infrastructure/Curriculum/LessonSuggestionService.cs")
$hashedFiles += Get-Item (Join-Path $repoRoot "src/Vlms.Web/LessonSuggestionEndpoints.cs")
# adr/0014 retention purge: the deletions, the reminder, the audit-log purge and the leaving service.
$hashedFiles += Get-ChildItem (Join-Path $repoRoot "src/Vlms.Infrastructure/Retention") -Filter "*.cs"
$hashedFiles += $pageFiles

if ($PrintHash) {
    $hash = Get-ChecklistSourceHash -Files $hashedFiles -RepoRoot $repoRoot
    Write-Host "Reviewed-hash: $hash"
    Write-Host "($($hashedFiles.Count) files hashed — paste the line above into docs/governance/asvs-access-control-checklist.md after reviewing it)"
    exit 0
}

$checklistPath = Join-Path $repoRoot "docs/governance/asvs-access-control-checklist.md"
$checklistResult = Test-ChecklistCurrency -ChecklistPath $checklistPath -SourceFiles $hashedFiles -RepoRoot $repoRoot
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
