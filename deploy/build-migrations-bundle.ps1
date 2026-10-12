# build-migrations-bundle.ps1 — builds the EF Core migrations bundle for the VPS, on a machine with
# the .NET SDK and dotnet-ef (`dotnet tool install --global dotnet-ef`). The server then applies it
# with deploy/migrate.sh and needs neither the SDK nor this repository (deploy/README.md §3).
#
# Self-contained linux-x64 per Microsoft's bundle guidance
# (learn.microsoft.com/ef/core/managing-schemas/migrations/applying#bundles). The startup project is
# Vlms.Infrastructure: VlmsDbContextFactory (IDesignTimeDbContextFactory) builds the context without
# Vlms.Web's host, appsettings or secrets, and the real connection is passed at run time via
# --connection. ASPNETCORE_ENVIRONMENT=Production is set as the same guidance recommends, so nothing
# environment-dependent is picked up from a developer machine.
param(
    [string]$Output = (Join-Path $PSScriptRoot '..' 'artifacts' 'efbundle')
)

$ErrorActionPreference = 'Stop'
$repo = Resolve-Path (Join-Path $PSScriptRoot '..')
$env:ASPNETCORE_ENVIRONMENT = 'Production'

dotnet ef migrations bundle `
    --project (Join-Path $repo 'src' 'Vlms.Infrastructure') `
    --startup-project (Join-Path $repo 'src' 'Vlms.Infrastructure') `
    --self-contained `
    --target-runtime linux-x64 `
    --output $Output `
    --force
if ($LASTEXITCODE -ne 0) { throw "dotnet ef migrations bundle failed ($LASTEXITCODE)" }

Write-Host "Bundle: $Output — copy to /opt/vlms-migrations/efbundle on the VPS, then run deploy/migrate.sh"
