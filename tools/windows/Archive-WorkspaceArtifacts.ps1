[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Workspace,
    [datetime]$Before = (Get-Date).Date,
    [switch]$Apply,
    [string]$Manifest
)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Resolve-Path -LiteralPath $Workspace).Path).TrimEnd('\', '/')
if (-not (Test-Path -LiteralPath (Join-Path $root '.git'))) { throw 'Expected a Git workspace root.' }
$prefix = $root + [IO.Path]::DirectorySeparatorChar
$archive = Join-Path $root ('artifacts\workspace-archive\' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
if (-not $Manifest) { $Manifest = Join-Path $archive 'manifest.json' }
$Manifest = [IO.Path]::GetFullPath($Manifest)
if (-not $Manifest.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Manifest must stay inside the workspace.' }

# Read-only process/task inventory. Never archive a directory containing a running executable, task action or managed worktree.
$protected = @(& git -C $root worktree list --porcelain | Where-Object { $_ -like 'worktree *' } | ForEach-Object { $_.Substring(9) })
if ($LASTEXITCODE -ne 0) { throw 'Could not inspect worktrees.' }
$processes = @(Get-CimInstance Win32_Process)
$protected += @($processes | Where-Object ExecutablePath | ForEach-Object ExecutablePath)
$actions = @(Get-ScheduledTask | ForEach-Object { $_.Actions })
$protected += @($actions | Where-Object Execute | ForEach-Object Execute)
$launchText = (@($processes | ForEach-Object CommandLine) + @($actions | ForEach-Object Arguments)) -join "`n"
# The currently deployed application/configuration can reference an old artifact directory even without an executable there.
$settings = @(Get-ChildItem -LiteralPath (Join-Path $root 'src\UnifiedRgb.Host\bin') -Filter 'appsettings.json' -File -Recurse -ErrorAction SilentlyContinue)
$configText = ($settings | ForEach-Object { [IO.File]::ReadAllText($_.FullName) }) -join "`n"

$entries = @()
foreach ($item in Get-ChildItem -LiteralPath $root -Force) {
    $isBuild = $item.PSIsContainer -and $item.Name -like '.artifacts-*'
    $isLog = -not $item.PSIsContainer -and $item.Name -match '^(opus-|fable-|bringup-codec-first)' -and $item.Extension -in '.json', '.err', '.log', '.stderr', '.txt'
    if (-not ($isBuild -or $isLog) -or $item.LastWriteTime -ge $Before -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) { continue }
    $source = [IO.Path]::GetFullPath($item.FullName)
    if (-not $source.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetDirectoryName($source) -ne $root) { throw 'Candidate escaped the workspace root.' }
    & git -C $root check-ignore --quiet -- $item.Name
    if ($LASTEXITCODE -ne 0) { continue } # Published files and source are never maintenance candidates.
    $candidatePrefix = $source + [IO.Path]::DirectorySeparatorChar
    if (@($protected | Where-Object { $_ -eq $source -or $_.StartsWith($candidatePrefix, [StringComparison]::OrdinalIgnoreCase) }).Count -gt 0) { continue }
    if ($launchText.IndexOf($source, [StringComparison]::OrdinalIgnoreCase) -ge 0) { continue }
    if ($configText.IndexOf($source, [StringComparison]::OrdinalIgnoreCase) -ge 0 -or $configText.IndexOf($source.Replace('\', '\\'), [StringComparison]::OrdinalIgnoreCase) -ge 0) { continue }
    if ($isBuild -and (Test-Path -LiteralPath (Join-Path $source '.git'))) { continue }
    $target = [IO.Path]::GetFullPath((Join-Path (Join-Path $archive $(if ($isBuild) { 'builds' } else { 'logs' })) $item.Name))
    if (-not $target.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Archive target escaped the workspace.' }
    $entries += [pscustomobject]@{ Source = $source; Target = $target; Kind = $(if ($isBuild) { 'build' } else { 'log' }); State = 'Planned'; Sha256 = $(if ($isLog) { (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash } else { $null }) }
}
$receipt = [pscustomobject]@{ Schema = 1; Workspace = $root; CreatedUtc = [datetime]::UtcNow.ToString('o'); Before = $Before.ToString('o'); Applied = [bool]$Apply; Entries = $entries }
if ($Apply) {
    New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($Manifest)) -Force | Out-Null
    $receipt | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $Manifest -Encoding utf8
    foreach ($entry in $entries) {
        if (Test-Path -LiteralPath $entry.Target) { throw "Archive target already exists: $($entry.Target)" }
        New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($entry.Target)) -Force | Out-Null
        Move-Item -LiteralPath $entry.Source -Destination $entry.Target
        $entry.State = 'Moved'
        if ($entry.Sha256 -and (Get-FileHash -LiteralPath $entry.Target -Algorithm SHA256).Hash -ne $entry.Sha256) { throw 'Archived log hash changed.' }
        $receipt | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $Manifest -Encoding utf8
    }
}
[pscustomobject]@{ Applied = [bool]$Apply; Builds = @($entries | Where-Object Kind -eq build).Count; Logs = @($entries | Where-Object Kind -eq log).Count; Manifest = $(if ($Apply) { $Manifest } else { $null }); Archive = $archive }
