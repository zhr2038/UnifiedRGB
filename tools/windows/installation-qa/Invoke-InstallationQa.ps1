#Requires -Version 5.1
<#
.SYNOPSIS
  Offline QA for tools/installation/UnifiedRgb.Installation.psm1 (audit F6: install / upgrade / uninstall).

.DESCRIPTION
  Every case gets a fresh sandbox .artifacts\installation-qa\run-<id>\c<NN> (a fake "Program Files", a fake
  "ProgramData\UnifiedRgb", fake publish trees and an "outside" directory) and a FAKE platform: fake processes, a
  fake scheduled task, a fake HKCU Run value, fake maintenance verbs and a fake clock. Nothing here starts a real
  Host or App, touches the real task scheduler, registry, services, ProgramData or Program Files, needs elevation,
  or runs tools/install.ps1 / tools/uninstall.ps1 (those are only parsed). Sandboxes are kept as evidence.

.EXAMPLE
  powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools\windows\installation-qa\Invoke-InstallationQa.ps1
#>
[CmdletBinding()]
param(
    [string] $WorkRoot,
    [string[]] $Case
)
Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..'))
$artifacts = Join-Path $repo '.artifacts'
if (-not $WorkRoot) { $WorkRoot = Join-Path $artifacts 'installation-qa' }
$WorkRoot = [IO.Path]::GetFullPath($WorkRoot)
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
if (-not ($WorkRoot.StartsWith($artifacts + '\', [StringComparison]::OrdinalIgnoreCase) -or
        $WorkRoot.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase))) {
    throw "WorkRoot must be under '$artifacts' or the temporary directory; got '$WorkRoot'."
}
# Short directory names keep sandbox paths under the 260-character limit of Windows PowerShell 5.1 (.NET Framework).
$script:RunStarted = [DateTime]::UtcNow.ToString('o')
$script:RunRoot = Join-Path $WorkRoot ('run-' + [guid]::NewGuid().ToString('N').Substring(0, 6))
[void][IO.Directory]::CreateDirectory($script:RunRoot)

Import-Module (Join-Path $repo 'tools\installation\UnifiedRgb.Installation.psm1') -Force
$script:Utf8 = [Text.UTF8Encoding]::new($false)
$script:Cases = [Collections.Generic.List[object]]::new()
function Add-Case([string]$Name, [scriptblock]$Body) { $script:Cases.Add([pscustomobject]@{ Name = $Name; Body = $Body }) }

# ============================================================== fake platform

function New-FakePlatform {
    $runRoot = $script:RunRoot
    $S = @{
        Processes = [Collections.Generic.List[object]]::new()
        Task = $null                 # @{ Command; Arguments; ActionCount; State }
        Run = $null
        Calls = [Collections.Generic.List[string]]::new()
        ClockMs = [long]0
        NextPid = 4000
        StopBehavior = 'exit'        # exit: the task's Host exits at once; ignore: nothing exits; delayed: after StopDelayMs
        StopDelayMs = 0
        PendingExitAt = $null
        RestoreExit = 0
        UnregisterWorks = $true
        OnStop = $null; OnSleep = $null; OnEnsureData = $null
        OnRegister = $null           # param($S, $exe) -> exit code; default: point the task at $exe and return 0
        Observed = @{}
    }
    $exitTaskHosts = {
        param($S)
        foreach ($p in @($S.Processes.ToArray())) { if ($p.ByTask) { [void]$S.Processes.Remove($p) } }
        if ($null -ne $S.Task) { $S.Task.State = 'Ready' }
    }
    $insideRun = { param([string]$Path) $Path.StartsWith($runRoot + '\', [StringComparison]::OrdinalIgnoreCase) }.GetNewClosure()
    [pscustomobject]@{
        State = $S
        LockName = 'Local\UnifiedRgb.InstallationQa.' + [guid]::NewGuid().ToString('N')
        IsAdministrator = { $true }
        GetProcesses = { $S.Processes.ToArray() }.GetNewClosure()
        GetTask = {
            if ($null -eq $S.Task) { return $null }
            [pscustomobject]@{ Command = $S.Task.Command; Arguments = $S.Task.Arguments; ActionCount = $S.Task.ActionCount; State = $S.Task.State }
        }.GetNewClosure()
        StopTask = {
            $S.Calls.Add('StopTask')
            if ($null -eq $S.Task) { throw 'fake: there is no task to stop' }
            if ($S.StopBehavior -eq 'exit') { & $exitTaskHosts $S }
            elseif ($S.StopBehavior -eq 'delayed') { $S.PendingExitAt = $S.ClockMs + $S.StopDelayMs }
            if ($null -ne $S.OnStop) { & $S.OnStop $S }
        }.GetNewClosure()
        StartTask = {
            $S.Calls.Add('StartTask')
            if ($null -eq $S.Task) { throw 'fake: there is no task to start' }
            $running = @($S.Processes | Where-Object { $_.ByTask }).Count -gt 0
            if (-not $running -and [IO.File]::Exists($S.Task.Command)) {
                $S.NextPid++
                $S.Processes.Add([pscustomobject]@{ Id = $S.NextPid; Name = 'UnifiedRgb.Host'; Path = $S.Task.Command; ByTask = $true })
                $S.Task.State = 'Running'
            }
        }.GetNewClosure()
        UnregisterTask = {
            $S.Calls.Add('UnregisterTask')
            if (-not $S.UnregisterWorks) { throw 'fake: unregistering failed' }
            $S.Task = $null
        }.GetNewClosure()
        InvokeMaintenance = {
            param([string]$Executable, [string]$Verb)
            if (-not (& $insideRun $Executable)) { throw "fake: refusing to model '$Executable' outside the sandbox" }
            if (-not [IO.File]::Exists($Executable)) { throw "fake: '$Executable' does not exist" }
            if ($Verb -eq '--register-task') {
                $S.Calls.Add("register|$Executable")
                if ($null -ne $S.OnRegister) { return [int](& $S.OnRegister $S $Executable) }
                $S.Task = @{ Command = $Executable; Arguments = ''; ActionCount = 1; State = 'Ready' }
                return 0
            }
            if ($Verb -eq '--restore-services') { $S.Calls.Add("restore|$Executable"); return [int]$S.RestoreExit }
            throw "fake: unexpected maintenance verb '$Verb'"
        }.GetNewClosure()
        GetRunValue = { $S.Run }.GetNewClosure()
        SetRunValue = { param([string]$Value) $S.Calls.Add('SetRunValue'); $S.Run = $Value }.GetNewClosure()
        RemoveRunValue = { $S.Calls.Add('RemoveRunValue'); $S.Run = $null }.GetNewClosure()
        EnsureDataDirectory = {
            param([string]$Path)
            $S.Calls.Add('EnsureData')
            if (-not (& $insideRun $Path)) { throw "fake: refusing data directory '$Path' outside the sandbox" }
            if ($null -ne $S.OnEnsureData) { & $S.OnEnsureData $S $Path }
            if ([IO.Directory]::Exists($Path)) { return 'kept as it is (fake)' }
            [void][IO.Directory]::CreateDirectory($Path)
            return 'created (fake: no ACL change)'
        }.GetNewClosure()
        Sleep = {
            param([int]$Milliseconds)
            $S.ClockMs += $Milliseconds
            if ($null -ne $S.PendingExitAt -and $S.ClockMs -ge $S.PendingExitAt) { $S.PendingExitAt = $null; & $exitTaskHosts $S }
            if ($null -ne $S.OnSleep) { & $S.OnSleep $S }
        }.GetNewClosure()
        Now = { [DateTime]::new(2026, 1, 1, 0, 0, 0, [DateTimeKind]::Utc).AddMilliseconds($S.ClockMs) }.GetNewClosure()
    }
}

# ============================================================== sandbox helpers

function New-Sandbox([string]$Name) {
    $box = Join-Path $script:RunRoot $Name
    $pf = Join-Path $box 'ProgramFiles'
    [void][IO.Directory]::CreateDirectory($pf)
    [void][IO.Directory]::CreateDirectory((Join-Path $box 'outside'))
    [pscustomobject]@{
        Box = $box
        ProgramFiles = $pf
        InstallDir = Join-Path $pf 'UnifiedRgb'
        DataDir = Join-Path $box 'ProgramData\UnifiedRgb'
        Outside = Join-Path $box 'outside'
        Platform = (New-FakePlatform)
    }
}

function Write-QaFile([string]$Path, [string]$Text) {
    [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Path))
    [IO.File]::WriteAllText($Path, $Text, $script:Utf8)
}

function New-PublishTree($T, [string]$Label, [hashtable]$Extra = @{}, [string[]]$Omit = @(), [switch]$NoConfig) {
    $dir = Join-Path $T.Box "src-$Label"
    $files = [ordered]@{
        'UnifiedRgb.Host.exe' = "fake host $Label"
        'UnifiedRgb.App.exe' = "fake app $Label"
        'urgb.exe' = "fake cli $Label"
        'UnifiedRgb.Core.dll' = "fake core $Label"
        'appsettings.json' = "{`n  `"Hardware`": { `"UseVirtualAdapter`": true, `"EnableLianLi`": false },`n  `"Source`": `"$Label`"`n}`n"
        'runtimes\win-x64\native\placeholder.dll' = "fake native $Label"
        'Docs\ScenePackages\README.md' = 'docs (identical in every version)'
    }
    foreach ($k in @($Extra.Keys)) { $files[$k] = $Extra[$k] }
    if ($NoConfig) { $files.Remove('appsettings.json') }
    foreach ($o in $Omit) { $files.Remove($o) }
    foreach ($k in @($files.Keys)) { Write-QaFile (Join-Path $dir $k) $files[$k] }
    return $dir
}

function Add-UserData($T) {
    Write-QaFile (Join-Path $T.DataDir 'profiles\mine.json') '{ "name": "user profile" }'
    Write-QaFile (Join-Path $T.DataDir 'takeover.json') '{ "stopped": [] }'
    Write-QaFile (Join-Path $T.DataDir 'screens\scene.json') '{ "assignments": [] }'
}

function New-Junction([string]$Link, [string]$Target) {
    [void][IO.Directory]::CreateDirectory($Target)
    [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Link))
    $null = New-Item -ItemType Junction -Path $Link -Value $Target
    if (([IO.File]::GetAttributes($Link) -band [IO.FileAttributes]::ReparsePoint) -eq 0) { throw "could not create junction '$Link'" }
}

function Get-Sha([string]$Path) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($sha.ComputeHash([IO.File]::ReadAllBytes($Path))).Replace('-', '').ToLowerInvariant() }
    finally { $sha.Dispose() }
}

