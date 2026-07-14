$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$bundledDotnet = Join-Path (Split-Path -Parent $root) '.dotnet-sdk\dotnet.exe'
$dotnet = if (Test-Path -LiteralPath $bundledDotnet) { $bundledDotnet } else { 'dotnet' }

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
