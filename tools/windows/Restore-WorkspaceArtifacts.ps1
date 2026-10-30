[CmdletBinding()]
param([Parameter(Mandatory)][string]$Manifest, [switch]$Apply)
$ErrorActionPreference = 'Stop'
$receipt = Get-Content -LiteralPath $Manifest -Raw | ConvertFrom-Json
if ($receipt.Schema -ne 1) { throw 'Unknown archive manifest.' }
$root = [IO.Path]::GetFullPath($receipt.Workspace).TrimEnd('\', '/')
$prefix = $root + [IO.Path]::DirectorySeparatorChar
$entries = @($receipt.Entries | Where-Object State -eq Moved)
foreach ($entry in $entries) {
    $source = [IO.Path]::GetFullPath($entry.Source); $target = [IO.Path]::GetFullPath($entry.Target)
    if (-not $source.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetDirectoryName($source) -ne $root -or -not $target.StartsWith((Join-Path $root 'artifacts\workspace-archive') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Manifest path escaped the archive/workspace.' }
    if ((Test-Path -LiteralPath $source) -or -not (Test-Path -LiteralPath $target)) { throw "Restore would overwrite a path or cannot find its archive: $source" }
    if ($entry.Sha256 -and (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne $entry.Sha256) { throw 'Archived log hash differs; restore refused.' }
}
if ($Apply) {
    foreach ($entry in $entries) {
        Move-Item -LiteralPath $entry.Target -Destination $entry.Source
        $entry.State = 'Restored'
        $receipt | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $Manifest -Encoding utf8
    }
}
[pscustomobject]@{ Applied = [bool]$Apply; Restorable = $entries.Count; Workspace = $root }
