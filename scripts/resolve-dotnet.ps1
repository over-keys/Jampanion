[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$RepositoryRoot
)

# The project bundle is kept next to the repository so a clean checkout does
# not depend on the machine-wide PATH. Prefer the readable `.dotnet` name, but
# retain `.dotnet-sdk` as a compatibility fallback for existing workspaces.
$repositoryPath = (Resolve-Path -LiteralPath $RepositoryRoot).Path
$workspaceRoot = Split-Path -Parent $repositoryPath
$candidates = [System.Collections.Generic.List[string]]::new()

if ($env:JAMPANION_DOTNET) {
    $candidates.Add($env:JAMPANION_DOTNET)
}
$candidates.Add((Join-Path $workspaceRoot '.dotnet\dotnet.exe'))
$candidates.Add((Join-Path $workspaceRoot '.dotnet-sdk\dotnet.exe'))

$pathDotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if ($pathDotnet) {
    $candidates.Add($pathDotnet.Source)
}

foreach ($candidate in $candidates | Select-Object -Unique) {
    if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) {
        continue
    }

    # A runtime-only dotnet.exe cannot restore or build. Select only a host
    # that reports at least one installed SDK.
    $sdkList = & $candidate --list-sdks 2>$null
    if ($LASTEXITCODE -eq 0 -and @($sdkList).Count -gt 0) {
        Write-Output (Resolve-Path -LiteralPath $candidate).Path
        exit 0
    }
}

throw "No .NET SDK found. Put the bundled SDK in '$workspaceRoot\.dotnet', set JAMPANION_DOTNET, or install .NET SDK 10."
