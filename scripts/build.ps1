$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$dotnet = & (Join-Path $PSScriptRoot 'resolve-dotnet.ps1') -RepositoryRoot $root
Write-Host "Using .NET SDK: $dotnet"

$env:AVALONIA_TELEMETRY_OPTOUT = '1'

& $dotnet restore (Join-Path $root 'Jampanion.sln') `
    -p:UsedAvaloniaProducts= `
    -p:RestoreIgnoreFailedSources=true
if ($LASTEXITCODE -ne 0) {
    throw "dotnet restore failed with exit code $LASTEXITCODE."
}

& $dotnet build (Join-Path $root 'Jampanion.sln') `
    -c Release `
    -p:UseSharedCompilation=false `
    -p:UsedAvaloniaProducts=
if ($LASTEXITCODE -ne 0) {
    throw "dotnet build failed with exit code $LASTEXITCODE."
}
