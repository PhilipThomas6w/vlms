param([switch]$Fast)
$ErrorActionPreference = "Stop"
function Stage($n,$b){ Write-Host "== $n ==" ; & $b ; if($LASTEXITCODE -ne 0){ Write-Error "$n failed"; exit 1 } }
Stage "build"  { dotnet build -warnaserror }
Stage "test"   { dotnet test --nologo }
Stage "secrets"{ & "$PSScriptRoot/check-secrets.ps1" }
if(-not $Fast){
  Stage "access-control (ASVS 5.0 V8)" { & "$PSScriptRoot/check-access-control.ps1" }
  Stage "accessibility (WCAG 2.2 AA)"  { & "$PSScriptRoot/check-accessibility.ps1" }
}
Write-Host "verify OK"
