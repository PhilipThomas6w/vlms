param([switch]$Fast)
$ErrorActionPreference = "Stop"

# One gate run at a time per checkout. Two overlapping runs (e.g. the Stop hook's and a manual one)
# race on the same obj/ outputs and fail with CS2012 "file in use". A named mutex makes a second run
# wait instead. If a run dies, Windows/.NET release its mutex as "abandoned", and the next run
# simply takes it over.
$repoKey = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData(
    [Text.Encoding]::UTF8.GetBytes((Resolve-Path "$PSScriptRoot/..").Path.ToLowerInvariant()))).Substring(0, 16)
$gateLock = [Threading.Mutex]::new($false, "Local\vlms-verify-$repoKey")
try {
    if (-not $gateLock.WaitOne(0)) {
        Write-Host "== waiting for another verify run in this checkout to finish =="
        if (-not $gateLock.WaitOne([TimeSpan]::FromMinutes(15))) { Write-Error "timed out after 15 minutes waiting for another verify run"; exit 1 }
    }
} catch [Threading.AbandonedMutexException] {
    Write-Host "== previous verify run ended without releasing its lock; continuing =="
}

try {
  function Stage($n,$b){ Write-Host "== $n ==" ; & $b ; if($LASTEXITCODE -ne 0){ Write-Error "$n failed"; exit 1 } }
  Stage "build"  { dotnet build -warnaserror }
  Stage "test"   { dotnet test --nologo }
  Stage "secrets"{ & "$PSScriptRoot/check-secrets.ps1" }
  if(-not $Fast){
    Stage "access-control (ASVS 5.0 V8)" { & "$PSScriptRoot/check-access-control.ps1" }
    Stage "accessibility (WCAG 2.2 AA)"  { & "$PSScriptRoot/check-accessibility.ps1" }
  }
  Write-Host "verify OK"
}
finally {
  # Runs on `exit` too, so a run inside a long-lived PowerShell session doesn't keep the lock.
  $gateLock.ReleaseMutex()
  $gateLock.Dispose()
}
