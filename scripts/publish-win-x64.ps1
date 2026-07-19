$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src\Jampanion\Jampanion.csproj'
$output = Join-Path $root 'artifacts\Jampanion-win-x64'
$dotnet = & (Join-Path $PSScriptRoot 'resolve-dotnet.ps1') -RepositoryRoot $root
Write-Host "Using .NET SDK: $dotnet"

if (Test-Path $output) {
    Remove-Item $output -Recurse -Force
}

New-Item $output -ItemType Directory -Force | Out-Null
$env:AVALONIA_TELEMETRY_OPTOUT = '1'

# Avalonia's optional build telemetry can fail when its per-user log
# directory is read-only; publishing does not need that task.
& $dotnet restore $project `
    -r win-x64 `
    -p:UsedAvaloniaProducts= `
    -p:RestoreIgnoreFailedSources=true
if ($LASTEXITCODE -ne 0) {
    throw "Windows restore failed with exit code $LASTEXITCODE."
}

& $dotnet publish $project `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:PublishReadyToRun=false `
    -p:UsedAvaloniaProducts= `
    -p:RestoreIgnoreFailedSources=true `
    --no-restore `
    -o $output
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

$unneededFiles = Get-ChildItem -LiteralPath $output -File |
    Where-Object { $_.Extension -in @('.pdb', '.dylib') }
foreach ($file in $unneededFiles) {
    Remove-Item -LiteralPath $file.FullName -Force
}

$executable = Join-Path $output 'Jampanion.exe'
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
    throw "Published executable was not created: $executable"
}

Write-Host "Published current Avalonia Windows build to: $output"
