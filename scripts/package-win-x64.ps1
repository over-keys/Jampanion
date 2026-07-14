$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
& (Join-Path $PSScriptRoot 'publish-win-x64.ps1')
if ($LASTEXITCODE -ne 0) {
    throw "Windows publish script failed with exit code $LASTEXITCODE."
}

$publish = Join-Path $root 'artifacts\Jampanion-win-x64'
$packageDirectory = Join-Path $root 'artifacts\package'
$zip = Join-Path $packageDirectory 'Jampanion-Windows-x64.zip'

New-Item $packageDirectory -ItemType Directory -Force | Out-Null
if (Test-Path $zip) {
    Remove-Item $zip -Force
}

Compress-Archive -Path (Join-Path $publish '*') -DestinationPath $zip -CompressionLevel Optimal
if (-not (Test-Path -LiteralPath $zip -PathType Leaf)) {
    throw "Package archive was not created: $zip"
}
Write-Host "Package created: $zip"
