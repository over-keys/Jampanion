$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$packageDirectory = Join-Path $root 'artifacts\package'
$plist = Join-Path $PSScriptRoot 'macos\Info.plist'

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
New-Item -Path $packageDirectory -ItemType Directory -Force | Out-Null

foreach ($architecture in @('x64', 'arm64')) {
    $publish = Join-Path $root "artifacts\Jampanion-macOS-$architecture-publish"
    $bundleRoot = Join-Path $root "artifacts\Jampanion-macOS-$architecture\Jampanion.app"
    $bundleDirectory = Split-Path -Parent $bundleRoot
    $contents = Join-Path $bundleRoot 'Contents'
    $macos = Join-Path $contents 'MacOS'
    $zip = Join-Path $packageDirectory "Jampanion-macOS-$architecture.zip"

    if (-not (Test-Path -LiteralPath (Join-Path $publish 'Jampanion') -PathType Leaf)) {
        throw "macOS publish output is missing: $publish"
    }

    if (Test-Path -LiteralPath $bundleRoot) {
        Remove-Item -LiteralPath $bundleRoot -Recurse -Force
    }
    New-Item -Path $macos -ItemType Directory -Force | Out-Null
    Copy-Item -LiteralPath $plist -Destination (Join-Path $contents 'Info.plist')

    Get-ChildItem -LiteralPath $publish -File |
        Where-Object { $_.Extension -notin @('.pdb', '.xml') } |
        Copy-Item -Destination $macos

    if (Test-Path -LiteralPath $zip) {
        Remove-Item -LiteralPath $zip -Force
    }

    $archive = [System.IO.Compression.ZipFile]::Open($zip, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        $files = Get-ChildItem -LiteralPath (Join-Path $root "artifacts\Jampanion-macOS-$architecture") -Recurse -File
        foreach ($file in $files) {
            $relative = $file.FullName.Substring($bundleDirectory.Length + 1).Replace('\', '/')
            $entry = $archive.CreateEntry($relative, [System.IO.Compression.CompressionLevel]::Optimal)
            # Preserve the Unix executable bit in the ZIP. macOS Archive Utility
            # otherwise extracts the Mach-O file as non-executable and reports it
            # as damaged.
            $entry.ExternalAttributes = if ($file.Name -eq 'Jampanion') {
                [int]0x81ED0000 # regular file, mode 0755
            } else {
                [int]0x81A40000 # regular file, mode 0644
            }
            $input = [System.IO.File]::OpenRead($file.FullName)
            $output = $entry.Open()
            try {
                $input.CopyTo($output)
            } finally {
                $output.Dispose()
                $input.Dispose()
            }
        }
    } finally {
        $archive.Dispose()
    }

    if (-not (Test-Path -LiteralPath $zip -PathType Leaf)) {
        throw "macOS package was not created: $zip"
    }
    Write-Host "Package created: $zip"
}