function Add-TreeLines([string]$Dir, [string]$Rel, $Lines) {
    # Ordinal order without generic [Array]::Sort inference (its PowerShell call-site binding depends on the first call).
    $entries = [Collections.Generic.List[object]]::new()
    foreach ($e in [IO.DirectoryInfo]::new($Dir).GetFileSystemInfos()) { $entries.Add($e) }
    $entries.Sort([Comparison[object]] { param($a, $b) [string]::CompareOrdinal($a.Name, $b.Name) })
    foreach ($e in $entries) {
        $r = if ($Rel) { "$Rel\$($e.Name)" } else { $e.Name }
        if (($e.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { $Lines.Add("L $r") }
        elseif (($e.Attributes -band [IO.FileAttributes]::Directory) -ne 0) { $Lines.Add("D $r"); Add-TreeLines $e.FullName $r $Lines }
        else { $Lines.Add("F $r $($e.Length) $(Get-Sha $e.FullName)") }
    }
}

# 'F name' / 'D name' / 'L name' lines without sizes and hashes.
function Get-TreeNames([string]$Path) {
    (@((Get-Tree $Path) -split "`n") | ForEach-Object { ($_ -split ' ')[0..1] -join ' ' }) -join "`n"
}

# A comparable listing of a directory tree (never follows junctions); '<absent>' when it does not exist.
function Get-Tree([string]$Path) {
    if (-not [IO.Directory]::Exists($Path)) { if ([IO.File]::Exists($Path)) { return "file $(Get-Sha $Path)" } return '<absent>' }
    $lines = [Collections.Generic.List[string]]::new()
    Add-TreeLines $Path '' $lines
    return ($lines -join "`n")
}

function Get-Manifest($T) { ConvertFrom-Json ([IO.File]::ReadAllText((Join-Path $T.InstallDir 'UnifiedRgb.install.json'), $script:Utf8)) }
function Get-VersionDir($T, [string]$Id) { Join-Path $T.InstallDir "versions\$Id" }
function Get-HostPath($T, [string]$Id) { Join-Path (Get-VersionDir $T $Id) 'UnifiedRgb.Host.exe' }
function Get-ManifestVersion($Manifest, [string]$Id) { @($Manifest.versions | Where-Object { $_.id -eq $Id })[0] }

function Install-Qa($T, [string]$Source, [hashtable]$More = @{}) {
    $a = @{ Source = $Source; InstallDir = $T.InstallDir; AllowedParent = $T.ProgramFiles; DataDir = $T.DataDir; Platform = $T.Platform; StopTimeoutSeconds = 3; Confirm = $false }
    foreach ($k in @($More.Keys)) { $a[$k] = $More[$k] }
    Invoke-UrgbInstall @a
}

function Uninstall-Qa($T, [hashtable]$More = @{}) {
    $a = @{ InstallDir = $T.InstallDir; AllowedParent = $T.ProgramFiles; DataDir = $T.DataDir; Platform = $T.Platform; StopTimeoutSeconds = 3; Confirm = $false }
    foreach ($k in @($More.Keys)) { $a[$k] = $More[$k] }
    Invoke-UrgbUninstall @a
}

function Add-Proc($T, [string]$Name, $Path, [bool]$ByTask = $false) {
    $S = $T.Platform.State
    $S.NextPid++
    $S.Processes.Add([pscustomobject]@{ Id = $S.NextPid; Name = $Name; Path = $Path; ByTask = $ByTask })
}

function Get-Procs($T, [string]$Name) { , @($T.Platform.State.Processes | Where-Object { $_.Name -eq $Name }) }
function Reset-Calls($T) { $T.Platform.State.Calls.Clear() }

# ============================================================== assertions

function Assert-True($Condition, [string]$Message) { if (-not $Condition) { throw "Assertion failed: $Message" } }

function Assert-Equal($Expected, $Actual, [string]$Message) {
    if ($Expected -is [string] -or $Actual -is [string]) { $same = [string]::Equals([string]$Expected, [string]$Actual, [StringComparison]::Ordinal) }
    else { $same = $Expected -eq $Actual }
    if (-not $same) { throw "Assertion failed: $Message`n  expected: $Expected`n  actual:   $Actual" }
}

function Assert-Calls($T, [string[]]$Expected, [string]$Message = 'platform calls') {
    $actual = @($T.Platform.State.Calls.ToArray())
    if (($actual -join "`n") -cne ($Expected -join "`n")) { throw "Assertion failed: $Message`n  expected: $($Expected -join ' ; ')`n  actual:   $($actual -join ' ; ')" }
}

# Runs Action and requires an error whose message starts with Prefix ('Refused:' or 'Failed:') and contains every Fragment.
function Assert-Error([scriptblock]$Action, [string]$Prefix, [string[]]$Fragment = @()) {
    $caught = $null
    try { $null = & $Action } catch { $caught = $_.Exception }
    if ($null -eq $caught) { throw "Assertion failed: expected a '$Prefix' error, but the call succeeded" }
    $m = $caught.Message
    if (-not $m.StartsWith($Prefix, [StringComparison]::Ordinal)) { throw "Assertion failed: expected a '$Prefix' error, got: $m" }
    foreach ($f in $Fragment) { if ($m.IndexOf($f, [StringComparison]::OrdinalIgnoreCase) -lt 0) { throw "Assertion failed: error lacks '$f': $m" } }
    return $m
}

function Assert-Contains($Collection, [string]$Fragment, [string]$Message) {
    foreach ($item in @($Collection)) { if ([string]$item -and ([string]$item).IndexOf($Fragment, [StringComparison]::OrdinalIgnoreCase) -ge 0) { return } }
    throw "Assertion failed: $Message (no entry contains '$Fragment'): $(@($Collection) -join ' | ')"
}

# Every listed file of the given versions (default: all) exists with the recorded length and SHA-256.
function Assert-ManifestMatchesDisk($T, [string[]]$Only) {
    $m = Get-Manifest $T
    foreach ($v in @($m.versions)) {
        if ($Only -and $Only -notcontains $v.id) { continue }
        foreach ($f in @($v.files)) {
            $p = Join-Path (Get-VersionDir $T $v.id) $f.path
            Assert-True ([IO.File]::Exists($p)) "listed file exists: $p"
            Assert-Equal $f.sha256 (Get-Sha $p) "hash of $p"
            Assert-Equal ([long]$f.length) ([IO.FileInfo]::new($p).Length) "length of $p"
        }
    }
}

# Installs v1 (started) and returns its result.
function Install-V1($T, [hashtable]$More = @{}) {
    $src = New-PublishTree $T 'v1'
    $r = Install-Qa $T $src $More
    Assert-Equal 'Installed' $r.Outcome 'v1 installed'
    return $r
}

# ============================================================== install / upgrade

Add-Case 'first-install-into-absent-directory' {
    param($T)
    $src = New-PublishTree $T 'v1'
    Assert-True (-not [IO.Directory]::Exists($T.DataDir)) 'precondition: no data directory'
    $r = Install-Qa $T $src
    Assert-Equal 'Installed' $r.Outcome 'outcome'
    Assert-True ($r.VersionId -cmatch '^v-[0-9]{8}T[0-9]{6}-[0-9a-f]{8}$') "version id format: $($r.VersionId)"
    $m = Get-Manifest $T
    Assert-Equal 1 $m.schema 'schema'
    Assert-Equal $T.InstallDir $m.installRoot 'manifest records the canonical root'
    Assert-Equal $r.VersionId $m.current 'current version'
    Assert-True ($null -eq $m.previous) 'no previous version'
    $raw = [IO.File]::ReadAllBytes((Join-Path $T.InstallDir 'UnifiedRgb.install.json'))
    Assert-True (-not ($raw.Length -ge 3 -and $raw[0] -eq 0xEF -and $raw[1] -eq 0xBB -and $raw[2] -eq 0xBF)) 'manifest has no BOM'
    $vdir = Get-VersionDir $T $r.VersionId
    Assert-Equal (Get-Tree $src) (Get-Tree $vdir) 'installed tree equals the source tree'
    Assert-Equal 'config' (@((Get-ManifestVersion $m $r.VersionId).files | Where-Object { $_.path -eq 'appsettings.json' })[0]).kind 'first install takes the source defaults'
    Assert-Equal "UnifiedRgb.install.json,versions" ((@([IO.Directory]::GetFileSystemEntries($T.InstallDir) | ForEach-Object { [IO.Path]::GetFileName($_) } | Sort-Object)) -join ',') 'only the manifest and versions\ at the root (no temporary files)'
    Assert-Equal $r.VersionId ((@([IO.Directory]::GetFileSystemEntries((Join-Path $T.InstallDir 'versions')) | ForEach-Object { [IO.Path]::GetFileName($_) })) -join ',') 'one version directory'
    Assert-ManifestMatchesDisk $T
    $newHost = Get-HostPath $T $r.VersionId
    Assert-Calls $T @('EnsureData', "register|$newHost", 'SetRunValue', 'StartTask')
    Assert-Equal $newHost $T.Platform.State.Task.Command 'task runs the installed Host'
    Assert-Equal "`"$(Join-Path $vdir 'UnifiedRgb.App.exe')`" --minimized" $T.Platform.State.Run 'tray entry'
    Assert-Equal 1 (Get-Procs $T 'UnifiedRgb.Host').Count 'Host started'
    Assert-True ([IO.Directory]::Exists($T.DataDir)) 'data directory created (fake)'
}

Add-Case 'first-install-into-empty-directory-nostart' {
    param($T)
    [void][IO.Directory]::CreateDirectory($T.InstallDir)
    Add-UserData $T
    $dataBefore = Get-Tree $T.DataDir
    $r = Install-Qa $T (New-PublishTree $T 'v1') @{ NoStart = $true }
    Assert-Equal 'Installed' $r.Outcome 'outcome'
    Assert-Calls $T @('EnsureData', "register|$(Get-HostPath $T $r.VersionId)", 'SetRunValue')
    Assert-Equal 0 (Get-Procs $T 'UnifiedRgb.Host').Count 'not started'
    Assert-Equal $dataBefore (Get-Tree $T.DataDir) 'existing data untouched'
    Assert-Contains $r.Log '-NoStart' 'log mentions -NoStart'
}

Add-Case 'upgrade-preserves-installed-appsettings-bytes' {
    param($T)
    Add-UserData $T
    $r1 = Install-V1 $T
    $v1dir = Get-VersionDir $T $r1.VersionId
    $v1Host = Get-HostPath $T $r1.VersionId
    # Owner-edited configuration: BOM, CRLF, non-ASCII text and fields this program version does not know.
    $text = "{`r`n  `"Hardware`": { `"UseVirtualAdapter`": false, `"EnableLianLi`": true, `"WireFormatVerified`": true },`r`n  `"FutureSection`": { `"Keep`": [1, 2, 3], `"Name`": `"$([char]0x5149)$([char]0x57DF)`" },`r`n  `"Trailing`": null`r`n}`r`n"
    $custom = [byte[]](@(0xEF, 0xBB, 0xBF) + [Text.UTF8Encoding]::new($false).GetBytes($text))
    [IO.File]::WriteAllBytes((Join-Path $v1dir 'appsettings.json'), $custom)
    $v1Before = Get-Tree $v1dir
    $dataBefore = Get-Tree $T.DataDir
    $versions = Join-Path $T.InstallDir 'versions'
    $T.Platform.State.OnStop = { param($S) $S.Observed.HostsAtStop = [IO.Directory]::GetFiles($versions, 'UnifiedRgb.Host.exe', 'AllDirectories').Length }.GetNewClosure()
    Reset-Calls $T
    $src2 = New-PublishTree $T 'v2' -Extra @{ 'NewInV2.dll' = 'new in v2' } -Omit @('urgb.exe')
    $r2 = Install-Qa $T $src2
    Assert-Equal 'Upgraded' $r2.Outcome 'outcome'
    Assert-Equal $r1.VersionId $r2.PreviousVersionId 'previous id'
    $v2dir = Get-VersionDir $T $r2.VersionId
    Assert-Equal ([Convert]::ToBase64String($custom)) ([Convert]::ToBase64String([IO.File]::ReadAllBytes((Join-Path $v2dir 'appsettings.json')))) 'appsettings.json carried over byte for byte'
    $m = Get-Manifest $T
    Assert-Equal $r2.VersionId $m.current 'current'
    Assert-Equal $r1.VersionId $m.previous 'previous kept for rollback'
    $entry = @((Get-ManifestVersion $m $r2.VersionId).files | Where-Object { $_.path -eq 'appsettings.json' })[0]
    Assert-Equal 'preserved-config' $entry.kind 'kind'
    Assert-Equal $v1Before (Get-Tree $v1dir) 'the running version was not modified'
    Assert-Equal $dataBefore (Get-Tree $T.DataDir) 'user data untouched'
    Assert-True ([IO.File]::Exists((Join-Path $v2dir 'NewInV2.dll'))) 'new file staged'
    Assert-True (-not [IO.File]::Exists((Join-Path $v2dir 'urgb.exe'))) 'file dropped by v2 is absent in v2'
    Assert-Equal (Get-Sha (Join-Path $src2 'UnifiedRgb.Host.exe')) (Get-Sha (Join-Path $v2dir 'UnifiedRgb.Host.exe')) 'new Host bytes'
    Assert-Equal 2 $T.Platform.State.Observed.HostsAtStop 'new version fully staged before the task was stopped'
    $v2Host = Get-HostPath $T $r2.VersionId
    Assert-Calls $T @('StopTask', 'EnsureData', "register|$v2Host", 'SetRunValue', 'StartTask')
    Assert-Equal $v2Host $T.Platform.State.Task.Command 'task switched'
    Assert-Equal 1 (Get-Procs $T 'UnifiedRgb.Host').Count 'one Host'
    Assert-Equal $v2Host (Get-Procs $T 'UnifiedRgb.Host')[0].Path 'new Host running'
    Assert-True ($T.Platform.State.Run -like "*$($r2.VersionId)\UnifiedRgb.App.exe*") 'tray entry moved to the new version'
    Assert-ManifestMatchesDisk $T @($r2.VersionId)   # v1's appsettings.json was edited by the owner after installation
    Assert-True ($v1Host -ne $v2Host) 'different directories'
}

Add-Case 'upgrade-without-installed-appsettings-keeps-it-absent' {
    param($T)
    $r1 = Install-Qa $T (New-PublishTree $T 'v1' -NoConfig)
    $r2 = Install-Qa $T (New-PublishTree $T 'v2')
    Assert-Equal 'Upgraded' $r2.Outcome 'outcome'
    Assert-True (-not [IO.File]::Exists((Join-Path (Get-VersionDir $T $r2.VersionId) 'appsettings.json'))) 'source defaults not applied on upgrade'
    Assert-Contains $r2.Warnings 'no appsettings.json' 'warning'
    Assert-True ($null -ne $r1) 'v1'
}

Add-Case 'further-upgrades-prune-only-unchanged-owned-files' {
    param($T)
    $r1 = Install-V1 $T
    $r2 = Install-Qa $T (New-PublishTree $T 'v2')
    $v1dir = Get-VersionDir $T $r1.VersionId
    Write-QaFile (Join-Path $v1dir 'user-notes.txt') 'not installed by UnifiedRgb'
    [IO.File]::AppendAllText((Join-Path $v1dir 'UnifiedRgb.Core.dll'), ' locally patched')
    [IO.File]::SetAttributes((Join-Path $v1dir 'urgb.exe'), [IO.FileAttributes]::ReadOnly)
    $v2Before = Get-Tree (Get-VersionDir $T $r2.VersionId)
    $r3 = Install-Qa $T (New-PublishTree $T 'v3')
    Assert-Equal 'Upgraded' $r3.Outcome 'v3'
    Assert-Equal "F UnifiedRgb.Core.dll`nF user-notes.txt" (Get-TreeNames $v1dir) 'v1 keeps only the modified and the unknown file; emptied sub-directories removed'
    Assert-True ([IO.File]::Exists((Join-Path $v1dir 'user-notes.txt'))) 'unknown file kept'
    Assert-True ([IO.File]::Exists((Join-Path $v1dir 'UnifiedRgb.Core.dll'))) 'modified owned file kept'
    Assert-True (-not [IO.File]::Exists((Join-Path $v1dir 'urgb.exe'))) 'unchanged read-only owned file removed'
    Assert-True (-not [IO.File]::Exists((Join-Path $v1dir 'UnifiedRgb.Host.exe'))) 'unchanged owned file removed'
    Assert-Equal $v2Before (Get-Tree (Get-VersionDir $T $r2.VersionId)) 'previous version kept intact'
    $m = Get-Manifest $T
    Assert-Equal 'UnifiedRgb.Core.dll' ((@((Get-ManifestVersion $m $r1.VersionId).files) | ForEach-Object { $_.path }) -join ',') 'manifest still lists the kept owned file'
    Assert-Contains $r3.Kept 'modified since installation' 'kept list'
    Assert-Contains $r3.Warnings 'not installed by UnifiedRgb' 'warning about the directory kept for an unknown file'
    $r4 = Install-Qa $T (New-PublishTree $T 'v4')
    Assert-True (-not [IO.Directory]::Exists((Get-VersionDir $T $r2.VersionId))) 'v2 (all unchanged, incl. carried-over config) fully removed'
    Assert-Equal (Get-Sha (Join-Path (Get-VersionDir $T $r3.VersionId) 'appsettings.json')) (Get-Sha (Join-Path (Get-VersionDir $T $r4.VersionId) 'appsettings.json')) 'config carried again'
    Assert-Equal (Get-Sha (Join-Path $T.Box 'src-v1\appsettings.json')) (Get-Sha (Join-Path (Get-VersionDir $T $r4.VersionId) 'appsettings.json')) 'still the first-install bytes'
    $m = Get-Manifest $T
    Assert-Equal "$($r1.VersionId),$($r3.VersionId),$($r4.VersionId)" ((@($m.versions) | ForEach-Object { $_.id }) -join ',') 'listed versions'
    Assert-ManifestMatchesDisk $T @($r3.VersionId, $r4.VersionId)
}

Add-Case 'refuses-running-gui-before-stopping-host' {
    param($T)
    Add-UserData $T
    $r1 = Install-V1 $T
    Add-Proc $T 'UnifiedRgb.App' (Join-Path (Get-VersionDir $T $r1.VersionId) 'UnifiedRgb.App.exe')
    $before = Get-Tree $T.Box
    Reset-Calls $T
    $src2 = New-PublishTree $T 'v2'
    $before = Get-Tree $T.Box
    Assert-Error { Install-Qa $T $src2 } 'Refused:' @('GUI', 'unsaved draft') | Out-Null
    Assert-Error { Uninstall-Qa $T } 'Refused:' @('GUI') | Out-Null
    $T.Platform.State.Processes.RemoveAt(@($T.Platform.State.Processes).Count - 1)
    Add-Proc $T 'UnifiedRgb.App' 'C:\Elsewhere\dev\UnifiedRgb.App.exe'
    Assert-Error { Install-Qa $T $src2 } 'Refused:' @('GUI') | Out-Null
    Assert-Calls $T @() 'no task, file or maintenance action'
    Assert-Equal $before (Get-Tree $T.Box) 'nothing changed'
    Assert-Equal 1 (Get-Procs $T 'UnifiedRgb.Host').Count 'Host still running'
}

Add-Case 'refuses-unowned-or-unprovable-processes' {
    param($T)
    $src = New-PublishTree $T 'v1'
    $before = Get-Tree $T.Box
    Add-Proc $T 'UnifiedRgb.Host' (Join-Path $T.Outside 'dev\UnifiedRgb.Host.exe')
    Assert-Error { Install-Qa $T $src } 'Refused:' @('does not own') | Out-Null
    $T.Platform.State.Processes.Clear()
    Add-Proc $T 'UnifiedRgb.Host' $null
    Assert-Error { Install-Qa $T $src } 'Refused:' @('cannot be read') | Out-Null
    $T.Platform.State.Processes.Clear()
    Add-Proc $T 'urgb' (Join-Path $T.Outside 'urgb.exe')
    Assert-Error { Install-Qa $T $src } 'Refused:' @('command-line') | Out-Null
    Assert-Calls $T @()
    Assert-Equal $before (Get-Tree $T.Box) 'nothing created'
    $T.Platform.State.Processes.Clear()
    $r1 = Install-V1 $T @{ NoStart = $true }
    Add-Proc $T 'helper' (Join-Path (Get-VersionDir $T $r1.VersionId) 'helper.exe')
    Reset-Calls $T
    $before = Get-Tree $T.Box
    Assert-Error { Install-Qa $T (New-PublishTree $T 'v2') } 'Refused:' @('installation directory') | Out-Null
    $before = Get-Tree $T.Box
    Assert-Error { Uninstall-Qa $T } 'Refused:' @('installation directory') | Out-Null
    Assert-Calls $T @()
    Assert-Equal $before (Get-Tree $T.Box) 'nothing changed'
}

Add-Case 'refuses-manually-running-owned-host-without-task' {
    param($T)
    $r1 = Install-V1 $T @{ NoStart = $true }
    $T.Platform.State.Task = $null
    Add-Proc $T 'UnifiedRgb.Host' (Get-HostPath $T $r1.VersionId)
    $src2 = New-PublishTree $T 'v2'
    $before = Get-Tree $T.Box
    Reset-Calls $T
    Assert-Error { Install-Qa $T $src2 } 'Refused:' @('without the installed scheduled task') | Out-Null
    Assert-Error { Uninstall-Qa $T } 'Refused:' @('without the installed scheduled task') | Out-Null
    Assert-Calls $T @()
    Assert-Equal $before (Get-Tree $T.Box) 'nothing changed'
    $T.Platform.State.Processes.Clear()
    $r2 = Install-Qa $T $src2
    Assert-Equal 'Upgraded' $r2.Outcome 'upgrade once nothing runs'
    Assert-Calls $T @('EnsureData', "register|$(Get-HostPath $T $r2.VersionId)", 'SetRunValue', 'StartTask') 'no stop needed when no task exists'
}

Add-Case 'install-stop-timeout-aborts-without-replacing-or-killing' {
    param($T)
    Add-UserData $T
    $r1 = Install-V1 $T
    $T.Platform.State.StopBehavior = 'ignore'
    $src2 = New-PublishTree $T 'v2'
    $before = Get-Tree $T.Box
    Reset-Calls $T
    Assert-Error { Install-Qa $T $src2 @{ StopTimeoutSeconds = 2 } } 'Failed:' @('did not exit within 2 s', 'nothing was killed', 'staged files removed') | Out-Null
    Assert-Calls $T @('StopTask') 'no registration, data, restore or restart after a timeout'
    Assert-Equal $before (Get-Tree $T.Box) 'installed files unchanged and staging removed'
    Assert-True ($T.Platform.State.ClockMs -ge 2000) 'waited the full timeout'
    Assert-Equal (Get-HostPath $T $r1.VersionId) (Get-Procs $T 'UnifiedRgb.Host')[0].Path 'Host left running, not killed'
    Assert-Equal (Get-HostPath $T $r1.VersionId) $T.Platform.State.Task.Command 'task unchanged'
}

Add-Case 'install-manual-owned-host-beside-task-times-out' {
    param($T)
    $r1 = Install-V1 $T
    Add-Proc $T 'UnifiedRgb.Host' (Get-HostPath $T $r1.VersionId) $false
    $src2 = New-PublishTree $T 'v2'
    $before = Get-Tree $T.Box
    Reset-Calls $T
    Assert-Error { Install-Qa $T $src2 } 'Failed:' @('did not exit') | Out-Null
    Assert-Calls $T @('StopTask')
    Assert-Equal $before (Get-Tree $T.Box) 'nothing replaced'
    Assert-Equal 1 (Get-Procs $T 'UnifiedRgb.Host').Count 'manually started Host still running (never killed)'
}

Add-Case 'install-waits-for-a-slow-exit' {
    param($T)
    $null = Install-V1 $T
    $T.Platform.State.StopBehavior = 'delayed'
    $T.Platform.State.StopDelayMs = 1500
    $r2 = Install-Qa $T (New-PublishTree $T 'v2')
    Assert-Equal 'Upgraded' $r2.Outcome 'outcome'
    Assert-True ($T.Platform.State.ClockMs -ge 1500 -and $T.Platform.State.ClockMs -lt 3000) "waited for the exit ($($T.Platform.State.ClockMs) ms)"
}

Add-Case 'install-aborts-when-gui-or-unowned-host-appears-during-stop' {
    param($T)
    $r1 = Install-V1 $T
    $v1Host = Get-HostPath $T $r1.VersionId
    $v1Before = Get-Tree (Get-VersionDir $T $r1.VersionId)
    $src2 = New-PublishTree $T 'v2'
    $S = $T.Platform.State
    # F6-1: the staged version is not deleted while something has appeared, and no start is requested around it.
    $S.OnStop = { param($S) $S.NextPid++; $S.Processes.Add([pscustomobject]@{ Id = $S.NextPid; Name = 'UnifiedRgb.App'; Path = 'C:\x\UnifiedRgb.App.exe'; ByTask = $false }) }
    Reset-Calls $T
    Assert-Error { Install-Qa $T $src2 } 'Failed:' @('GUI', 'C:\x\UnifiedRgb.App.exe', 'nothing was killed', 'kept because something is running', 'no start of the scheduled task was requested') | Out-Null
    Assert-Calls $T @('StopTask') 'no start request while the GUI runs'
    Assert-Equal 0 (Get-Procs $T 'UnifiedRgb.Host').Count 'the stopped Host is reported as possibly stopped, not claimed restarted'
    $m = Get-Manifest $T
    Assert-Equal $r1.VersionId $m.current 'v1 stays current'
    Assert-Equal 2 @($m.versions).Count 'staged version kept and listed'
    $staged1 = @($m.versions | Where-Object { $_.id -ne $r1.VersionId })[0].id
    # Upgrade staging excludes source config until the installed Host exits.
    $stagedExpected = ((Get-TreeNames $src2) -split "`n" | Where-Object { $_ -ne 'F appsettings.json' }) -join "`n"
    Assert-Equal $stagedExpected (Get-TreeNames (Get-VersionDir $T $staged1)) 'every staged file retained; installed config not copied before exit'
    Assert-ManifestMatchesDisk $T @($staged1)
    Assert-Equal $v1Before (Get-Tree (Get-VersionDir $T $r1.VersionId)) 'v1 unchanged'
    Assert-Equal $v1Host $S.Task.Command 'task unchanged'
    $S.Processes.Clear()
    $S.Processes.Add([pscustomobject]@{ Id = 1; Name = 'UnifiedRgb.Host'; Path = $v1Host; ByTask = $true })
    $outsideHost = Join-Path $T.Outside 'other\UnifiedRgb.Host.exe'
    $S.OnStop = { param($S) $S.Processes.Add([pscustomobject]@{ Id = 77; Name = 'UnifiedRgb.Host'; Path = $outsideHost; ByTask = $false }) }.GetNewClosure()
    Reset-Calls $T
    Assert-Error { Install-Qa $T $src2 } 'Failed:' @('does not own', "PID 77, $outsideHost", 'kept because something is running', 'no start of the scheduled task was requested') | Out-Null
    Assert-Calls $T @('StopTask')
    Assert-Equal $v1Host $S.Task.Command 'task unchanged'
    Assert-Equal 3 @((Get-Manifest $T).versions).Count 'the second staged version is kept too'
    Assert-Equal $v1Before (Get-Tree (Get-VersionDir $T $r1.VersionId)) 'v1 unchanged'
    # Once nothing runs, a normal install proceeds and prunes both never-activated staged versions.
    $S.Processes.Clear()
    $S.OnStop = $null
    $r2 = Install-Qa $T $src2
    Assert-Equal 'Upgraded' $r2.Outcome 'clean retry'
    Assert-Equal $r1.VersionId $r2.PreviousVersionId 'from v1'
    $m = Get-Manifest $T
    Assert-Equal "$($r1.VersionId),$($r2.VersionId)" ((@($m.versions) | ForEach-Object { $_.id }) -join ',') 'kept staged versions pruned from the manifest'
    Assert-Equal ((@($r1.VersionId, $r2.VersionId) | Sort-Object) -join '|') ((@([IO.Directory]::GetDirectories((Join-Path $T.InstallDir 'versions')) | ForEach-Object { [IO.Path]::GetFileName($_) } | Sort-Object)) -join '|') 'and from disk'
}

Add-Case 'registration-failure-rolls-back' {
    param($T)
    Add-UserData $T
    $r1 = Install-V1 $T
    $v1Host = Get-HostPath $T $r1.VersionId
    $src2 = New-PublishTree $T 'v2'
    $before = Get-Tree $T.Box
    $S = $T.Platform.State
    # 1: non-zero exit, task untouched
    $S.OnRegister = { param($S, $exe) 1 }
    Reset-Calls $T
    $m = Assert-Error { Install-Qa $T $src2 } 'Failed:' @('exited with 1', 'still runs version', 'staged files removed', 'a start of the scheduled task was requested', 'does not prove which Host runs')
    Assert-Equal $before (Get-Tree $T.Box) 'variant 1: staged files removed, installed files unchanged'
    Assert-Equal $v1Host $S.Task.Command 'variant 1: task unchanged'
    Assert-Equal 4 @($S.Calls).Count "variant 1 calls: $(@($S.Calls) -join ';')"
    Assert-Equal 'StartTask' $S.Calls[3] 'variant 1: a start was requested last'
    # 2: exit 0 but the task does not run the staged Host
    $S.OnRegister = { param($S, $exe) 0 }
    Assert-Error { Install-Qa $T $src2 } 'Failed:' @('does not run the staged Host') | Out-Null
    Assert-Equal $before (Get-Tree $T.Box) 'variant 2'
    # 3: partially registered (task now points at the staged Host) and non-zero exit: previous Host re-registered
    $S.OnRegister = { param($S, $exe) $S.Task = @{ Command = $exe; Arguments = ''; ActionCount = 1; State = 'Ready' }; if ($exe -eq $v1Host) { 0 } else { 5 } }.GetNewClosure()
    Reset-Calls $T
    Assert-Error { Install-Qa $T $src2 } 'Failed:' @('exited with 5', 're-registered to version') | Out-Null
    $calls = @($S.Calls.ToArray())
    Assert-Equal "register|$v1Host" $calls[3] "variant 3 re-registers the previous Host: $($calls -join ';')"
    Assert-Equal 'StartTask' $calls[4] 'variant 3 requests a start'
    Assert-Equal $before (Get-Tree $T.Box) 'variant 3'
    Assert-Equal $v1Host $S.Task.Command 'variant 3: task back on v1'
    # 4: maintenance verb throws
    $S.OnRegister = { param($S, $exe) throw 'fake: launch failed' }
    Assert-Error { Install-Qa $T $src2 } 'Failed:' @('launch failed') | Out-Null
    Assert-Equal $before (Get-Tree $T.Box) 'variant 4'
    $S.OnRegister = $null
    $r2 = Install-Qa $T $src2
    Assert-Equal 'Upgraded' $r2.Outcome 'a clean retry succeeds'
    Assert-Equal $r1.VersionId $r2.PreviousVersionId 'retry keeps v1 as previous'
}

Add-Case 'registration-partial-with-tampered-previous-host-keeps-staged' {
    param($T)
    $r1 = Install-V1 $T
    $v1Host = Get-HostPath $T $r1.VersionId
    [IO.File]::AppendAllText($v1Host, ' tampered')
    $S = $T.Platform.State
    $S.OnRegister = { param($S, $exe) $S.Task = @{ Command = $exe; Arguments = ''; ActionCount = 1; State = 'Ready' }; 3 }
    Reset-Calls $T
    Assert-Error { Install-Qa $T (New-PublishTree $T 'v2') } 'Failed:' @('previous Host is modified', 'kept because the task still points at it') | Out-Null
    $m = Get-Manifest $T
    Assert-Equal $r1.VersionId $m.current 'v1 still current'
    Assert-Equal 2 @($m.versions).Count 'staged version kept and listed'
    $staged = @($m.versions | Where-Object { $_.id -ne $r1.VersionId })[0].id
    Assert-Equal (Get-HostPath $T $staged) $S.Task.Command 'task points at the kept staged Host, which still exists'
    Assert-True ([IO.File]::Exists($S.Task.Command)) 'staged Host exists'
    Assert-True (-not ($S.Calls -contains 'StartTask')) 'no restart of an unverified state'
    Assert-ManifestMatchesDisk $T @($staged)
    # F6-2, revised expectation: the task points at the kept staged version, which is still exactly what was staged, so
    # the next install reconciles the interrupted switch and adopts it: its configuration is preserved and it becomes the
    # rollback target. The earlier expectation (previous = v1, the staged version pruned) made the version with the
    # modified Host the rollback target and deleted the version the task actually pointed at.
    $S.OnRegister = $null
    Reset-Calls $T
    $r3 = Install-Qa $T (New-PublishTree $T 'v3')
    Assert-Equal 'Upgraded' $r3.Outcome 'a later install recovers'
    Assert-Equal $staged $r3.PreviousVersionId 'upgraded from the version the task points at'
    Assert-Contains $r3.Warnings 'interrupted' 'the reconciliation is reported'
    $m = Get-Manifest $T
    Assert-Equal $r3.VersionId $m.current 'v3 current'
    Assert-Equal $staged $m.previous 'previous is the verified version the task pointed at, not the tampered v1'
    Assert-True ([IO.Directory]::Exists((Get-VersionDir $T $staged))) 'the adopted version is kept for rollback'
    Assert-Equal 'F UnifiedRgb.Host.exe' (Get-TreeNames (Get-VersionDir $T $r1.VersionId)) 'v1 pruned except its modified Host'
    Assert-Contains $r3.Kept 'modified since installation' 'the modified v1 Host is kept and reported'
    Assert-Equal ([Convert]::ToBase64String([IO.File]::ReadAllBytes((Join-Path (Get-VersionDir $T $staged) 'appsettings.json')))) ([Convert]::ToBase64String([IO.File]::ReadAllBytes((Join-Path (Get-VersionDir $T $r3.VersionId) 'appsettings.json')))) 'configuration carried from the adopted version'
    Assert-Calls $T @('StopTask', 'EnsureData', "register|$(Get-HostPath $T $r3.VersionId)", 'SetRunValue', 'StartTask')
    Assert-ManifestMatchesDisk $T @($staged, $r3.VersionId)
}

Add-Case 'first-install-registration-failure-leaves-nothing' {
    param($T)
    $S = $T.Platform.State
    $S.OnRegister = { param($S, $exe) $S.Task = @{ Command = $exe; Arguments = ''; ActionCount = 1; State = 'Ready' }; 1 }
    $pfBefore = Get-Tree $T.ProgramFiles
    Assert-Error { Install-Qa $T (New-PublishTree $T 'v1') } 'Failed:' @('partially registered task was removed', 'staged files removed') | Out-Null
    Assert-Equal $pfBefore (Get-Tree $T.ProgramFiles) 'install root removed again'
    Assert-True ($null -eq $S.Task) 'task removed'
    Assert-True ($null -eq $S.Run) 'no tray entry'
    Assert-Equal 'EnsureData' $S.Calls[0] 'data'
    Assert-Equal 'UnregisterTask' $S.Calls[2] 'unregistered'
    Assert-True (-not ($S.Calls -contains 'StartTask')) 'nothing started'
}

Add-Case 'staged-file-tampered-before-switch-is-not-activated' {
    param($T)
    $r1 = Install-V1 $T
    $versions = Join-Path $T.InstallDir 'versions'
    $v1Id = $r1.VersionId
    $S = $T.Platform.State
    $S.OnEnsureData = {
        param($S, $Path)
        foreach ($d in [IO.Directory]::GetDirectories($versions)) {
            if ([IO.Path]::GetFileName($d) -ne $v1Id) { [IO.File]::AppendAllText((Join-Path $d 'UnifiedRgb.Core.dll'), ' swapped'); $S.Observed.Tampered = $d }
        }
    }.GetNewClosure()
    Reset-Calls $T
    Assert-Error { Install-Qa $T (New-PublishTree $T 'v2') } 'Failed:' @('final verification', 'modified', 'stays active', 'could only partly be removed', 'a start of the scheduled task was requested') | Out-Null
    Assert-Calls $T @('StopTask', 'EnsureData', 'StartTask') 'never registered'
    $staged = [IO.Path]::GetFileName($S.Observed.Tampered)
    Assert-Equal 'F UnifiedRgb.Core.dll' ((@((Get-Tree $S.Observed.Tampered) -split "`n") | ForEach-Object { ($_ -split ' ')[0..1] -join ' ' }) -join "`n") 'only the tampered file is left in the staged directory'
    $m = Get-Manifest $T
    Assert-Equal $v1Id $m.current 'v1 current'
    Assert-Equal 'UnifiedRgb.Core.dll' ((@((Get-ManifestVersion $m $staged).files) | ForEach-Object { $_.path }) -join ',') 'tampered file stays listed'
    Assert-Equal (Get-HostPath $T $v1Id) $S.Task.Command 'task unchanged'
    $S.OnEnsureData = { param($S, $Path) throw 'fake: disk full' }
    $src3 = New-PublishTree $T 'v3'
    $before = Get-Tree $T.Box
    Reset-Calls $T
    Assert-Error { Install-Qa $T $src3 } 'Failed:' @('disk full', 'staged files removed') | Out-Null
    Assert-Calls $T @('StopTask', 'EnsureData', 'StartTask')
    Assert-Equal $before (Get-Tree $T.Box) 'data-directory failure rolled back'
}

Add-Case 'staging-copy-failure-touches-nothing-running' {
    param($T)
    $null = Install-V1 $T
    $src2 = New-PublishTree $T 'v2'
    $locked = Join-Path $src2 'UnifiedRgb.Core.dll'
    $holder = @{ Stream = $null }
    $reporter = { param($Line) if ($Line -like 'Staging version*' -and $null -eq $holder.Stream) { $holder.Stream = [IO.File]::Open($locked, 'Open', 'Read', 'None') } }.GetNewClosure()
    $before = Get-Tree $T.Box
    Reset-Calls $T
    try { Assert-Error { Install-Qa $T $src2 @{ Reporter = $reporter } } 'Failed:' @('Staging version', 'were not touched') | Out-Null }
    finally { if ($null -ne $holder.Stream) { $holder.Stream.Dispose() } }
    Assert-True ($null -ne $holder.Stream) 'source file was locked during copy'
    Assert-Calls $T @() 'no stop, registration or data action'
    Assert-Equal $before (Get-Tree $T.Box) 'nothing changed'
}

# ============================================================== uninstall

Add-Case 'uninstall-deletes-only-unchanged-owned-files' {
    param($T)
    Add-UserData $T
    $r1 = Install-V1 $T
    $r2 = Install-Qa $T (New-PublishTree $T 'v2')
    $v1dir = Get-VersionDir $T $r1.VersionId
    $v2dir = Get-VersionDir $T $r2.VersionId
    [IO.File]::AppendAllText((Join-Path $v1dir 'UnifiedRgb.Core.dll'), ' patched')
    Write-QaFile (Join-Path $v2dir 'logs\local.log') 'unknown'
    Write-QaFile (Join-Path $T.InstallDir 'README-local.txt') 'unknown at root'
    $dataBefore = Get-Tree $T.DataDir
    $pfOutside = Join-Path $T.ProgramFiles 'OtherVendor\keep.txt'
    Write-QaFile $pfOutside 'another product'
    Reset-Calls $T
    $r = Uninstall-Qa $T
    Assert-Equal 'Uninstalled' $r.Outcome 'outcome'
    $v2Host = Get-HostPath $T $r2.VersionId
    Assert-Calls $T @('StopTask', "restore|$v2Host", 'UnregisterTask', 'RemoveRunValue')
    Assert-True ($null -eq $T.Platform.State.Task) 'task removed'
    Assert-True ($null -eq $T.Platform.State.Run) 'tray entry removed'
    Assert-Equal 0 (Get-Procs $T 'UnifiedRgb.Host').Count 'Host exited'
    Assert-Equal $dataBefore (Get-Tree $T.DataDir) 'ProgramData untouched'
    Assert-True ([IO.File]::Exists($pfOutside)) 'other product untouched'
    $left = @((Get-TreeNames $T.InstallDir) -split "`n")
    $expected = @('F README-local.txt', 'F UnifiedRgb.install.json', 'D versions',
        "D versions\$($r1.VersionId)", "F versions\$($r1.VersionId)\UnifiedRgb.Core.dll",
        "D versions\$($r2.VersionId)", "F versions\$($r2.VersionId)\appsettings.json",
        "D versions\$($r2.VersionId)\logs", "F versions\$($r2.VersionId)\logs\local.log")
    Assert-Equal (($expected | Sort-Object) -join "`n") (($left | Sort-Object) -join "`n") 'exactly the unknown, modified and carried-over configuration files are left'
    $m = Get-Manifest $T
    Assert-True ($null -eq $m.current -and $null -eq $m.previous) 'no active version'
    Assert-Equal 'UnifiedRgb.Core.dll|appsettings.json' ((@($m.versions) | ForEach-Object { @($_.files) | ForEach-Object { $_.path } }) -join '|') 'manifest lists only kept owned files'
    Assert-Contains $r.Kept 'modified since installation' 'kept modified'
    Assert-Contains $r.Kept 'carried over' 'kept configuration'
    Assert-Contains $r.Kept 'not installed by UnifiedRgb' 'kept unknown'
    Assert-ManifestMatchesDisk $T @($r2.VersionId)
    # Owner removes the leftovers they do not want; a second uninstall then finishes without restoring again.
    [IO.File]::Delete((Join-Path $T.InstallDir 'README-local.txt'))
    [IO.File]::Delete((Join-Path $v2dir 'logs\local.log'))
    [IO.Directory]::Delete((Join-Path $v2dir 'logs'))
    Reset-Calls $T
    $again = Uninstall-Qa $T
    Assert-Equal 'Uninstalled' $again.Outcome 'second run'
    Assert-Calls $T @() 'no task, restore or registry action the second time'
    Assert-True ([IO.File]::Exists((Join-Path $v1dir 'UnifiedRgb.Core.dll'))) 'still keeps the modified file'
    Assert-True ([IO.File]::Exists((Join-Path $v2dir 'appsettings.json'))) 'still keeps the configuration'
    $expectedLeft = @('F UnifiedRgb.install.json', 'D versions', "D versions\$($r1.VersionId)", "F versions\$($r1.VersionId)\UnifiedRgb.Core.dll",
        "D versions\$($r2.VersionId)", "F versions\$($r2.VersionId)\appsettings.json")
    Assert-Equal (($expectedLeft | Sort-Object) -join "`n") ((@((Get-TreeNames $T.InstallDir) -split "`n") | Sort-Object) -join "`n") 'only the kept owned files and their manifest remain'
}

Add-Case 'uninstall-clean-installation-removes-root-keeps-data' {
    param($T)
    Add-UserData $T
    $r1 = Install-V1 $T
    $dataBefore = Get-Tree $T.DataDir
    Reset-Calls $T
    $r = Uninstall-Qa $T
    Assert-Equal 'Uninstalled' $r.Outcome 'outcome'
    Assert-Calls $T @('StopTask', "restore|$(Get-HostPath $T $r1.VersionId)", 'UnregisterTask', 'RemoveRunValue')
    Assert-Equal '<absent>' (Get-Tree $T.InstallDir) 'install root removed'
    Assert-Equal '' (Get-Tree $T.ProgramFiles) 'product parent kept and otherwise empty'
    Assert-Equal $dataBefore (Get-Tree $T.DataDir) 'data kept byte for byte'
    Assert-Contains $r.Log 'Kept the data directory' 'log'
}

Add-Case 'uninstall-restore-failure-keeps-files-and-can-rerun' {
    param($T)
    $r1 = Install-V1 $T
    $before = Get-Tree $T.InstallDir
    $S = $T.Platform.State
    $S.RestoreExit = 4
    Reset-Calls $T
    $r = Uninstall-Qa $T
    Assert-Equal 'RestoreFailed' $r.Outcome 'outcome'
    Assert-Equal $before (Get-Tree $T.InstallDir) 'all files kept'
    Assert-Contains $r.Warnings 'exited with 4' 'warning'
    Assert-Equal $r1.VersionId (Get-Manifest $T).current 'still current'
    $S.RestoreExit = 0
    Reset-Calls $T
    $again = Uninstall-Qa $T
    Assert-Equal 'Uninstalled' $again.Outcome 'rerun'
    Assert-Calls $T @("restore|$(Get-HostPath $T $r1.VersionId)") 'rerun restores again; task and Run value already gone'
    Assert-Equal '<absent>' (Get-Tree $T.InstallDir) 'removed after a successful restore'
}

Add-Case 'uninstall-missing-or-modified-host-is-refused' {
    param($T)
    $r1 = Install-V1 $T
    $hostPath = Get-HostPath $T $r1.VersionId
    $original = [IO.File]::ReadAllBytes($hostPath)
    [IO.File]::Delete($hostPath)
    $before = Get-Tree $T.Box
    Reset-Calls $T
    Assert-Error { Uninstall-Qa $T } 'Refused:' @('is missing', 'does not permit deleting') | Out-Null
    Assert-Calls $T @()
    Assert-Equal $before (Get-Tree $T.Box) 'missing Host: nothing deleted'
    [IO.File]::WriteAllText($hostPath, 'replaced binary')
    $before = Get-Tree $T.Box
    Assert-Error { Uninstall-Qa $T } 'Refused:' @('modified since installation') | Out-Null
    Assert-Calls $T @()
    Assert-Equal $before (Get-Tree $T.Box) 'modified Host: nothing deleted'
    [IO.File]::WriteAllBytes($hostPath, $original)
    Assert-Equal 'Uninstalled' (Uninstall-Qa $T).Outcome 'repaired Host: uninstall proceeds'
}

Add-Case 'uninstall-stop-timeout-or-blocker-restores-and-deletes-nothing' {
    param($T)
    $r1 = Install-V1 $T
    $S = $T.Platform.State
    $S.StopBehavior = 'ignore'
    $before = Get-Tree $T.Box
    $run = $S.Run
    Reset-Calls $T
    Assert-Error { Uninstall-Qa $T } 'Failed:' @('did not exit', 'No vendor services were restored', 'nothing was deleted') | Out-Null
    Assert-Calls $T @('StopTask')
    Assert-Equal $before (Get-Tree $T.Box) 'nothing deleted'
    Assert-True ($null -ne $S.Task -and $S.Run -eq $run) 'task and Run value kept'
    $S.StopBehavior = 'delayed'
    $S.StopDelayMs = 5000
    $other = Join-Path $T.Outside 'UnifiedRgb.Host.exe'
    $S.OnSleep = { param($S) if ($S.ClockMs -ge 500 -and @($S.Processes | Where-Object { $_.Id -eq 99 }).Count -eq 0) { $S.Processes.Add([pscustomobject]@{ Id = 99; Name = 'UnifiedRgb.Host'; Path = $other; ByTask = $false }) } }.GetNewClosure()
    Reset-Calls $T
    Assert-Error { Uninstall-Qa $T } 'Failed:' @('does not own', 'No vendor services were restored', 'No start of the scheduled task was requested because something is running', "PID 99, $other") | Out-Null
    Assert-Calls $T @('StopTask') 'F6-1: no start request while an unowned Host runs'
    Assert-Equal $before (Get-Tree $T.Box) 'nothing deleted'
    Assert-True ($null -ne $r1) 'v1'
}

# ============================================================== ownership of directories and paths

Add-Case 'unrelated-legacy-and-absent-directories' {
    param($T)
    Write-QaFile (Join-Path $T.InstallDir 'notes.txt') 'not ours'
    $src = New-PublishTree $T 'v1'
    $before = Get-Tree $T.Box
    Assert-Error { Install-Qa $T $src } 'Refused:' @('not a manifest-owned') | Out-Null
    Assert-Error { Uninstall-Qa $T } 'Refused:' @('not a manifest-owned') | Out-Null
    Assert-Equal $before (Get-Tree $T.Box) 'unrelated directory untouched'
    [IO.File]::Delete((Join-Path $T.InstallDir 'notes.txt'))
    foreach ($n in 'UnifiedRgb.Host.exe', 'UnifiedRgb.App.exe', 'appsettings.json') { Write-QaFile (Join-Path $T.InstallDir $n) "legacy $n" }
    $before = Get-Tree $T.Box
    Assert-Error { Install-Qa $T $src } 'Refused:' @('earlier unmanaged installer') | Out-Null
    Assert-Error { Uninstall-Qa $T } 'Refused:' @('earlier unmanaged installer') | Out-Null
    Assert-Equal $before (Get-Tree $T.Box) 'legacy directory not adopted or deleted'
    Assert-Calls $T @()
    $T2 = New-Sandbox 'unrelated-legacy-and-absent-directories\absent'
    $T2.Platform.State.Task = @{ Command = 'C:\Somewhere\UnifiedRgb.Host.exe'; Arguments = ''; ActionCount = 1; State = 'Ready' }
    $before = Get-Tree $T2.Box
    $r = Uninstall-Qa $T2
    Assert-Equal 'NotInstalled' $r.Outcome 'absent root'
    Assert-Contains $r.Warnings 'left unchanged' 'foreign task reported, not removed'
    Assert-Calls $T2 @()
    Assert-Equal $before (Get-Tree $T2.Box) 'nothing created'
}

Add-Case 'bad-paths-are-refused' {
    param($T)
    $src = New-PublishTree $T 'v1'
    Write-QaFile (Join-Path $T.ProgramFiles 'a-file') 'file'
    $pf = $T.ProgramFiles
    $driveRoot = [IO.Path]::GetPathRoot($T.Box)
    $cases = @(
        @{ Why = 'unused drive root'; InstallDir = 'Q:\'; Expect = 'drive root' },
        @{ Why = 'drive root of the sandbox'; InstallDir = $driveRoot; Expect = 'drive root' },
        @{ Why = 'drive-relative'; InstallDir = 'Q:UnifiedRgb'; Expect = 'absolute local drive path' },
        @{ Why = 'relative'; InstallDir = 'UnifiedRgb'; Expect = 'absolute local drive path' },
        @{ Why = 'UNC'; InstallDir = '\\server\share\UnifiedRgb'; Expect = 'absolute local drive path' },
        @{ Why = 'device path'; InstallDir = "\\?\$pf\UnifiedRgb"; Expect = 'absolute local drive path' },
        @{ Why = 'equal to the product parent'; InstallDir = $pf; Expect = 'outside the reviewed product root' },
        @{ Why = 'outside the product parent'; InstallDir = (Join-Path $T.Outside 'UnifiedRgb'); Expect = 'outside the reviewed product root' },
        @{ Why = 'dot-dot escape'; InstallDir = "$pf\UnifiedRgb\..\..\outside\UnifiedRgb"; Expect = 'outside the reviewed product root' },
        @{ Why = 'alternate data stream'; InstallDir = "$pf\UnifiedRgb:stream"; Expect = 'stream or wildcard' },
        @{ Why = 'wildcard'; InstallDir = "$pf\Unified*"; Expect = 'stream or wildcard' },
        @{ Why = 'install path is a file'; InstallDir = (Join-Path $pf 'a-file'); Expect = 'is a file' },
        @{ Why = 'product parent is a drive root'; AllowedParent = 'Q:\'; InstallDir = 'Q:\UnifiedRgb'; Expect = 'drive root' },
        @{ Why = 'data inside the install root'; DataDir = (Join-Path $T.InstallDir 'data'); Expect = 'overlaps the data directory' },
        @{ Why = 'install root inside the data directory'; DataDir = $pf; Expect = 'overlaps the data directory' },
        @{ Why = 'source inside the install root'; Source = (Join-Path $T.InstallDir 'src'); Expect = 'overlaps InstallDir'; InstallOnly = $true },
        @{ Why = 'source contains the install root'; Source = $T.Box; Expect = 'overlaps'; InstallOnly = $true }
    )
    $before = Get-Tree $T.Box
    foreach ($c in $cases) {
        $a = @{ InstallDir = $T.InstallDir; AllowedParent = $pf; DataDir = $T.DataDir; Platform = $T.Platform; Confirm = $false }
        foreach ($k in 'InstallDir', 'AllowedParent', 'DataDir') { if ($c.ContainsKey($k)) { $a[$k] = $c[$k] } }
        $ia = $a.Clone(); $ia.Source = if ($c.ContainsKey('Source')) { $c.Source } else { $src }
        try { Assert-Error { Invoke-UrgbInstall @ia } 'Refused:' @($c.Expect) | Out-Null }
        catch { throw "install, $($c.Why): $($_.Exception.Message)" }
        if (-not $c.ContainsKey('InstallOnly')) {
            try { Assert-Error { Invoke-UrgbUninstall @a } 'Refused:' @($c.Expect) | Out-Null }
            catch { throw "uninstall, $($c.Why): $($_.Exception.Message)" }
        }
    }
    Assert-Calls $T @()
    Assert-Equal $before (Get-Tree $T.Box) 'no path case changed anything'
}

Add-Case 'invalid-or-foreign-manifests-are-refused' {
    param($T)
    $null = Install-V1 $T @{ NoStart = $true }
    Reset-Calls $T
    $manifestPath = Join-Path $T.InstallDir 'UnifiedRgb.install.json'
    $good = [IO.File]::ReadAllText($manifestPath, $script:Utf8)
    $victim = Join-Path $T.Box 'outside\victim.txt'
    Write-QaFile $victim 'must survive'
    # Regular-expression edits tolerate both Windows PowerShell and PowerShell 7 JSON spacing; each must match.
    $victimJson = $victim.Replace('\', '\\')
    $mutations = @(
        @{ Why = 'other installRoot'; Pattern = '"installRoot":\s*"[^"]*"'; To = '"installRoot": "C:\\Program Files\\Elsewhere"' },
        @{ Why = 'dot-dot file path'; Pattern = '"path":\s*"UnifiedRgb\.Core\.dll"'; To = '"path": "..\\..\\..\\outside\\victim.txt"' },
        @{ Why = 'rooted file path'; Pattern = '"path":\s*"UnifiedRgb\.Core\.dll"'; To = ('"path": "' + $victimJson + '"') },
        @{ Why = 'unknown current'; Pattern = '"current":\s*"[^"]+"'; To = '"current": "v-29991231T000000-deadbeef"' },
        @{ Why = 'bad hash'; Pattern = '"sha256":\s*"'; To = '"sha256": "X' },
        @{ Why = 'schema'; Pattern = '"schema":\s*1'; To = '"schema": 2' },
        @{ Why = 'kind'; Pattern = '"kind":\s*"program"'; To = '"kind": "anything"' },
        @{ Why = 'config kind on another file'; Pattern = '"kind":\s*"program"'; To = '"kind": "preserved-config"' },
        @{ Why = 'truncated'; Truncate = $true }
    )
    foreach ($mu in $mutations) {
        if ($mu.ContainsKey('Truncate')) { $text = $good.Substring(0, 40) }
        else {
            $regex = [regex]::new($mu.Pattern)
            Assert-True ($regex.IsMatch($good)) "mutation '$($mu.Why)' applies"
            $text = $regex.Replace($good, $mu.To.Replace('$', '$$'), 1)
        }
        [IO.File]::WriteAllText($manifestPath, $text, $script:Utf8)
        $before = Get-Tree $T.Box
        try {
            Assert-Error { Install-Qa $T (Join-Path $T.Box 'src-v1') } 'Refused:' @('manifest') | Out-Null
            Assert-Error { Uninstall-Qa $T } 'Refused:' @('manifest') | Out-Null
        }
        catch { throw "$($mu.Why): $($_.Exception.Message)" }
        Assert-Equal $before (Get-Tree $T.Box) "nothing changed ($($mu.Why))"
    }
    [IO.File]::WriteAllText($manifestPath, $good, $script:Utf8)
    # A copied/moved installation is not owned at its new location.
    $copy = Join-Path $T.ProgramFiles 'UnifiedRgbCopy'
    foreach ($f in [IO.Directory]::GetFiles($T.InstallDir, '*', 'AllDirectories')) {
        $copied = $copy + $f.Substring($T.InstallDir.Length)   # (not $t: PowerShell names are case-insensitive)
        [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($copied)); [IO.File]::Copy($f, $copied)
    }
    $before = Get-Tree $T.Box
    Assert-Error { Invoke-UrgbUninstall -InstallDir $copy -AllowedParent $T.ProgramFiles -DataDir $T.DataDir -Platform $T.Platform -Confirm:$false } 'Refused:' @('different directory') | Out-Null
    Assert-Equal $before (Get-Tree $T.Box) 'copy untouched'
    Assert-Equal 'must survive' ([IO.File]::ReadAllText($victim)) 'victim intact'
    Assert-Calls $T @()
}

Add-Case 'junctions-and-links-are-refused' {
    param($T)
    $src = New-PublishTree $T 'v1'
    $S = $T.Platform.State
    # 1: the install root itself is a junction to an outside directory holding a real installation
    $real = New-Sandbox ([IO.Path]::GetFileName($T.Box) + 'r')
    $null = Install-Qa $real (New-PublishTree $real 'v1') @{ NoStart = $true }
    New-Junction $T.InstallDir $real.InstallDir
    $before = Get-Tree $real.Box
    Assert-Error { Install-Qa $T $src } 'Refused:' @('reparse point') | Out-Null
    Assert-Error { Uninstall-Qa $T } 'Refused:' @('reparse point') | Out-Null
    Assert-Equal $before (Get-Tree $real.Box) 'junction target untouched'
    [IO.Directory]::Delete($T.InstallDir)   # removes only the junction itself
    # 2: an ancestor of the install root is a junction
    $link = Join-Path $T.ProgramFiles 'linked'
    New-Junction $link (Join-Path $T.Outside 'linked-target')
    Assert-Error { Install-Qa $T $src @{ InstallDir = (Join-Path $link 'UnifiedRgb') } } 'Refused:' @('reparse point') | Out-Null
    Assert-Equal '' (Get-Tree (Join-Path $T.Outside 'linked-target')) 'nothing created through the junction'
    # 3: Source is, or contains, a junction
    $srcLink = Join-Path $T.Box 'src-link'
    New-Junction $srcLink $src
    Assert-Error { Install-Qa $T $srcLink } 'Refused:' @('Source') | Out-Null
    $src2 = New-PublishTree $T 'v1b'
    New-Junction (Join-Path $src2 'linked-dir') (Join-Path $T.Outside 'secrets')
    Write-QaFile (Join-Path $T.Outside 'secrets\token.txt') 'secret'
    Assert-Error { Install-Qa $T $src2 } 'Refused:' @('reparse point') | Out-Null
    # 4: the data directory is a junction
    New-Junction $T.DataDir (Join-Path $T.Outside 'data-target')
    Assert-Error { Install-Qa $T $src } 'Refused:' @('data directory') | Out-Null
    [IO.Directory]::Delete($T.DataDir)
    Assert-True (-not [IO.Directory]::Exists($T.InstallDir)) 'nothing was installed by the refused cases'
    Assert-Calls $T @()
    # 5: 'versions' replaced by a junction
    $r1 = Install-Qa $T $src @{ NoStart = $true }
    $versions = Join-Path $T.InstallDir 'versions'
    $moved = Join-Path $T.Outside 'moved-versions'
    [IO.Directory]::Move($versions, $moved)
    New-Junction $versions $moved
    $before = Get-Tree $T.Outside
    Reset-Calls $T
    Assert-Error { Install-Qa $T (New-PublishTree $T 'v2') } 'Refused:' @('versions') | Out-Null
    Assert-Error { Uninstall-Qa $T } 'Refused:' @('versions') | Out-Null
    Assert-Equal $before (Get-Tree $T.Outside) 'moved versions untouched'
    [IO.Directory]::Delete($versions)
    [IO.Directory]::Move($moved, $versions)
    # 6: a version directory replaced by a junction
    $vdir = Get-VersionDir $T $r1.VersionId
    $movedVersion = Join-Path $T.Outside 'moved-version'
    [IO.Directory]::Move($vdir, $movedVersion)
    New-Junction $vdir $movedVersion
    $before = Get-Tree $T.Outside
    Assert-Error { Uninstall-Qa $T } 'Refused:' @('not a plain directory') | Out-Null
    Assert-Equal $before (Get-Tree $T.Outside) 'moved version untouched'
    [IO.Directory]::Delete($vdir)
    [IO.Directory]::Move($movedVersion, $vdir)
    # 7: a sub-directory inside a version is a junction to identical outside files (same names and hashes)
    $runtimes = Join-Path $vdir 'runtimes'
    $victimDir = Join-Path $T.Outside 'victim-runtimes'
    [IO.Directory]::Move($runtimes, $victimDir)
    New-Junction $runtimes $victimDir
    $victimBefore = Get-Tree $victimDir
    Reset-Calls $T
    $r = Uninstall-Qa $T
    Assert-Equal 'Uninstalled' $r.Outcome 'uninstall proceeds for the regular files'
    Assert-Equal $victimBefore (Get-Tree $victimDir) 'files behind the junction were not deleted'
    Assert-True (([IO.File]::GetAttributes($runtimes) -band [IO.FileAttributes]::ReparsePoint) -ne 0) 'junction itself kept'
    Assert-Contains $r.Kept 'reparse point' 'reported'
    Assert-True (-not [IO.File]::Exists((Join-Path $vdir 'UnifiedRgb.Host.exe'))) 'regular owned files deleted'
    # Leave no links in the kept sandbox: non-recursive Directory.Delete removes only the junction itself.
    foreach ($l in $runtimes, $link, $srcLink, (Join-Path $src2 'linked-dir')) { [IO.Directory]::Delete($l) }
    Assert-Equal 'secret' ([IO.File]::ReadAllText((Join-Path $T.Outside 'secrets\token.txt'))) 'junction targets survive link cleanup'
}

Add-Case 'file-symbolic-links-are-refused-or-kept' {
    param($T)
    # Needs Developer Mode or SeCreateSymbolicLinkPrivilege; reported as Skipped (not Passed) when Windows refuses.
    $probeTarget = Join-Path $T.Outside 'probe.txt'
    Write-QaFile $probeTarget 'probe'
    try { $null = New-Item -ItemType SymbolicLink -Path (Join-Path $T.Outside 'probe-link.txt') -Target $probeTarget -ErrorAction Stop }
    catch { throw "SKIP: this account may not create file symbolic links ($($_.Exception.GetType().Name))" }
    # 1: a publish tree containing a file symlink is refused
    $src = New-PublishTree $T 'v1'
    $src2 = New-PublishTree $T 'v1b'
    $secret = Join-Path $T.Outside 'secret.dll'
    Write-QaFile $secret 'secret'
    $null = New-Item -ItemType SymbolicLink -Path (Join-Path $src2 'linked.dll') -Target $secret
    Assert-Error { Install-Qa $T $src2 } 'Refused:' @('reparse point') | Out-Null
    Assert-True (-not [IO.Directory]::Exists($T.InstallDir)) 'nothing installed'
    # 2: an owned file replaced by a symlink to identical outside bytes is kept on uninstall, and its target survives
    $r1 = Install-Qa $T $src @{ NoStart = $true }
    $owned = Join-Path (Get-VersionDir $T $r1.VersionId) 'UnifiedRgb.Core.dll'
    $victim = Join-Path $T.Outside 'victim-core.dll'
    [IO.File]::Copy($owned, $victim)
    [IO.File]::Delete($owned)
    $null = New-Item -ItemType SymbolicLink -Path $owned -Target $victim
    $victimBefore = Get-Sha $victim
    $r = Uninstall-Qa $T
    Assert-Equal 'Uninstalled' $r.Outcome 'uninstall proceeds for the regular files'
    Assert-Equal $victimBefore (Get-Sha $victim) 'symlink target intact'
    Assert-True (([IO.File]::GetAttributes($owned) -band [IO.FileAttributes]::ReparsePoint) -ne 0) 'symlink itself kept'
    Assert-Contains $r.Kept 'reparse point' 'reported'
}

Add-Case 'foreign-task-and-run-value' {
    param($T)
    $S = $T.Platform.State
    $src = New-PublishTree $T 'v1'
    $S.Task = @{ Command = 'E:\dev\src\UnifiedRgb.Host\bin\UnifiedRgb.Host.exe'; Arguments = ''; ActionCount = 1; State = 'Running' }
    $before = Get-Tree $T.Box
    Assert-Error { Install-Qa $T $src } 'Refused:' @('does not own') | Out-Null
    Assert-Equal $before (Get-Tree $T.Box) 'first install refused under a foreign task'
    $S.Task = $null
    $S.Run = '"C:\Other\UnifiedRgb.App.exe" --minimized'
    $r1 = Install-Qa $T $src
    Assert-Equal '"C:\Other\UnifiedRgb.App.exe" --minimized' $S.Run 'foreign Run value unchanged'
    Assert-Contains $r1.Warnings 'does not own' 'warned'
    Assert-True (-not ($S.Calls -contains 'SetRunValue')) 'Run value not written'
    $hostPath = Get-HostPath $T $r1.VersionId
    $src2 = New-PublishTree $T 'v2'
    foreach ($variant in @(@{ Arguments = '--other'; ActionCount = 1 }, @{ Arguments = ''; ActionCount = 2 })) {
        $S.Task = @{ Command = $hostPath; Arguments = $variant.Arguments; ActionCount = $variant.ActionCount; State = 'Running' }
        $before = Get-Tree $T.Box
        Reset-Calls $T
        Assert-Error { Install-Qa $T $src2 } 'Refused:' @('does not own') | Out-Null
        Assert-Error { Uninstall-Qa $T } 'Refused:' @('does not own') | Out-Null
        Assert-Calls $T @()
        Assert-Equal $before (Get-Tree $T.Box) 'nothing changed'
    }
    $S.Task = @{ Command = "`"$hostPath`""; Arguments = ''; ActionCount = 1; State = 'Running' }
    $r2 = Install-Qa $T $src2
    Assert-Equal 'Upgraded' $r2.Outcome 'a quoted command is still recognised as owned'
    Reset-Calls $T
    $r = Uninstall-Qa $T
    Assert-Equal 'Uninstalled' $r.Outcome 'uninstall'
    Assert-Equal '"C:\Other\UnifiedRgb.App.exe" --minimized' $S.Run 'foreign Run value still unchanged'
    Assert-True (-not ($S.Calls -contains 'RemoveRunValue')) 'Run value not removed'
}

Add-Case 'whatif-changes-nothing' {
    param($T)
    $src = New-PublishTree $T 'v1'
    $before = Get-Tree $T.Box
    $r = Install-Qa $T $src @{ WhatIf = $true }
    Assert-Equal 'WhatIf' $r.Outcome 'install'
    Assert-Equal $before (Get-Tree $T.Box) 'nothing created'
    $null = Install-V1 $T
    $before = Get-Tree $T.Box
    Reset-Calls $T
    Assert-Equal 'WhatIf' (Install-Qa $T (Join-Path $T.Box 'src-v1') @{ WhatIf = $true }).Outcome 'upgrade'
    Assert-Equal 'WhatIf' (Uninstall-Qa $T @{ WhatIf = $true }).Outcome 'uninstall'
    Assert-Calls $T @()
    Assert-Equal $before (Get-Tree $T.Box) 'nothing changed'
}

Add-Case 'concurrent-run-is-refused' {
    param($T)
    $name = $T.Platform.LockName
    $ready = [Threading.ManualResetEvent]::new($false)
    $release = [Threading.ManualResetEvent]::new($false)
    $ps = [powershell]::Create()
    try {
        $null = $ps.AddScript({ param($Name, $Ready, $Release)
                $m = [Threading.Mutex]::new($false, $Name); [void]$m.WaitOne(); [void]$Ready.Set(); [void]$Release.WaitOne(); $m.ReleaseMutex(); $m.Dispose() }).AddArgument($name).AddArgument($ready).AddArgument($release)
        $handle = $ps.BeginInvoke()
        Assert-True ($ready.WaitOne(10000)) 'other holder acquired the lock'
        $before = Get-Tree $T.Box
        $src = New-PublishTree $T 'v1'
        $before = Get-Tree $T.Box
        Assert-Error { Install-Qa $T $src } 'Refused:' @('Another UnifiedRgb install') | Out-Null
        Assert-Error { Uninstall-Qa $T } 'Refused:' @('Another UnifiedRgb install') | Out-Null
        Assert-Equal $before (Get-Tree $T.Box) 'nothing changed'
        [void]$release.Set()
        $ps.EndInvoke($handle)
    }
    finally { [void]$release.Set(); $ps.Dispose(); $ready.Dispose(); $release.Dispose() }
    Assert-Equal 'Installed' (Install-Qa $T (Join-Path $T.Box 'src-v1')).Outcome 'works after the other run ends'
}

Add-Case 'platform-contract-is-required' {
    param($T)
    $partial = [pscustomobject]@{ LockName = 'Local\x'; GetProcesses = { } }
    $caught = $null
    try { $null = Invoke-UrgbUninstall -InstallDir $T.InstallDir -AllowedParent $T.ProgramFiles -DataDir $T.DataDir -Platform $partial -Confirm:$false } catch { $caught = $_.Exception }
    Assert-True ($null -ne $caught -and $caught.Message -like "*has no 'IsAdministrator' member*") "incomplete platform rejected: $caught"
}

# ============================================================== static checks of the scripts (parsed, never run)

function Get-ScriptAst([string]$Path) {
    $tokens = $null; $errors = $null
    $ast = [Management.Automation.Language.Parser]::ParseFile($Path, [ref]$tokens, [ref]$errors)
    if (@($errors).Count -gt 0) { throw "parse errors in ${Path}: $(@($errors | ForEach-Object { $_.Message }) -join '; ')" }
    return $ast
}

function Get-Commands($Ast) {
    , @($Ast.FindAll({ param($n) $n -is [Management.Automation.Language.CommandAst] }, $true))
}

Add-Case 'static-entry-scripts-and-module' {
    param($T)
    $tools = Join-Path $repo 'tools'
    $files = @{
        install = Join-Path $tools 'install.ps1'
        uninstall = Join-Path $tools 'uninstall.ps1'
        module = Join-Path $tools 'installation\UnifiedRgb.Installation.psm1'
    }
    $asts = @{}
    foreach ($k in @($files.Keys)) { $asts[$k] = Get-ScriptAst $files[$k] }
    # Public parameters are exactly the historical ones: no platform, timeout, force or skip switch.
    $paramsOf = { param($ast) (@($ast.ParamBlock.Parameters) | ForEach-Object { $_.Name.VariablePath.UserPath }) -join ',' }
    Assert-Equal 'Source,InstallDir,NoStart' (& $paramsOf $asts.install) 'install.ps1 parameters'
    Assert-Equal 'InstallDir,Purge' (& $paramsOf $asts.uninstall) 'uninstall.ps1 parameters'
    foreach ($k in 'install', 'uninstall') {
        $text = [IO.File]::ReadAllText($files[$k])
        Assert-True ($text -match 'CmdletBinding\(SupportsShouldProcess\)') "$k keeps SupportsShouldProcess"
        Assert-True ($text -match '\$platform = New-UrgbWindowsPlatform') "$k uses the production platform"
        Assert-True ($text -match "AllowedParent = \`$env:ProgramFiles") "$k pins the reviewed product parent"
        Assert-True ($text -match "Platform = \`$platform") "$k passes only the production platform"
        Assert-True ($text -notmatch 'StopTimeoutSeconds|New-Fake|Skip') "$k exposes no bypass"
    }
    # The module is called once, splatting a fixed argument set; only Confirm may be added (and only when bound).
    $expectedKeys = @{
        install = 'AllowedParent,DataDir,InstallDir,NoStart,Platform,Reporter,Source,WhatIf'
        uninstall = 'AllowedParent,DataDir,InstallDir,Platform,Reporter,WhatIf'
    }
    foreach ($k in 'install', 'uninstall') {
        $ast = $asts[$k]
        $assigns = @($ast.FindAll({ param($n) $n -is [Management.Automation.Language.AssignmentStatementAst] -and $n.Left.Extent.Text -eq '$arguments' }, $true))
        Assert-Equal 1 $assigns.Count "$k assigns `$arguments once"
        $table = @($assigns[0].Right.FindAll({ param($n) $n -is [Management.Automation.Language.HashtableAst] }, $true))[0]
        $keys = @($table.KeyValuePairs | ForEach-Object { $_.Item1.Extent.Text } | Sort-Object) -join ','
        Assert-Equal $expectedKeys[$k] $keys "$k module arguments"
        $memberSets = @($ast.FindAll({ param($n) $n -is [Management.Automation.Language.AssignmentStatementAst] -and $n.Left.Extent.Text -like '$arguments.*' }, $true))
        Assert-Equal '$arguments.Confirm' (@($memberSets | ForEach-Object { $_.Left.Extent.Text }) -join ',') "$k adds only Confirm"
        $verb = if ($k -eq 'install') { 'Invoke-UrgbInstall' } else { 'Invoke-UrgbUninstall' }
        $all = Get-Commands $ast
        $calls = @($all | Where-Object { $_.GetCommandName() -like 'Invoke-Urgb*' })
        Assert-Equal 1 $calls.Count "$k calls the module once"
        Assert-Equal "$verb @arguments" $calls[0].Extent.Text "$k module call"
    }
    # uninstall.ps1 refuses -Purge before anything else.
    $stmts = @($asts.uninstall.EndBlock.Statements)
    Assert-True ($stmts[0].Extent.Text -eq "`$ErrorActionPreference = 'Stop'") 'first statement'
    Assert-True ($stmts[1] -is [Management.Automation.Language.IfStatementAst] -and $stmts[1].Clauses[0].Item1.Extent.Text -eq '$Purge') 'second statement is the -Purge refusal'
    $purgeCheck = [scriptblock]::Create("`$Purge = `$true`n" + $stmts[1].Extent.Text)
    $caught = $null
    try { & $purgeCheck } catch { $caught = $_.Exception.Message }
    Assert-True ($caught -like 'Refused: -Purge*Nothing was changed.') "purge refusal text: $caught"
    # No deletion, kill, copy-over or broad system command anywhere.
    $forbidden = 'Remove-Item', 'Copy-Item', 'Move-Item', 'Rename-Item', 'Stop-Process', 'taskkill', 'taskkill.exe', 'schtasks', 'schtasks.exe', 'reg', 'reg.exe',
    'sc', 'sc.exe', 'icacls', 'takeown', 'Register-ScheduledTask', 'Set-ScheduledTask', 'Disable-ScheduledTask', 'Set-ExecutionPolicy', 'pnputil', 'Stop-Service', 'Set-Service'
    foreach ($k in @($asts.Keys)) {
        foreach ($c in (Get-Commands $asts[$k])) {
            $name = $c.GetCommandName()
            if ($name -and $forbidden -contains $name) { throw "Assertion failed: $k uses forbidden command '$name' at line $($c.Extent.StartLineNumber)" }
            foreach ($p in @($c.CommandElements | Where-Object { $_ -is [Management.Automation.Language.CommandParameterAst] })) {
                # Import-Module -Force only reloads the module; it is the one permitted -Force.
                if ($p.ParameterName -eq 'Force' -and $name -eq 'Import-Module') { continue }
                if ($p.ParameterName -in 'Recurse', 'Force') { throw "Assertion failed: $k passes -$($p.ParameterName) to '$name' at line $($p.Extent.StartLineNumber)" }
            }
        }
        foreach ($mem in @($asts[$k].FindAll({ param($n) $n -is [Management.Automation.Language.InvokeMemberExpressionAst] }, $true))) {
            $member = $mem.Member.Extent.Text
            if ($member -in 'Kill', 'DeleteSubKey', 'DeleteSubKeyTree', 'SetAccessControl') { throw "Assertion failed: $k calls .$member at line $($mem.Extent.StartLineNumber)" }
            if ($member -eq 'Delete' -and $mem.Expression.Extent.Text -match 'Directory' -and @($mem.Arguments).Count -ne 2) { throw "Assertion failed: $k Directory.Delete without explicit non-recursive flag at line $($mem.Extent.StartLineNumber)" }
            if ($member -eq 'Delete' -and $mem.Expression.Extent.Text -match 'Directory' -and $mem.Arguments[1].Extent.Text -ne '$false') { throw "Assertion failed: $k recursive Directory.Delete at line $($mem.Extent.StartLineNumber)" }
        }
    }
    $moduleCommands = Get-Commands $asts.module
    $only = { param([string]$Name) , @($moduleCommands | Where-Object { $_.GetCommandName() -eq $Name }) }
    # Maintenance verbs: exactly one launch, hidden and explicitly waited, exit code read from the process.
    $starts = & $only 'Start-Process'
    Assert-Equal 1 $starts.Count 'one Start-Process'
    $startText = $starts[0].Extent.Text
    Assert-True ($startText -match '-WindowStyle Hidden' -and $startText -match '-Wait\b' -and $startText -match '-PassThru') "maintenance launch hidden and waited: $startText"
    Assert-True ([IO.File]::ReadAllText($files.module) -match '\[int\]\$process\.ExitCode') 'exit code returned'
    # Scheduled task: only the exact root task name.
    foreach ($n in 'Get-ScheduledTask', 'Stop-ScheduledTask', 'Start-ScheduledTask', 'Unregister-ScheduledTask') {
        $uses = & $only $n
        Assert-Equal 1 $uses.Count "one $n"
        Assert-True ($uses[0].Extent.Text -match "-TaskPath '\\' -TaskName \`$script:TaskName") "$n addresses only '\UnifiedRgb Host': $($uses[0].Extent.Text)"
    }
    # Registry: only the single HKCU Run value.
    foreach ($n in 'Get-ItemProperty', 'Set-ItemProperty', 'Remove-ItemProperty') {
        $uses = & $only $n
        Assert-Equal 1 $uses.Count "one $n"
        Assert-True ($uses[0].Extent.Text -match '-LiteralPath \$script:RunKey -Name \$script:RunValueName') "$n addresses only the Run value: $($uses[0].Extent.Text)"
    }
    Assert-True ([IO.File]::ReadAllText($files.module) -match "RunKey = 'HKCU:\\Software\\Microsoft\\Windows\\CurrentVersion\\Run'") 'Run key is per-user'
    # ACLs: only on a data directory the platform has just created.
    $acl = & $only 'Set-Acl'
    Assert-Equal 1 $acl.Count 'one Set-Acl'
    $ensure = [regex]::Match([IO.File]::ReadAllText($files.module), '(?s)EnsureDataDirectory = \{.*?Set-Acl')
    Assert-True ($ensure.Success -and $ensure.Value -match "if \(Test-Path -LiteralPath \`$Path\) \{ return 'kept as it is' \}") 'Set-Acl only after the exists check'
    $exported = @((Get-Module UnifiedRgb.Installation).ExportedFunctions.Keys | Sort-Object) -join ','
    Assert-Equal 'Invoke-UrgbInstall,Invoke-UrgbUninstall,New-UrgbWindowsPlatform' $exported 'exported functions'
}

# ============================================================== F6-1 / F6-2: processes that appear around a switch, interrupted switches

# Simulates an interrupted switch: rewrites only the manifest's current/previous pointers (an empty id means null).
function Set-ManifestPointers($T, [string]$Current, [string]$Previous) {
    $path = Join-Path $T.InstallDir 'UnifiedRgb.install.json'
    $text = [IO.File]::ReadAllText($path, $script:Utf8)
    foreach ($field in 'current', 'previous') {
        $id = if ($field -eq 'current') { $Current } else { $Previous }
        $value = if ($id) { '"' + $id + '"' } else { 'null' }
        $re = [regex]::new('"' + $field + '":\s+(null|"[^"]*")')
        if ($re.Matches($text).Count -ne 1) { throw "manifest field '$field' not found exactly once" }
        $text = $re.Replace($text, ('"' + $field + '":  ' + $value))
    }
    [IO.File]::WriteAllText($path, $text, $script:Utf8)
}

Add-Case 'f6-1-a1-upgrade-switch-failure-while-staged-host-runs' {
    param($T)
    Add-UserData $T
    $r1 = Install-V1 $T
    $v1Host = Get-HostPath $T $r1.VersionId
    $v1Before = Get-Tree (Get-VersionDir $T $r1.VersionId)
    $src2 = New-PublishTree $T 'v2'
    $S = $T.Platform.State
    $S.Locks = [Collections.Generic.List[object]]::new()
    # The definition already points at the staged Host when a start request (the GUI's, for example) runs it, holding its
    # image; registration then reports failure.
    $S.OnRegister = {
        param($S, $exe)
        $S.Task = @{ Command = $exe; Arguments = ''; ActionCount = 1; State = 'Ready' }
        if ($exe -eq $v1Host) { return 0 }
        $S.NextPid++
        $S.Processes.Add([pscustomobject]@{ Id = $S.NextPid; Name = 'UnifiedRgb.Host'; Path = $exe; ByTask = $true })
        $S.Task.State = 'Running'
        $S.Locks.Add([IO.File]::Open($exe, 'Open', 'Read', 'Read'))
        return 5
    }.GetNewClosure()
    $S.OnStop = { param($S) foreach ($l in $S.Locks) { $l.Dispose() }; $S.Locks.Clear() }
    try {
        Reset-Calls $T
        $msg = Assert-Error { Install-Qa $T $src2 } 'Failed:' @('exited with 5', 'neither re-registered nor removed', 'an installed Host is running (PID', 'kept because something is running', 'no start of the scheduled task was requested', 'unresolved')
        $m = Get-Manifest $T
        $staged = @($m.versions | Where-Object { $_.id -ne $r1.VersionId })[0].id
        $stagedHost = Get-HostPath $T $staged
        $running = Get-Procs $T 'UnifiedRgb.Host'
        Assert-Equal 1 $running.Count 'only the staged Host runs'
        Assert-Equal $stagedHost $running[0].Path 'the staged Host is the one running'
        Assert-True ($msg.Contains("PID $($running[0].Id), $stagedHost")) "message names the running Host's PID and path: $msg"
        Assert-True ($msg.Contains("points at version $staged")) 'message states the actual task target'
        Assert-True ($msg.Contains("still records version $($r1.VersionId) as current")) 'message states the recorded version'
        Assert-Calls $T @('StopTask', 'EnsureData', "register|$stagedHost") 'no re-registration, removal or start around the running Host'
        Assert-Equal $stagedHost $S.Task.Command 'task left as it is'
        Assert-Equal $r1.VersionId $m.current 'the switch was not committed'
        Assert-True ($null -eq $m.previous) 'previous unchanged'
        Assert-Equal (Get-TreeNames $src2) (Get-TreeNames (Get-VersionDir $T $staged)) 'no staged file was deleted under the running Host'
        Assert-ManifestMatchesDisk $T @($staged)
        Assert-Equal $v1Before (Get-Tree (Get-VersionDir $T $r1.VersionId)) 'v1 unchanged'
        # Once it has settled, the next install reconciles the interrupted switch (F6-2) and recovers through the task.
        $S.OnRegister = $null
        Reset-Calls $T
        $r3 = Install-Qa $T (New-PublishTree $T 'v3')
        Assert-Equal 'Upgraded' $r3.Outcome 'recovered'
        Assert-Equal $staged $r3.PreviousVersionId 'upgraded from the version the task ran'
        Assert-Contains $r3.Warnings 'interrupted' 'reconciliation reported'
        $v3Host = Get-HostPath $T $r3.VersionId
        Assert-Calls $T @('StopTask', 'EnsureData', "register|$v3Host", 'SetRunValue', 'StartTask')
        $running = Get-Procs $T 'UnifiedRgb.Host'
        Assert-Equal 1 $running.Count 'one Host'
        Assert-Equal $v3Host $running[0].Path 'v3 runs'
        $m = Get-Manifest $T
        Assert-Equal $staged $m.previous 'the rollback target is the version that actually ran'
        Assert-True (-not [IO.Directory]::Exists((Get-VersionDir $T $r1.VersionId))) 'v1 pruned'
        Assert-ManifestMatchesDisk $T
    }
    finally { foreach ($l in $S.Locks) { $l.Dispose() } }
}

Add-Case 'f6-1-a3-first-install-switch-failure-while-staged-host-runs' {
    param($T)
    $S = $T.Platform.State
    $S.OnRegister = {
        param($S, $exe)
        $S.Task = @{ Command = $exe; Arguments = ''; ActionCount = 1; State = 'Running' }
        $S.NextPid++
        $S.Processes.Add([pscustomobject]@{ Id = $S.NextPid; Name = 'UnifiedRgb.Host'; Path = $exe; ByTask = $true })
        return 1
    }
    $src = New-PublishTree $T 'v1'
    $msg = Assert-Error { Install-Qa $T $src } 'Failed:' @('exited with 1', 'neither re-registered nor removed', 'the manifest records no active version', 'kept because something is running', 'no start of the scheduled task was requested')
    $m = Get-Manifest $T
    Assert-True ($null -eq $m.current -and $null -eq $m.previous) 'nothing recorded as active'
    Assert-Equal 1 @($m.versions).Count 'staged version listed'
    $staged = @($m.versions)[0].id
    $stagedHost = Get-HostPath $T $staged
    Assert-True ($msg.Contains("PID $((Get-Procs $T 'UnifiedRgb.Host')[0].Id), $stagedHost")) "message names the running staged Host: $msg"
    Assert-Calls $T @('EnsureData', "register|$stagedHost") 'the task is not removed around its running Host, and no start is requested'
    Assert-Equal $stagedHost $S.Task.Command 'task kept'
    Assert-True ($null -eq $S.Run) 'no tray entry'
    Assert-Equal (Get-Tree $src) (Get-Tree (Get-VersionDir $T $staged)) 'staged files intact'
    Assert-ManifestMatchesDisk $T
    # The owner enables hardware in the version that actually runs; the next install adopts it and keeps that file.
    $edited = [Text.UTF8Encoding]::new($false).GetBytes("{ `"Hardware`": { `"UseVirtualAdapter`": false, `"EnableLianLi`": true } }`r`n")
    [IO.File]::WriteAllBytes((Join-Path (Get-VersionDir $T $staged) 'appsettings.json'), $edited)
    $S.OnRegister = $null
    Reset-Calls $T
    $r2 = Install-Qa $T (New-PublishTree $T 'v2')
    Assert-Equal 'Upgraded' $r2.Outcome 'recovered as an upgrade of the adopted version'
    Assert-Equal $staged $r2.PreviousVersionId 'previous'
    Assert-Contains $r2.Warnings 'records no active version' 'reconciliation reported'
    Assert-Equal ([Convert]::ToBase64String($edited)) ([Convert]::ToBase64String([IO.File]::ReadAllBytes((Join-Path (Get-VersionDir $T $r2.VersionId) 'appsettings.json')))) 'edited configuration preserved, not source defaults'
    $v2Host = Get-HostPath $T $r2.VersionId
    Assert-Calls $T @('StopTask', 'EnsureData', "register|$v2Host", 'SetRunValue', 'StartTask')
    Assert-Equal 1 (Get-Procs $T 'UnifiedRgb.Host').Count 'one Host'
    Assert-Equal $v2Host (Get-Procs $T 'UnifiedRgb.Host')[0].Path 'v2 runs'
    $m = Get-Manifest $T
    Assert-Equal $r2.VersionId $m.current 'current'
    Assert-Equal $staged $m.previous 'previous'
    Assert-ManifestMatchesDisk $T @($r2.VersionId)
}

Add-Case 'f6-1-a5-host-started-after-stop-blocks-the-switch' {
    param($T)
    $r1 = Install-V1 $T
    $v1Host = Get-HostPath $T $r1.VersionId
    $v1Before = Get-Tree (Get-VersionDir $T $r1.VersionId)
    $src2 = New-PublishTree $T 'v2'
    $S = $T.Platform.State
    # After the confirmed exit, a start request runs the task's current (old) Host again before the switch.
    $S.OnEnsureData = {
        param($S, $Path)
        if (@($S.Processes | Where-Object { $_.ByTask }).Count -eq 0) {
            $S.NextPid++
            $S.Processes.Add([pscustomobject]@{ Id = $S.NextPid; Name = 'UnifiedRgb.Host'; Path = $S.Task.Command; ByTask = $true })
            $S.Task.State = 'Running'
        }
    }
    Reset-Calls $T
    $msg = Assert-Error { Install-Qa $T $src2 } 'Failed:' @('staged and verified but not activated', 'stays active and the task was not changed', 'kept because something is running', 'no start of the scheduled task was requested')
    $running = Get-Procs $T 'UnifiedRgb.Host'
    Assert-Equal 1 $running.Count 'the restarted old Host runs'
    Assert-True ($msg.Contains("PID $($running[0].Id), $v1Host")) "message names its PID and path: $msg"
    Assert-Calls $T @('StopTask', 'EnsureData') 'no registration and no start'
    Assert-Equal $v1Host $S.Task.Command 'task unchanged'
    $m = Get-Manifest $T
    Assert-Equal $r1.VersionId $m.current 'v1 still current'
    $staged = @($m.versions | Where-Object { $_.id -ne $r1.VersionId })[0].id
    Assert-Equal (Get-TreeNames $src2) (Get-TreeNames (Get-VersionDir $T $staged)) 'staged version kept complete'
    Assert-ManifestMatchesDisk $T @($staged)
    Assert-Equal $v1Before (Get-Tree (Get-VersionDir $T $r1.VersionId)) 'v1 unchanged'
    $S.OnEnsureData = $null
    $r3 = Install-Qa $T (New-PublishTree $T 'v3')
    Assert-Equal 'Upgraded' $r3.Outcome 'the next install proceeds'
    Assert-Equal $r1.VersionId $r3.PreviousVersionId 'from v1 (nothing to reconcile)'
    Assert-True (-not [IO.Directory]::Exists((Get-VersionDir $T $staged))) 'the never-activated staged version is pruned'
    Assert-True (@($r3.Warnings | Where-Object { $_ -like '*interrupted*' }).Count -eq 0) 'no interrupted-switch warning'
}

Add-Case 'f6-1-start-after-final-check-is-reported-not-hidden' {
    param($T)
    $r1 = Install-V1 $T
    $r2 = Install-Qa $T (New-PublishTree $T 'v2')
    $v2Host = Get-HostPath $T $r2.VersionId
    $S = $T.Platform.State
    # The remaining race (documented, not closed): a start request lands after the last check and before the definition
    # changes, so the previous Host runs while the task already points at the new one. The installer must not hide it,
    # prune under it or request or claim a start.
    $S.OnRegister = {
        param($S, $exe)
        $S.NextPid++
        $S.Processes.Add([pscustomobject]@{ Id = $S.NextPid; Name = 'UnifiedRgb.Host'; Path = $S.Task.Command; ByTask = $true })
        $S.Task = @{ Command = $exe; Arguments = ''; ActionCount = 1; State = 'Running' }
        return 0
    }
    Reset-Calls $T
    $r3 = Install-Qa $T (New-PublishTree $T 'v3')
    Assert-Equal 'Upgraded' $r3.Outcome 'the switch itself succeeded'
    $v3Host = Get-HostPath $T $r3.VersionId
    $running = Get-Procs $T 'UnifiedRgb.Host'
    Assert-Equal 1 $running.Count 'one Host runs'
    Assert-Equal $v2Host $running[0].Path 'it is the previous version, not the new one'
    Assert-Contains $r3.Warnings "PID $($running[0].Id), $v2Host" 'the running Host is named'
    Assert-Contains $r3.Warnings 'no start of the scheduled task was requested' 'no start requested or claimed'
    Assert-Contains $r3.Warnings 'were not pruned' 'pruning deferred'
    Assert-Calls $T @('StopTask', 'EnsureData', "register|$v3Host", 'SetRunValue') 'no start request'
    Assert-True ([IO.Directory]::Exists((Get-VersionDir $T $r1.VersionId))) 'v1 not pruned while something runs'
    Assert-True (@($r3.Log | Where-Object { $_ -like '*Requested a start*' }).Count -eq 0) 'the log does not claim a start'
    $m = Get-Manifest $T
    Assert-Equal $r3.VersionId $m.current 'v3 current'
    Assert-Equal $r2.VersionId $m.previous 'v2 previous'
    $S.OnRegister = $null
    $r4 = Install-Qa $T (New-PublishTree $T 'v4')
    Assert-Equal 'Upgraded' $r4.Outcome 'a later install proceeds'
    Assert-True (-not [IO.Directory]::Exists((Get-VersionDir $T $r1.VersionId))) 'and prunes'
}

Add-Case 'f6-1-prepare-failure-while-staged-host-runs' {
    param($T)
    $r1 = Install-V1 $T
    $v1Host = Get-HostPath $T $r1.VersionId
    $versions = Join-Path $T.InstallDir 'versions'
    $v1Id = $r1.VersionId
    $src2 = New-PublishTree $T 'v2'
    $S = $T.Platform.State
    $S.Locks = [Collections.Generic.List[object]]::new()
    # Something starts the staged Host directly (holding its image), then preparing the data directory fails.
    $S.OnEnsureData = {
        param($S, $Path)
        foreach ($d in [IO.Directory]::GetDirectories($versions)) {
            if ([IO.Path]::GetFileName($d) -ne $v1Id) {
                $exe = [IO.Path]::Combine($d, 'UnifiedRgb.Host.exe')
                $S.NextPid++
                $S.Processes.Add([pscustomobject]@{ Id = $S.NextPid; Name = 'UnifiedRgb.Host'; Path = $exe; ByTask = $false })
                $S.Locks.Add([IO.File]::Open($exe, 'Open', 'Read', 'Read'))
            }
        }
        throw 'fake: disk full'
    }.GetNewClosure()
    try {
        Reset-Calls $T
        $msg = Assert-Error { Install-Qa $T $src2 } 'Failed:' @('disk full', 'stays active', 'an installed Host is running (PID', 'kept because something is running', 'no start of the scheduled task was requested while that runs')
        $m = Get-Manifest $T
        $staged = @($m.versions | Where-Object { $_.id -ne $v1Id })[0].id
        $stagedHost = Get-HostPath $T $staged
        Assert-True ($msg.Contains($stagedHost)) "message names the running staged Host: $msg"
        Assert-Calls $T @('StopTask', 'EnsureData') 'no registration and no start'
        Assert-Equal $v1Id $m.current 'v1 current'
        Assert-Equal $v1Host $S.Task.Command 'task unchanged'
        Assert-Equal (Get-TreeNames $src2) (Get-TreeNames (Get-VersionDir $T $staged)) 'staged version kept complete'
        Assert-ManifestMatchesDisk $T @($staged)
    }
    finally { foreach ($l in $S.Locks) { $l.Dispose() }; $S.Locks.Clear() }
    $S.Processes.Clear()
    $S.OnEnsureData = $null
    $r3 = Install-Qa $T (New-PublishTree $T 'v3')
    Assert-Equal 'Upgraded' $r3.Outcome 'the next install proceeds'
    Assert-True (-not [IO.Directory]::Exists((Get-VersionDir $T $staged))) 'and prunes the kept staged version'
}

Add-Case 'f6-1-staging-failure-while-staged-host-runs' {
    param($T)
    $r1 = Install-V1 $T
    $v1Before = Get-Tree (Get-VersionDir $T $r1.VersionId)
    $src2 = New-PublishTree $T 'v2'
    $S = $T.Platform.State
    $holder = @{ Stream = $null; Dir = $null }
    $locked = Join-Path $src2 'urgb.exe'   # copied last, after the Host
    $reporter = {
        param($Line)
        if ($null -eq $holder.Stream -and $Line -match '^Staging version \S+ in (.+) \(\d+ files\)\.$') {
            $holder.Dir = $Matches[1]
            $S.NextPid++
            $S.Processes.Add([pscustomobject]@{ Id = $S.NextPid; Name = 'UnifiedRgb.Host'; Path = [IO.Path]::Combine($holder.Dir, 'UnifiedRgb.Host.exe'); ByTask = $false })
            $holder.Stream = [IO.File]::Open($locked, 'Open', 'Read', 'None')
        }
    }.GetNewClosure()
    try {
        Reset-Calls $T
        $msg = Assert-Error { Install-Qa $T $src2 @{ Reporter = $reporter } } 'Failed:' @('Staging version', 'an installed Host is running (PID', 'kept because something is running', 'were not touched')
    }
    finally { if ($null -ne $holder.Stream) { $holder.Stream.Dispose() } }
    Assert-True ($null -ne $holder.Dir) 'staging started'
    $stagedHost = [IO.Path]::Combine($holder.Dir, 'UnifiedRgb.Host.exe')
    Assert-True ($msg.Contains($stagedHost)) "message names the running path: $msg"
    Assert-Calls $T @() 'no stop, registration or data action'
    $stagedId = [IO.Path]::GetFileName($holder.Dir)
    $m = Get-Manifest $T
    Assert-Equal $r1.VersionId $m.current 'v1 current'
    Assert-Equal 1 @($m.versions | Where-Object { $_.id -eq $stagedId }).Count 'the staged version stays listed'
    $expected = @((Get-TreeNames $src2) -split "`n" | Where-Object { $_ -ne 'F appsettings.json' -and $_ -ne 'F urgb.exe' }) -join "`n"
    Assert-Equal $expected (Get-TreeNames $holder.Dir) 'every file copied before the failure is still there, including the running Host'
    Assert-Equal $v1Before (Get-Tree (Get-VersionDir $T $r1.VersionId)) 'v1 unchanged'
    $S.Processes.Clear()
    $r3 = Install-Qa $T (New-PublishTree $T 'v3')
    Assert-Equal 'Upgraded' $r3.Outcome 'the next install proceeds'
    Assert-True (-not [IO.Directory]::Exists($holder.Dir)) 'and prunes the partial staged version'
}

Add-Case 'f6-2-b4-interrupted-first-install-adopts-the-task-target' {
    param($T)
    Add-UserData $T
    $r1 = Install-V1 $T
    $v1dir = Get-VersionDir $T $r1.VersionId
    $edited = [Text.UTF8Encoding]::new($false).GetBytes("{ `"Hardware`": { `"UseVirtualAdapter`": false, `"EnableLianLi`": true }, `"Note`": `"owner`" }`r`n")
    [IO.File]::WriteAllBytes((Join-Path $v1dir 'appsettings.json'), $edited)
    Set-ManifestPointers $T $null $null
    $src2 = New-PublishTree $T 'v2'
    $before = Get-Tree $T.Box
    Reset-Calls $T
    $w = Install-Qa $T $src2 @{ WhatIf = $true }
    Assert-Equal 'WhatIf' $w.Outcome 'WhatIf'
    Assert-Equal $r1.VersionId $w.PreviousVersionId 'WhatIf reports an upgrade from the version the task runs'
    Assert-Contains $w.Warnings 'records no active version' 'WhatIf shows the reconciliation'
    Assert-Calls $T @() 'WhatIf: no platform action'
    Assert-Equal $before (Get-Tree $T.Box) 'WhatIf: nothing written, the manifest included'
    $r2 = Install-Qa $T $src2
    Assert-Equal 'Upgraded' $r2.Outcome 'an upgrade of the adopted version, not a first install'
    Assert-Equal $r1.VersionId $r2.PreviousVersionId 'previous'
    Assert-Contains $r2.Warnings 'interrupted' 'reported'
    $v2dir = Get-VersionDir $T $r2.VersionId
    Assert-Equal ([Convert]::ToBase64String($edited)) ([Convert]::ToBase64String([IO.File]::ReadAllBytes((Join-Path $v2dir 'appsettings.json')))) 'owner configuration preserved, source defaults not applied'
    $m = Get-Manifest $T
    Assert-Equal $r2.VersionId $m.current 'current'
    Assert-Equal $r1.VersionId $m.previous 'previous'
    Assert-Equal 'preserved-config' (@((Get-ManifestVersion $m $r2.VersionId).files | Where-Object { $_.path -eq 'appsettings.json' })[0]).kind 'kind'
    Assert-Calls $T @('StopTask', 'EnsureData', "register|$(Get-HostPath $T $r2.VersionId)", 'SetRunValue', 'StartTask')
}

Add-Case 'f6-2-b1-task-on-a-different-listed-version-is-adopted' {
    param($T)
    $r1 = Install-V1 $T
    $r2 = Install-Qa $T (New-PublishTree $T 'v2')
    $edited = [Text.UTF8Encoding]::new($false).GetBytes("{ `"Hardware`": { `"UseVirtualAdapter`": false }, `"Note`": `"edited in v2`" }`r`n")
    [IO.File]::WriteAllBytes((Join-Path (Get-VersionDir $T $r2.VersionId) 'appsettings.json'), $edited)
    Set-ManifestPointers $T $r1.VersionId $null   # the switch to v2 was not recorded
    Reset-Calls $T
    $r3 = Install-Qa $T (New-PublishTree $T 'v3')
    Assert-Equal 'Upgraded' $r3.Outcome 'upgraded'
    Assert-Equal $r2.VersionId $r3.PreviousVersionId 'from the version the task runs, not the recorded v1'
    Assert-Contains $r3.Warnings "records version $($r1.VersionId) as current" 'reported'
    Assert-Equal ([Convert]::ToBase64String($edited)) ([Convert]::ToBase64String([IO.File]::ReadAllBytes((Join-Path (Get-VersionDir $T $r3.VersionId) 'appsettings.json')))) 'v2 configuration preserved, not v1''s'
    $m = Get-Manifest $T
    Assert-Equal $r3.VersionId $m.current 'current'
    Assert-Equal $r2.VersionId $m.previous 'previous'
    Assert-True (-not [IO.Directory]::Exists((Get-VersionDir $T $r1.VersionId))) 'v1 pruned'
    Assert-Calls $T @('StopTask', 'EnsureData', "register|$(Get-HostPath $T $r3.VersionId)", 'SetRunValue', 'StartTask')
}

Add-Case 'f6-2-b2-rollback-goes-to-the-version-the-task-ran' {
    param($T)
    $r1 = Install-V1 $T
    $r2 = Install-Qa $T (New-PublishTree $T 'v2')
    $v1Host = Get-HostPath $T $r1.VersionId
    $v2Host = Get-HostPath $T $r2.VersionId
    Set-ManifestPointers $T $r1.VersionId $null
    $S = $T.Platform.State
    $S.OnRegister = { param($S, $exe) $S.Task = @{ Command = $exe; Arguments = ''; ActionCount = 1; State = 'Ready' }; if ([IO.File]::ReadAllText($exe).Contains('fake host v3')) { 5 } else { 0 } }
    Reset-Calls $T
    Assert-Error { Install-Qa $T (New-PublishTree $T 'v3') } 'Failed:' @('exited with 5', "re-registered to version $($r2.VersionId)", 'staged files removed', 'a start of the scheduled task was requested') | Out-Null
    $calls = @($S.Calls.ToArray())
    Assert-Equal "register|$v2Host" $calls[3] "the rollback re-registers v2: $($calls -join ';')"
    Assert-True (-not ($calls -contains "register|$v1Host")) 'v1 is never registered (no silent downgrade)'
    Assert-Equal 'StartTask' $calls[4] 'a start is requested after a proven rollback'
    Assert-Equal $v2Host $S.Task.Command 'task back on v2'
    $m = Get-Manifest $T
    Assert-Equal $r2.VersionId $m.current 'the adoption was persisted with the rollback'
    Assert-Equal $r1.VersionId $m.previous 'previous'
    Assert-Equal 2 @($m.versions).Count 'staged v3 removed'
    Assert-Equal $v2Host (Get-Procs $T 'UnifiedRgb.Host')[0].Path 'v2 runs'
}

Add-Case 'f6-2-unverifiable-task-target-is-refused' {
    param($T)
    $r1 = Install-V1 $T
    $r2 = Install-Qa $T (New-PublishTree $T 'v2')
    Set-ManifestPointers $T $r1.VersionId $null
    $v2dir = Get-VersionDir $T $r2.VersionId
    $src3 = New-PublishTree $T 'v3'
    $variants = @(
        @{ Name = 'modified program file'; File = 'UnifiedRgb.Core.dll'; Expect = 'UnifiedRgb.Core.dll is modified since installation' },
        @{ Name = 'missing program file'; File = 'urgb.exe'; Expect = 'urgb.exe is missing' },
        @{ Name = 'extra file'; File = 'injected.dll'; Expect = 'injected.dll is not part of the staged version' },
        @{ Name = 'missing configuration'; File = 'appsettings.json'; Expect = 'appsettings.json is missing' },
        @{ Name = 'modified Host'; File = 'UnifiedRgb.Host.exe'; Expect = 'UnifiedRgb.Host.exe is modified since installation' }
    )
    foreach ($v in $variants) {
        $path = Join-Path $v2dir $v.File
        $original = if ([IO.File]::Exists($path)) { [IO.File]::ReadAllBytes($path) } else { $null }
        if ($v.Name -like 'modified*') { [IO.File]::AppendAllText($path, ' patched') }
        elseif ($v.Name -like 'missing*') { [IO.File]::Delete($path) }
        else { Write-QaFile $path 'not staged' }
        $before = Get-Tree $T.Box
        Reset-Calls $T
        Assert-Error { Install-Qa $T $src3 } 'Refused:' @("runs version $($r2.VersionId)", "records version $($r1.VersionId) as current", 'cannot be verified', $v.Expect, 'not adopted', 'Nothing was changed') | Out-Null
        Assert-Error { Install-Qa $T $src3 @{ WhatIf = $true } } 'Refused:' @('cannot be verified') | Out-Null
        Assert-Calls $T @() "$($v.Name): no platform action"
        Assert-Equal $before (Get-Tree $T.Box) "$($v.Name): nothing changed"
        if ($null -ne $original) { [IO.File]::WriteAllBytes($path, $original) } else { [IO.File]::Delete($path) }
    }
    $r3 = Install-Qa $T $src3
    Assert-Equal 'Upgraded' $r3.Outcome 'adopted once exactly what was staged again'
    Assert-Equal $r2.VersionId $r3.PreviousVersionId 'from v2'
}

Add-Case 'f6-2-uninstall-restores-with-the-actual-task-version' {
    param($T)
    $one = Install-Qa $T (New-PublishTree $T 'one')
    $two = Install-Qa $T (New-PublishTree $T 'two')
    $manifest = Get-Manifest $T
    $manifest.current = $one.VersionId
    $manifest.previous = $null
    [IO.File]::WriteAllText((Join-Path $T.InstallDir 'UnifiedRgb.install.json'), (ConvertTo-Json -InputObject $manifest -Depth 8), $script:Utf8)
    $actualHost = Get-HostPath $T $two.VersionId
    Reset-Calls $T
    $result = Uninstall-Qa $T
    Assert-Equal 'Uninstalled' $result.Outcome 'uninstall outcome'
    $restores = @($T.Platform.State.Calls | Where-Object { $_ -like 'restore|*' })
    Assert-Equal 1 $restores.Count 'exactly one restore'
    Assert-Equal ('restore|' + $actualHost) $restores[0] 'the verified actual task target supplies restore configuration'
}

# ============================================================== runner

$results = [Collections.Generic.List[object]]::new()
# powershell.exe -File passes "-Case a,b" as one string; accept comma-separated names and reject unknown ones.
$selected = @(@($Case) | Where-Object { $_ } | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
foreach ($name in $selected) {
    if (-not @($script:Cases | Where-Object { $_.Name -eq $name }).Count) { throw "Unknown case '$name'." }
}
$Case = $selected
$index = 0
foreach ($c in $script:Cases) {
    $index++
    if ($Case -and $Case -notcontains $c.Name) { continue }
    $sandbox = 'c{0:d2}' -f $index
    $T = New-Sandbox $sandbox
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $status = 'Passed'; $detail = $null
    try { $null = & $c.Body $T }
    catch {
        if ($_.Exception.Message -like 'SKIP: *') { $status = 'Skipped'; $detail = $_.Exception.Message.Substring(6) }
        else { $status = 'Failed'; $detail = "$($_.Exception.Message)`n$($_.ScriptStackTrace)" }
    }
    $results.Add([pscustomobject]@{ Name = $c.Name; Sandbox = $sandbox; Status = $status; Milliseconds = $watch.ElapsedMilliseconds; Detail = $detail })
    Write-Host ('{0,-7} {1} {2} ({3} ms)' -f $status, $sandbox, $c.Name, $watch.ElapsedMilliseconds)
    if ($detail) { Write-Host ('        ' + ($detail -replace "`n", "`n        ")) }
}
if ($results.Count -eq 0) { throw 'No case was run.' }
$failed = @($results | Where-Object { $_.Status -eq 'Failed' }).Count
$skipped = @($results | Where-Object { $_.Status -eq 'Skipped' }).Count
$summary = [pscustomobject]@{
    PowerShell = $PSVersionTable.PSVersion.ToString()
    Edition = if ($PSVersionTable.ContainsKey('PSEdition')) { $PSVersionTable.PSEdition } else { 'Desktop' }
    RunRoot = $script:RunRoot
    Started = $script:RunStarted
    Total = $results.Count
    Passed = $results.Count - $failed - $skipped
    Failed = $failed
    Skipped = $skipped
    Cases = $results.ToArray()
}
[IO.File]::WriteAllText((Join-Path $script:RunRoot 'results.json'), (ConvertTo-Json -InputObject $summary -Depth 4), $script:Utf8)
Write-Host ("{0} passed, {1} failed, {2} skipped (PowerShell {3}); sandboxes and results.json in {4}" -f $summary.Passed, $failed, $skipped, $summary.PowerShell, $script:RunRoot)
if ($failed -gt 0) { exit 1 }
exit 0
