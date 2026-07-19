# Shared helper for the two full-only gate stages (build/check-access-control.ps1,
# build/check-accessibility.ps1). Dot-sourced, not run directly.
#
# The mechanism: a manual-review checklist (docs/.../*-checklist.md) is only a real gate if it can
# go stale and be caught. Each checklist records a SHA-256 hash, computed over a named set of
# source files, in a "Reviewed-hash: <64 hex chars>" line. This library recomputes that hash from
# the files currently on disk and compares it to what's recorded. A mismatch means the reviewed
# files changed since a human last worked through the checklist — the stage fails until someone
# re-reviews it and records the new hash (via -PrintHash on the calling script). A missing hash
# line, or a missing checklist file entirely, also fails — an unwritten checklist cannot pass by
# default, which is the whole point (see loop-harness:verify-gate-authoring: "a gate that always
# passes is not a gate").
#
# Portability: the hash is computed over each file's path RELATIVE to the repo root (not the
# absolute FullName) and over its content with line endings normalized to `n. Hashing FullName
# would bind the recorded hash to the exact directory the checklist was authored in (fails on any
# other clone/worktree at a different absolute path); hashing raw bytes would make the hash flip
# with core.autocrlf/editor line-ending differences even with zero real content change. Neither is
# a real content change, so neither should flip the hash.

function Get-ChecklistSourceHash {
    param(
        [Parameter(Mandatory)] [System.IO.FileInfo[]] $Files,
        [Parameter(Mandatory)] [string] $RepoRoot
    )

    $normalizedRoot = $RepoRoot.TrimEnd('\', '/')
    $sha256 = [System.Security.Cryptography.SHA256]::Create()
    try {
        $builder = New-Object System.Text.StringBuilder
        foreach ($file in ($Files | Sort-Object FullName)) {
            $relativePath = $file.FullName.Substring($normalizedRoot.Length + 1) -replace '\\', '/'
            $content = Get-Content -Raw -LiteralPath $file.FullName
            $normalizedContent = $content -replace "`r`n", "`n" -replace "`r", "`n"
            [void]$builder.Append($relativePath)
            [void]$builder.Append("`n")
            [void]$builder.Append($normalizedContent)
            [void]$builder.Append("`n")
        }
        $bytes = [System.Text.Encoding]::UTF8.GetBytes($builder.ToString())
        $hashBytes = $sha256.ComputeHash($bytes)
        return -join ($hashBytes | ForEach-Object { $_.ToString("x2") })
    } finally {
        $sha256.Dispose()
    }
}

function Test-ChecklistCurrency {
    param(
        [Parameter(Mandatory)] [string] $ChecklistPath,
        [Parameter(Mandatory)] [System.IO.FileInfo[]] $SourceFiles,
        [Parameter(Mandatory)] [string] $RepoRoot
    )

    if (-not (Test-Path -LiteralPath $ChecklistPath)) {
        return [pscustomobject]@{
            IsCurrent = $false
            Message   = "checklist missing: $ChecklistPath — a human must write it and record a Reviewed-hash line before this stage can pass"
        }
    }

    $content = Get-Content -Raw -LiteralPath $ChecklistPath
    if ($content -notmatch '(?m)^Reviewed-hash:\s*([0-9a-fA-F]{64})\s*$') {
        return [pscustomobject]@{
            IsCurrent = $false
            Message   = "checklist $ChecklistPath has no 'Reviewed-hash: <sha256>' line — re-run the calling script with -PrintHash, review the checklist, and record the printed hash"
        }
    }

    $recordedHash = $Matches[1].ToLowerInvariant()
    $currentHash = Get-ChecklistSourceHash -Files $SourceFiles -RepoRoot $RepoRoot

    if ($recordedHash -ne $currentHash) {
        return [pscustomobject]@{
            IsCurrent = $false
            Message   = "checklist $ChecklistPath is stale — recorded hash $recordedHash does not match the current source ($currentHash). The reviewed files changed since the last sign-off; re-review and update the Reviewed-hash line (run the calling script with -PrintHash for the new value)"
        }
    }

    return [pscustomobject]@{ IsCurrent = $true; Message = "" }
}
