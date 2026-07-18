$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$packageDirectory = Join-Path $root 'artifacts\package'
$plist = Join-Path $PSScriptRoot 'macos\Info.plist'
$icon = Join-Path $root 'src\Jampanion\Assets\Jampanion.icns'

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
New-Item -Path $packageDirectory -ItemType Directory -Force | Out-Null

function Set-UnixZipCreator {
    param([Parameter(Mandatory)][string]$Path)

    # .NET's ZipArchive writes the Unix permission bits but labels the archive
    # as DOS-created (version-made-by 0x0014). Archive Utility can then ignore
    # those bits and extract the Mach-O apphost without its executable mode,
    # which macOS reports as a damaged application. Mark every central-directory
    # entry as Unix-created (0x0314) while retaining the permission bits.
    $bytes = [System.IO.File]::ReadAllBytes($Path)
    $eocd = -1
    for ($i = $bytes.Length - 22; $i -ge [Math]::Max(0, $bytes.Length - 65557); $i--) {
        if ($bytes[$i] -eq 0x50 -and $bytes[$i + 1] -eq 0x4b -and
            $bytes[$i + 2] -eq 0x05 -and $bytes[$i + 3] -eq 0x06) {
            $eocd = $i
            break
        }
    }
    if ($eocd -lt 0) { throw "ZIP end-of-central-directory record not found: $Path" }

    $centralDirectorySize = [BitConverter]::ToUInt32($bytes, $eocd + 12)
    $centralDirectoryOffset = [BitConverter]::ToUInt32($bytes, $eocd + 16)
    $end = [int]$centralDirectoryOffset + [int]$centralDirectorySize
    $entryCount = 0
    for ($i = [int]$centralDirectoryOffset; $i -lt $end;) {
        if ($bytes[$i] -ne 0x50 -or $bytes[$i + 1] -ne 0x4b -or
            $bytes[$i + 2] -ne 0x01 -or $bytes[$i + 3] -ne 0x02) {
            throw "Invalid ZIP central-directory entry at offset ${i}: $Path"
        }
        # Version made by is a two-byte field at offset 4 of each entry.
        $bytes[$i + 4] = 0x14
        $bytes[$i + 5] = 0x03
        $nameLength = [BitConverter]::ToUInt16($bytes, $i + 28)
        $extraLength = [BitConverter]::ToUInt16($bytes, $i + 30)
        $commentLength = [BitConverter]::ToUInt16($bytes, $i + 32)
        $i += 46 + $nameLength + $extraLength + $commentLength
        $entryCount++
    }
    if ($i -ne $end) { throw "ZIP central-directory length mismatch: $Path" }
    [System.IO.File]::WriteAllBytes($Path, $bytes)
    Write-Host "Unix ZIP metadata updated ($entryCount entries): $Path"
}

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
    Copy-Item -LiteralPath $icon -Destination (Join-Path $contents 'Jampanion.icns')

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

    Set-UnixZipCreator -Path $zip

    if (-not (Test-Path -LiteralPath $zip -PathType Leaf)) {
        throw "macOS package was not created: $zip"
    }
    Write-Host "Package created: $zip"
}
