[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$worker = Join-Path $repo 'src/UnifiedRgb.Firmware.Worker'
$manifest = Get-Content -LiteralPath (Join-Path $worker 'core-manifest.json') -Raw | ConvertFrom-Json
if ($manifest.Schema -ne 1 -or @($manifest.Sources.PSObject.Properties).Count -ne 8) {
    throw 'Unexpected retained firmware source manifest.'
}
foreach ($pin in $manifest.Sources.PSObject.Properties) {
    $path = [IO.Path]::GetFullPath((Join-Path $worker $pin.Name))
    if (-not $path.StartsWith($worker + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Firmware source pin escapes the worker project.'
    }
    if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $pin.Value) {
        throw "Retained firmware source changed: $($pin.Name)"
    }
}
$fake = Join-Path $repo 'tests/UnifiedRgb.Firmware.Checks/CoreFakes/FakeBoot.cs'
if ((Get-FileHash -LiteralPath $fake -Algorithm SHA256).Hash -ne $manifest.TestFakeSha256) {
    throw 'Retained transaction fake changed.'
}
Write-Output 'Verified eight retained transaction sources and their fake; no device operations.'
