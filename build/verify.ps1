param([switch]$Fast)
$ErrorActionPreference = "Stop"
function Stage($n,$b){ Write-Host "== $n ==" ; & $b ; if($LASTEXITCODE -ne 0){ Write-Error "$n failed"; exit 1 } }
Stage "build"  { dotnet build -warnaserror }
Stage "test"   { dotnet test --nologo }
Stage "secrets"{
  if (Get-Command gitleaks -ErrorAction SilentlyContinue) {
    gitleaks detect --no-git --redact --exit-code 1
  } else {
    $hits = Get-ChildItem -Recurse -Include *.cs,*.json,*.yaml,*.yml -ErrorAction SilentlyContinue |
      Where-Object { $_.FullName -notmatch '[\\/](\.git|node_modules|bin|obj)[\\/]' -and $_.FullName -notmatch '(?i)(test|fixture)' } |
      Select-String -Pattern 'AKIA|BEGIN (RSA |EC |DSA )?PRIVATE KEY|password\s*=\s*[''"][^''"\s]{3,}' -List
    if ($hits) { Write-Error "possible secret"; exit 1 } else { "clean (regex fallback — install gitleaks for a real scan)" }
  }
}
if(-not $Fast){
  Stage "access-control (ASVS 5.0 V8)" { & "$PSScriptRoot/check-access-control.ps1" }
  Stage "accessibility (WCAG 2.2 AA)"  { & "$PSScriptRoot/check-accessibility.ps1" }
}
Write-Host "verify OK"
