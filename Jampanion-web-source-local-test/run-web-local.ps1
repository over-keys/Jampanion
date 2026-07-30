param(
    [int]$Port = 5279,
    [switch]$SkipNpmInstall
)

$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
$WebProject = Join-Path $Root 'src/Jampanion.Web/Jampanion.Web.csproj'
$CoreProject = Join-Path $Root 'src/Jampanion.Core/Jampanion.Core.csproj'
$WebDirectory = Join-Path $Root 'src/Jampanion.Web'
$SoundFont = Join-Path $WebDirectory 'wwwroot/soundfonts/FluidR3_Jampanion.sf3'
$ExpectedSha256 = '2e4aa17f20743930c87ada7cc1fee2228ecd2bb0e2de75a83cd590c53bcd0d63'

function Require-Command([string]$Name, [string]$Message) {
    if (-not (Get-Command $Name -ErrorAction SilentlyContinue)) {
        throw $Message
    }
}

Require-Command 'dotnet' '.NET SDK 10 is required.'
Require-Command 'node' 'Node.js 20 or later is required.'
Require-Command 'npm' 'npm is required.'

if (-not (Test-Path -LiteralPath $CoreProject -PathType Leaf)) {
    throw 'Extract this source bundle into the root of the Jampanion repository; Jampanion.Core was not found.'
}
if (-not (Test-Path -LiteralPath $WebProject -PathType Leaf)) {
    throw 'Jampanion.Web.csproj was not found.'
}
if (-not (Test-Path -LiteralPath $SoundFont -PathType Leaf)) {
    throw 'The bundled SoundFont is missing.'
}

$ActualSha256 = (Get-FileHash -LiteralPath $SoundFont -Algorithm SHA256).Hash.ToLowerInvariant()
if ($ActualSha256 -ne $ExpectedSha256) {
    throw "SoundFont checksum mismatch: $ActualSha256"
}
Write-Host "Verified SoundFont: $ActualSha256"

Push-Location $WebDirectory
try {
    if (-not $SkipNpmInstall) {
        & npm install --no-audit --no-fund
        if ($LASTEXITCODE -ne 0) { throw 'npm install failed.' }
    }
    & npm run build
    if ($LASTEXITCODE -ne 0) { throw 'npm run build failed.' }
}
finally {
    Pop-Location
}

Write-Host "`nStarting Jampanion Web at http://localhost:$Port/`n"
& dotnet run --project $WebProject --urls "http://localhost:$Port"
if ($LASTEXITCODE -ne 0) { throw 'dotnet run failed.' }
