#Requires -Version 5.1
<#
.SYNOPSIS
  Ownership-checked install / upgrade / uninstall for Unified RGB Controller (audit F6). Used by tools/install.ps1
  and tools/uninstall.ps1.

.DESCRIPTION
  Layout under the install root (which must be a strict descendant of the reviewed product parent, by default
  %ProgramFiles%, because the scheduled task runs the Host with highest privileges):
    UnifiedRgb.install.json      owned-file manifest: canonical root, current/previous version, per-file SHA-256
    versions\<id>\...            one complete, verified program directory per version

  A new version is always copied into a new directory, verified, and only then activated by re-registering the
  task, so running binaries are never overwritten. The previous version is kept for rollback. Every operating
  system action (processes, the one scheduled task, the one HKCU Run value, maintenance verbs, the data directory,
  time) goes through a platform object. Offline tests supply a fake one; the safety checks below are not optional
  and have no bypass switch.
#>
Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

$script:ManifestName = 'UnifiedRgb.install.json'
$script:HostExe = 'UnifiedRgb.Host.exe'
$script:AppExe = 'UnifiedRgb.App.exe'
$script:ConfigName = 'appsettings.json'
$script:TaskName = 'UnifiedRgb Host'
$script:RunKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$script:RunValueName = 'UnifiedRgb'
$script:ProcessNames = @('UnifiedRgb.Host', 'UnifiedRgb.App', 'UnifiedRgb.Setup', 'urgb')
$script:FileKinds = @('program', 'config', 'preserved-config')
$script:VersionPattern = '^v-[0-9]{8}T[0-9]{6}-[0-9a-f]{8}$'
$script:MaxFiles = 20000
$script:MaxManifestBytes = 16MB
$script:Ignore = [StringComparison]::OrdinalIgnoreCase
$script:PlatformMembers = @('LockName', 'IsAdministrator', 'GetProcesses', 'GetTask', 'StopTask', 'StartTask', 'UnregisterTask',
    'InvokeMaintenance', 'GetRunValue', 'SetRunValue', 'RemoveRunValue', 'EnsureDataDirectory', 'Sleep', 'Now')

# ---------------------------------------------------------------- errors and reporting

function Stop-UrgbRefused([string]$Message) {
    throw [InvalidOperationException]::new("Refused: $Message Nothing was changed.")
}

function Stop-UrgbFailed([string]$Message) {
    throw [InvalidOperationException]::new("Failed: $Message")
}

function Get-UrgbInnermost([Exception]$Exception) {
    $e = $Exception
    while ($null -ne $e.InnerException) { $e = $e.InnerException }
    return $e
}

function New-UrgbContext($Platform, [scriptblock]$Reporter) {
    if ($null -eq $Platform) { throw [ArgumentNullException]::new('Platform') }
    foreach ($member in $script:PlatformMembers) {
        if ($null -eq $Platform.PSObject.Properties[$member]) { throw [ArgumentException]::new("The platform object has no '$member' member.") }
    }
    [pscustomobject]@{
        Platform = $Platform
        Reporter = $Reporter
        Log = [Collections.Generic.List[string]]::new()
        Warnings = [Collections.Generic.List[string]]::new()
        Kept = [Collections.Generic.List[string]]::new()
    }
}

function Write-UrgbLog($Ctx, [string]$Message) {
    $Ctx.Log.Add($Message)
    if ($null -ne $Ctx.Reporter) { & $Ctx.Reporter $Message }
}

function Add-UrgbWarning($Ctx, [string]$Message) {
    $Ctx.Warnings.Add($Message)
    Write-UrgbLog $Ctx "WARNING: $Message"
}

function New-UrgbResult($Ctx, $Layout, [string]$Outcome, $VersionId, $PreviousVersionId) {
    [pscustomobject]@{
        Outcome = $Outcome
        InstallRoot = $Layout.Root
        VersionId = $VersionId
        PreviousVersionId = $PreviousVersionId
        Warnings = $Ctx.Warnings.ToArray()
        Kept = $Ctx.Kept.ToArray()
        Log = $Ctx.Log.ToArray()
    }
}

# ---------------------------------------------------------------- paths

function Get-UrgbFullPath([string]$Path, [string]$What) {
    if ([string]::IsNullOrWhiteSpace($Path)) { Stop-UrgbRefused "$What is empty." }
    if ($Path -notmatch '^[A-Za-z]:[\\/]') { Stop-UrgbRefused "$What must be an absolute local drive path (not relative, UNC or a device path): '$Path'." }
    if ($Path.IndexOf(':', 2) -ge 0 -or $Path.IndexOfAny([char[]]'*?"<>|') -ge 0) { Stop-UrgbRefused "$What contains a stream or wildcard character: '$Path'." }
    try { $full = [IO.Path]::GetFullPath($Path) } catch { Stop-UrgbRefused "$What is not a valid path: '$Path'." }
    if ($full.Length -le 3) { return $full }
    return $full.TrimEnd('\')
}

function Test-UrgbUnder([string]$Child, [string]$Parent) {
    $prefix = $Parent.TrimEnd('\') + '\'
    return $Child.Length -gt $prefix.Length -and $Child.StartsWith($prefix, $script:Ignore)
}

function Test-UrgbSameOrUnder([string]$Child, [string]$Parent) {
    return [string]::Equals($Child, $Parent, $script:Ignore) -or (Test-UrgbUnder $Child $Parent)
}

function Get-UrgbItemAttributes([string]$Path) {
    try { return [IO.File]::GetAttributes($Path) }
    catch {
        $e = Get-UrgbInnermost $_.Exception
        if ($e -is [IO.FileNotFoundException] -or $e -is [IO.DirectoryNotFoundException]) { return $null }
        throw
    }
}

function Test-UrgbReparse($Attributes) { return $null -ne $Attributes -and ($Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 }

function Test-UrgbDirectory($Attributes) { return $null -ne $Attributes -and ($Attributes -band [IO.FileAttributes]::Directory) -ne 0 }

# Returns the first junction / symbolic link / other reparse point on the way from Path up to StopAt (inclusive;
# the drive root when StopAt is empty), or $null.
function Find-UrgbReparse([string]$Path, [string]$StopAt) {
    $p = $Path
    while (-not [string]::IsNullOrEmpty($p)) {
        if (Test-UrgbReparse (Get-UrgbItemAttributes $p)) { return $p }
        if ($StopAt -and [string]::Equals($p, $StopAt, $script:Ignore)) { return $null }
        $p = [IO.Path]::GetDirectoryName($p)
    }
    return $null
}

function Assert-UrgbNoReparse([string]$Path, [string]$What) {
    $link = Find-UrgbReparse $Path ''
    if ($link) { Stop-UrgbRefused "$What '$Path' goes through a junction, symbolic link or other reparse point ('$link')." }
}

# A manifest/source relative path: backslash separated, no rooting, streams, '.'/'..' segments or device names.
function ConvertTo-UrgbRelativePath([string]$Path) {
    if ([string]::IsNullOrEmpty($Path) -or $Path.Length -gt 400) { return $null }
    $r = $Path.Replace('/', '\')
    if ($r.StartsWith('\') -or $r.Contains(':')) { return $null }
    foreach ($segment in $r.Split('\')) {
        if ($segment.Length -eq 0 -or $segment -eq '.' -or $segment -eq '..') { return $null }
        if ($segment.EndsWith(' ') -or $segment.EndsWith('.')) { return $null }
        if ($segment.IndexOfAny([IO.Path]::GetInvalidFileNameChars()) -ge 0) { return $null }
        if ($segment -match '^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])(\..*)?$') { return $null }
    }
    return $r
}

function Join-UrgbOwnedPath([string]$Base, [string]$Relative) {
    $full = [IO.Path]::GetFullPath([IO.Path]::Combine($Base, $Relative))
    if (-not (Test-UrgbUnder $full $Base)) { throw [InvalidOperationException]::new("'$Relative' leaves '$Base'.") }
    return $full
}

function Get-UrgbFileSha256([string]$Path) {
    $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try {
        $sha = [Security.Cryptography.SHA256]::Create()
        try { return [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '').ToLowerInvariant() }
        finally { $sha.Dispose() }
    }
    finally { $stream.Dispose() }
}

function Get-UrgbBytesSha256([byte[]]$Bytes) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($sha.ComputeHash($Bytes)).Replace('-', '').ToLowerInvariant() }
    finally { $sha.Dispose() }
}

function Resolve-UrgbLayout([string]$InstallDir, [string]$AllowedParent, [string]$DataDir, [string]$Source) {
    $parent = Get-UrgbFullPath $AllowedParent 'The reviewed product parent directory'
    if ($parent.Length -le 3) { Stop-UrgbRefused "The reviewed product parent '$parent' is a drive root." }
    $root = Get-UrgbFullPath $InstallDir 'InstallDir'
    if ($root.Length -le 3) { Stop-UrgbRefused "InstallDir '$InstallDir' is a drive root." }
    if (-not (Test-UrgbUnder $root $parent)) {
        Stop-UrgbRefused "InstallDir '$root' is outside the reviewed product root '$parent'. The elevated Host must be installed in an administrator-only location."
    }
    $data = Get-UrgbFullPath $DataDir 'The data directory'
    if ((Test-UrgbSameOrUnder $root $data) -or (Test-UrgbSameOrUnder $data $root)) { Stop-UrgbRefused "InstallDir '$root' overlaps the data directory '$data'." }
    $src = $null
    if (-not [string]::IsNullOrEmpty($Source)) {
        $src = Get-UrgbFullPath $Source 'Source'
        if ((Test-UrgbSameOrUnder $src $root) -or (Test-UrgbSameOrUnder $root $src)) { Stop-UrgbRefused "Source '$src' overlaps InstallDir '$root'." }
        if ((Test-UrgbSameOrUnder $src $data) -or (Test-UrgbSameOrUnder $data $src)) { Stop-UrgbRefused "Source '$src' overlaps the data directory '$data'." }
        Assert-UrgbNoReparse $src 'Source'
        # Only an install may create the data directory, so only an install needs it to be a plain path.
        Assert-UrgbNoReparse $data 'The data directory'
        $dataAttributes = Get-UrgbItemAttributes $data
        if ($null -ne $dataAttributes -and -not (Test-UrgbDirectory $dataAttributes)) { Stop-UrgbRefused "The data directory '$data' is a file." }
    }
    Assert-UrgbNoReparse $root 'InstallDir'
    [pscustomobject]@{
        Parent = $parent
        Root = $root
        Versions = [IO.Path]::Combine($root, 'versions')
        Manifest = [IO.Path]::Combine($root, $script:ManifestName)
        Data = $data
        Source = $src
    }
}

# ---------------------------------------------------------------- manifest

function New-UrgbManifest($Layout) {
    @{ InstallRoot = $Layout.Root; Current = $null; Previous = $null; Versions = [Collections.Generic.List[object]]::new() }
}

function Get-UrgbJsonValue($Object, [string]$Name) {
    if ($null -eq $Object) { return $null }
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) { return $null }
    return , $property.Value
}

function Test-UrgbJsonInteger($Value) { return $Value -is [int] -or $Value -is [long] }

function Read-UrgbManifest($Layout) {
    $path = $Layout.Manifest
    $invalid = { param($why) Stop-UrgbRefused "The installation manifest '$path' is invalid ($why), so '$($Layout.Root)' is not treated as an owned installation." }
    if ([IO.FileInfo]::new($path).Length -gt $script:MaxManifestBytes) { & $invalid 'too large' }
    try {
        $text = [IO.File]::ReadAllText($path, [Text.UTF8Encoding]::new($false, $true))
        $doc = ConvertFrom-Json -InputObject $text
    }
    catch { & $invalid 'not readable UTF-8 JSON' }
    $schema = Get-UrgbJsonValue $doc 'schema'
    if (-not (Test-UrgbJsonInteger $schema) -or $schema -ne 1) { & $invalid 'unsupported schema' }
    $product = Get-UrgbJsonValue $doc 'product'
    if (-not ($product -is [string]) -or $product -cne 'UnifiedRgb') { & $invalid 'wrong product' }
    $recordedRoot = Get-UrgbJsonValue $doc 'installRoot'
    if (-not ($recordedRoot -is [string]) -or $recordedRoot -notmatch '^[A-Za-z]:\\' -or
        -not [string]::Equals([IO.Path]::GetFullPath($recordedRoot).TrimEnd('\'), $Layout.Root, $script:Ignore)) {
        & $invalid "it was written for a different directory ('$recordedRoot')"
    }
    $versionsJson = Get-UrgbJsonValue $doc 'versions'
    if (-not ($versionsJson -is [array])) { & $invalid 'versions is not a list' }
    $manifest = New-UrgbManifest $Layout
    $ids = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $fileCount = 0
    foreach ($v in $versionsJson) {
        $id = Get-UrgbJsonValue $v 'id'
        if (-not ($id -is [string]) -or $id -cnotmatch $script:VersionPattern -or -not $ids.Add($id)) { & $invalid 'bad or duplicate version id' }
        $filesJson = Get-UrgbJsonValue $v 'files'
        if (-not ($filesJson -is [array])) { & $invalid "files of $id is not a list" }
        $paths = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        $files = [Collections.Generic.List[object]]::new()
        foreach ($f in $filesJson) {
            $rel = Get-UrgbJsonValue $f 'path'
            $length = Get-UrgbJsonValue $f 'length'
            $sha = Get-UrgbJsonValue $f 'sha256'
            $kind = Get-UrgbJsonValue $f 'kind'
            if (-not ($rel -is [string]) -or (ConvertTo-UrgbRelativePath $rel) -cne $rel -or -not $paths.Add($rel)) { & $invalid "unsafe or duplicate file path in $id" }
            if (-not (Test-UrgbJsonInteger $length) -or $length -lt 0) { & $invalid "bad length in $id" }
            if (-not ($sha -is [string]) -or $sha -cnotmatch '^[0-9a-f]{64}$') { & $invalid "bad sha256 in $id" }
            if (-not ($kind -is [string]) -or $script:FileKinds -cnotcontains $kind) { & $invalid "bad kind in $id" }
            if ($kind -ne 'program' -and $rel -ne $script:ConfigName) { & $invalid "configuration kind on '$rel' in $id" }
            $files.Add(@{ Path = $rel; Length = [long]$length; Sha256 = $sha; Kind = $kind })
            $fileCount++
        }
        if ($fileCount -gt $script:MaxFiles * 4) { & $invalid 'too many files' }
        $manifest.Versions.Add(@{ Id = $id; Files = $files })
    }
    foreach ($name in 'current', 'previous') {
        $value = Get-UrgbJsonValue $doc $name
        if ($null -ne $value -and (-not ($value -is [string]) -or -not $ids.Contains($value))) { & $invalid "$name names no listed version" }
        if ($name -eq 'current') { $manifest.Current = $value } else { $manifest.Previous = $value }
    }
    if ($null -ne $manifest.Previous -and $manifest.Previous -eq $manifest.Current) { & $invalid 'previous equals current' }
    return $manifest
}

function Write-UrgbManifest($Layout, $Manifest) {
    $versions = [Collections.Generic.List[object]]::new()
    foreach ($v in $Manifest.Versions) {
        $files = [Collections.Generic.List[object]]::new()
        foreach ($f in $v.Files) { $files.Add([ordered]@{ path = $f.Path; length = [long]$f.Length; sha256 = $f.Sha256; kind = $f.Kind }) }
        $versions.Add([ordered]@{ id = $v.Id; files = $files.ToArray() })
    }
    $doc = [ordered]@{
        schema = 1; product = 'UnifiedRgb'; installRoot = $Manifest.InstallRoot
        current = $Manifest.Current; previous = $Manifest.Previous; versions = $versions.ToArray()
    }
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes((ConvertTo-Json -InputObject $doc -Depth 8))
    if (Test-UrgbReparse (Get-UrgbItemAttributes $Layout.Manifest)) { throw [InvalidOperationException]::new('The manifest became a reparse point.') }
    $temp = [IO.Path]::Combine($Layout.Root, "$($script:ManifestName).$([guid]::NewGuid().ToString('N')).tmp")
    try {
        Write-UrgbNewFile $temp $bytes
        if ($null -ne (Get-UrgbItemAttributes $Layout.Manifest)) { [IO.File]::Replace($temp, $Layout.Manifest, [NullString]::Value) }
        else { [IO.File]::Move($temp, $Layout.Manifest) }
    }
    finally {
        # Only this call's uniquely named temporary file; the manifest itself is never deleted here.
        try { if ([IO.File]::Exists($temp)) { [IO.File]::Delete($temp) } } catch { }
    }
}

function Write-UrgbNewFile([string]$Path, [byte[]]$Bytes) {
    $stream = [IO.File]::Open($Path, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try { $stream.Write($Bytes, 0, $Bytes.Length); $stream.Flush($true) }
    finally { $stream.Dispose() }
}

function Get-UrgbVersion($Manifest, [string]$Id) {
    foreach ($v in $Manifest.Versions) { if ($v.Id -eq $Id) { return $v } }
    return $null
}

function Get-UrgbVersionDir($Layout, [string]$Id) { return [IO.Path]::Combine($Layout.Versions, $Id) }

function Get-UrgbVersionHost($Layout, [string]$Id) { return [IO.Path]::Combine((Get-UrgbVersionDir $Layout $Id), $script:HostExe) }

function Get-UrgbOwnedHosts($Layout, $Manifest) {
    $hosts = [Collections.Generic.List[string]]::new()
    foreach ($v in $Manifest.Versions) { $hosts.Add((Get-UrgbVersionHost $Layout $v.Id)) }
    return , $hosts.ToArray()
}

# Absent, Empty or Owned. Anything else is refused: a file, a reparse point, an unrelated non-empty directory or an
# invalid manifest. A directory that merely contains UnifiedRgb.Host.exe is not treated as owned.
function Get-UrgbInstallState($Layout) {
    $attributes = Get-UrgbItemAttributes $Layout.Root
    if ($null -eq $attributes) { return [pscustomobject]@{ Kind = 'Absent'; Manifest = $null } }
    if (Test-UrgbReparse $attributes) { Stop-UrgbRefused "InstallDir '$($Layout.Root)' is a junction, symbolic link or other reparse point." }
    if (-not (Test-UrgbDirectory $attributes)) { Stop-UrgbRefused "InstallDir '$($Layout.Root)' is a file." }
    if (@([IO.DirectoryInfo]::new($Layout.Root).GetFileSystemInfos()).Count -eq 0) { return [pscustomobject]@{ Kind = 'Empty'; Manifest = $null } }
    $manifestAttributes = Get-UrgbItemAttributes $Layout.Manifest
    if ($null -eq $manifestAttributes) {
        $hint = ''
        if ($null -ne (Get-UrgbItemAttributes ([IO.Path]::Combine($Layout.Root, $script:HostExe)))) {
            $hint = ' It contains UnifiedRgb.Host.exe but no installation manifest, as left by the earlier unmanaged installer; such directories are not adopted or deleted automatically.'
        }
        Stop-UrgbRefused "InstallDir '$($Layout.Root)' is a non-empty directory that is not a manifest-owned UnifiedRgb installation.$hint"
    }
    if ((Test-UrgbReparse $manifestAttributes) -or (Test-UrgbDirectory $manifestAttributes)) { Stop-UrgbRefused "The installation manifest '$($Layout.Manifest)' is not a regular file." }
    $manifest = Read-UrgbManifest $Layout
    $versionsAttributes = Get-UrgbItemAttributes $Layout.Versions
    if ($null -ne $versionsAttributes -and ((Test-UrgbReparse $versionsAttributes) -or -not (Test-UrgbDirectory $versionsAttributes))) {
        Stop-UrgbRefused "'$($Layout.Versions)' is not a plain directory."
    }
    foreach ($v in $manifest.Versions) {
        $a = Get-UrgbItemAttributes (Get-UrgbVersionDir $Layout $v.Id)
        if ($null -ne $a -and ((Test-UrgbReparse $a) -or -not (Test-UrgbDirectory $a))) { Stop-UrgbRefused "Version directory '$(Get-UrgbVersionDir $Layout $v.Id)' is not a plain directory." }
    }
    return [pscustomobject]@{ Kind = 'Owned'; Manifest = $manifest }
}

# ---------------------------------------------------------------- owned files

function Get-UrgbFileProblem($Layout, [string]$VersionDir, $Entry) {
    $path = Join-UrgbOwnedPath $VersionDir $Entry.Path
    $link = Find-UrgbReparse $path $Layout.Root
    if ($link) { return "goes through the reparse point '$link'" }
    $attributes = Get-UrgbItemAttributes $path
    if ($null -eq $attributes) { return 'missing' }
    if (Test-UrgbDirectory $attributes) { return 'a directory' }
    if ([IO.FileInfo]::new($path).Length -ne $Entry.Length -or (Get-UrgbFileSha256 $path) -ne $Entry.Sha256) { return 'modified since installation' }
    return $null
}

function Get-UrgbHostProblem($Layout, $Version) {
    foreach ($f in $Version.Files) {
        if ($f.Path -eq $script:HostExe) { return Get-UrgbFileProblem $Layout (Get-UrgbVersionDir $Layout $Version.Id) $f }
    }
    return 'not listed in the manifest'
}

# All listed files present and unchanged; with -Exact the directory must also contain nothing else and no links.
# -AllowEditedConfig accepts locally edited configuration bytes; the file must still exist as a plain listed file.
function Get-UrgbVersionProblems($Layout, $Version, [switch]$Exact, [switch]$AllowEditedConfig) {
    $dir = Get-UrgbVersionDir $Layout $Version.Id
    $problems = [Collections.Generic.List[string]]::new()
    $listed = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($f in $Version.Files) {
        [void]$listed.Add($f.Path)
        $problem = Get-UrgbFileProblem $Layout $dir $f
        if ($AllowEditedConfig -and $f.Kind -ne 'program' -and $problem -eq 'modified since installation') { $problem = $null }
        if ($problem) { $problems.Add("$($f.Path) is $problem") }
    }
    if ($Exact) {
        $stack = [Collections.Generic.Stack[string]]::new()
        $stack.Push($dir)
        while ($stack.Count -gt 0) {
            foreach ($e in [IO.DirectoryInfo]::new($stack.Pop()).GetFileSystemInfos()) {
                $rel = $e.FullName.Substring($dir.Length + 1)
                if (($e.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { $problems.Add("$rel is a reparse point") }
                elseif (($e.Attributes -band [IO.FileAttributes]::Directory) -ne 0) { $stack.Push($e.FullName) }
                elseif (-not $listed.Contains($rel)) { $problems.Add("$rel is not part of the staged version") }
            }
        }
    }
    return , $problems.ToArray()
}

# Deletes one manifest-owned file only if it is still exactly what was installed. Returns $null when the file is gone.
# KeepSettings keeps carried-over configuration (uninstall); pruning and undoing a stage may delete it because the
# active version holds a verified copy of the same bytes.
function Remove-UrgbOwnedFile($Layout, [string]$VersionDir, $Entry, [bool]$KeepSettings) {
    $path = Join-UrgbOwnedPath $VersionDir $Entry.Path
    $link = Find-UrgbReparse $path $Layout.Root
    if ($link) { return "kept: goes through the reparse point '$link'" }
    $attributes = Get-UrgbItemAttributes $path
    if ($null -eq $attributes) { return $null }
    if (Test-UrgbDirectory $attributes) { return 'kept: it is now a directory' }
    if ($KeepSettings -and $Entry.Kind -eq 'preserved-config') { return 'kept: configuration carried over from an earlier version (it may contain local settings)' }
    if ([IO.FileInfo]::new($path).Length -ne $Entry.Length -or (Get-UrgbFileSha256 $path) -ne $Entry.Sha256) { return 'kept: modified since installation' }
    if (($attributes -band [IO.FileAttributes]::ReadOnly) -ne 0) { [IO.File]::SetAttributes($path, $attributes -band -bnot [IO.FileAttributes]::ReadOnly) }
    [IO.File]::Delete($path)
    return $null
}

# Removes a directory only when it is a plain, empty directory strictly inside the install root (never recursive).
function Remove-UrgbEmptyDirectory($Layout, [string]$Path) {
    if (-not (Test-UrgbUnder $Path $Layout.Root) -and -not [string]::Equals($Path, $Layout.Root, $script:Ignore)) { return $false }
    $attributes = Get-UrgbItemAttributes $Path
    if ($null -eq $attributes) { return $true }
    if ((Test-UrgbReparse $attributes) -or -not (Test-UrgbDirectory $attributes)) { return $false }
    if (@([IO.DirectoryInfo]::new($Path).GetFileSystemInfos()).Count -ne 0) { return $false }
    try { [IO.Directory]::Delete($Path, $false); return $true }
    catch { return $false }
}

# Deletes the unchanged owned files of one version and the then-empty directories they imply. Keeps everything
# else and reports it. Updates the version's file list; drops the version from the manifest once nothing owned is left.
function Remove-UrgbOwnedVersion($Ctx, $Layout, $Manifest, $Version, [bool]$KeepSettings) {
    $dir = Get-UrgbVersionDir $Layout $Version.Id
    $remaining = [Collections.Generic.List[object]]::new()
    $directories = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($f in $Version.Files) {
        try { $verdict = Remove-UrgbOwnedFile $Layout $dir $f $KeepSettings }
        catch { $verdict = "kept: could not be checked or deleted ($($_.Exception.Message))" }
        if ($verdict) { $remaining.Add($f); $Ctx.Kept.Add("$(Join-UrgbOwnedPath $dir $f.Path) ($verdict)") }
        $parent = [IO.Path]::GetDirectoryName((Join-UrgbOwnedPath $dir $f.Path))
        while ((Test-UrgbUnder $parent $dir)) { [void]$directories.Add($parent); $parent = [IO.Path]::GetDirectoryName($parent) }
    }
    foreach ($d in @($directories | Sort-Object -Property Length -Descending)) { [void](Remove-UrgbEmptyDirectory $Layout $d) }
    $dirGone = Remove-UrgbEmptyDirectory $Layout $dir
    $Version.Files = $remaining
    if (-not $dirGone) { $Ctx.Kept.Add("$dir\ (not removed: it still contains files that were kept or not installed by UnifiedRgb)") }
    if ($remaining.Count -eq 0) { [void]$Manifest.Versions.Remove($Version) }
}

# ---------------------------------------------------------------- task, Run value, processes

function ConvertTo-UrgbExecutablePath([string]$Value) {
    if ([string]::IsNullOrWhiteSpace($Value)) { return $null }
    $v = $Value.Trim()
    if ($v.StartsWith('"')) {
        $end = $v.IndexOf('"', 1)
        if ($end -le 1) { return $null }
        $v = $v.Substring(1, $end - 1)
    }
    if ($v -notmatch '^[A-Za-z]:\\' -or $v.IndexOf(':', 2) -ge 0) { return $null }
    try { return [IO.Path]::GetFullPath($v) } catch { return $null }
}

function Get-UrgbTaskReport($Ctx, [string[]]$OwnedHosts) {
    $task = & $Ctx.Platform.GetTask
    if ($null -eq $task) { return [pscustomobject]@{ Kind = 'Absent'; Host = $null; Command = $null; State = $null } }
    $command = ConvertTo-UrgbExecutablePath ([string]$task.Command)
    $owned = $null
    if ($null -ne $command -and $task.ActionCount -eq 1 -and [string]::IsNullOrWhiteSpace([string]$task.Arguments)) {
        foreach ($h in $OwnedHosts) { if ([string]::Equals($h, $command, $script:Ignore)) { $owned = $h } }
    }
    $kind = if ($null -ne $owned) { 'Owned' } else { 'Foreign' }
    return [pscustomobject]@{ Kind = $kind; Host = $owned; Command = [string]$task.Command; State = [string]$task.State }
}

function Get-UrgbRunReport($Ctx, $Layout) {
    $value = & $Ctx.Platform.GetRunValue
    if ([string]::IsNullOrWhiteSpace([string]$value)) { return [pscustomobject]@{ Kind = 'Absent'; Value = $null } }
    $exe = ConvertTo-UrgbExecutablePath ([string]$value)
    $owned = $null -ne $exe -and (Test-UrgbUnder $exe $Layout.Root) -and [string]::Equals([IO.Path]::GetFileName($exe), $script:AppExe, $script:Ignore)
    $kind = if ($owned) { 'Owned' } else { 'Foreign' }
    return [pscustomobject]@{ Kind = $kind; Value = [string]$value }
}

# Blockers: any GUI, CLI or Host this installation does not own (or whose path cannot be read), and anything else
# running from inside the install root. Owned Hosts are returned separately; they may only be stopped via the task.
function Get-UrgbProcessReport($Ctx, $Layout, [string[]]$OwnedHosts) {
    $blockers = [Collections.Generic.List[string]]::new()
    $owned = [Collections.Generic.List[string]]::new()
    foreach ($p in @(& $Ctx.Platform.GetProcesses)) {
        $name = [string]$p.Name
        $path = $null
        if (-not [string]::IsNullOrWhiteSpace([string]$p.Path)) { $path = ConvertTo-UrgbExecutablePath ([string]$p.Path) }
        $ours = $script:ProcessNames -contains $name
        $inside = $null -ne $path -and (Test-UrgbUnder $path $Layout.Root)
        if (-not $ours -and -not $inside) { continue }
        $shown = if ($null -ne $path) { $path } else { 'path unavailable' }
        if ($name -eq 'UnifiedRgb.Host' -and $null -ne $path -and @($OwnedHosts | Where-Object { [string]::Equals($_, $path, $script:Ignore) }).Count -gt 0) {
            $owned.Add("PID $($p.Id), $shown")
            continue
        }
        $why = if ($name -eq 'UnifiedRgb.App') { 'the UnifiedRgb GUI is running; close it from its tray menu first so an unsaved draft is not discarded' }
        elseif ($name -eq 'UnifiedRgb.Host' -and $null -eq $path) { 'a UnifiedRgb Host is running whose executable path cannot be read, so its ownership cannot be proven' }
        elseif ($name -eq 'UnifiedRgb.Host') { 'a UnifiedRgb Host that this installation does not own is running' }
        elseif ($name -eq 'urgb') { 'the UnifiedRgb command-line tool is running' }
        else { 'a program is running from the installation directory' }
        $blockers.Add("$why (PID $($p.Id), $shown)")
    }
    [pscustomobject]@{ Blockers = $blockers.ToArray(); OwnedHosts = $owned.ToArray() }
}

function Assert-UrgbNoBlockers($Report) {
    if ($Report.Blockers.Count -gt 0) { Stop-UrgbRefused ('Cannot continue: ' + ($Report.Blockers -join '; ') + '.') }
}

# Waits until no owned Host runs. Never kills anything: returns Exited, Timeout or Blocked (a GUI or unowned
# process appeared). The caller must not replace, restore or delete anything unless the status is Exited.
function Wait-UrgbOwnedExit($Ctx, $Layout, [string[]]$OwnedHosts, [int]$TimeoutSeconds) {
    $deadline = (& $Ctx.Platform.Now).AddSeconds($TimeoutSeconds)
    while ($true) {
        $report = Get-UrgbProcessReport $Ctx $Layout $OwnedHosts
        if ($report.Blockers.Count -gt 0) { return [pscustomobject]@{ Status = 'Blocked'; Detail = ($report.Blockers -join '; ') } }
        if ($report.OwnedHosts.Count -eq 0) { return [pscustomobject]@{ Status = 'Exited'; Detail = $null } }
        if ((& $Ctx.Platform.Now) -ge $deadline) {
            return [pscustomobject]@{ Status = 'Timeout'; Detail = "the installed Host did not exit within $TimeoutSeconds s ($($report.OwnedHosts -join '; '))" }
        }
        & $Ctx.Platform.Sleep 250
    }
}

function Enter-UrgbLock($Ctx) {
    $mutex = [Threading.Mutex]::new($false, [string]$Ctx.Platform.LockName)
    $acquired = $false
    try { $acquired = $mutex.WaitOne(0) }
    catch {
        if ((Get-UrgbInnermost $_.Exception) -is [Threading.AbandonedMutexException]) { $acquired = $true }
        else { $mutex.Dispose(); throw }
    }
    if (-not $acquired) { $mutex.Dispose(); Stop-UrgbRefused 'Another UnifiedRgb install or uninstall is running.' }
    return $mutex
}

function Exit-UrgbLock($Mutex) {
    if ($null -eq $Mutex) { return }
    try { $Mutex.ReleaseMutex() } finally { $Mutex.Dispose() }
}

# ---------------------------------------------------------------- source

function Get-UrgbSourceInventory([string]$Source) {
    $attributes = Get-UrgbItemAttributes $Source
    if (-not (Test-UrgbDirectory $attributes)) { Stop-UrgbRefused "Source '$Source' is not a directory." }
    $files = [Collections.Generic.List[object]]::new()
    $stack = [Collections.Generic.Stack[string]]::new()
    $stack.Push($Source)
    while ($stack.Count -gt 0) {
        foreach ($e in [IO.DirectoryInfo]::new($stack.Pop()).GetFileSystemInfos()) {
            if (($e.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { Stop-UrgbRefused "Source contains a junction, symbolic link or other reparse point: '$($e.FullName)'." }
            if (($e.Attributes -band [IO.FileAttributes]::Directory) -ne 0) { $stack.Push($e.FullName); continue }
            $rel = ConvertTo-UrgbRelativePath $e.FullName.Substring($Source.Length + 1)
            if ($null -eq $rel) { Stop-UrgbRefused "Source contains an unsupported file name: '$($e.FullName)'." }
            $kind = if ($rel -eq $script:ConfigName) { 'config' } else { 'program' }
            $files.Add(@{ Path = $rel; Source = $e.FullName; Length = [long]$e.Length; Sha256 = (Get-UrgbFileSha256 $e.FullName); Kind = $kind })
            if ($files.Count -gt $script:MaxFiles) { Stop-UrgbRefused "Source has more than $($script:MaxFiles) files." }
        }
    }
    if (@($files | Where-Object { $_.Path -eq $script:HostExe }).Count -ne 1) { Stop-UrgbRefused "$($script:HostExe) not found in Source '$Source'." }
    return , @($files | Sort-Object -Property { $_.Path })
}

# ---------------------------------------------------------------- install

function Undo-UrgbStage($Ctx, $Layout, $Manifest, $Version, [bool]$CreatedRoot) {
    Remove-UrgbOwnedVersion $Ctx $Layout $Manifest $Version $false
    if ($Manifest.Versions.Count -eq 0 -and $null -eq $Manifest.Current) {
        if ($null -ne (Get-UrgbItemAttributes $Layout.Manifest)) { [IO.File]::Delete($Layout.Manifest) }
        [void](Remove-UrgbEmptyDirectory $Layout $Layout.Versions)
        if ($CreatedRoot) { [void](Remove-UrgbEmptyDirectory $Layout $Layout.Root) }
    }
    else { Write-UrgbManifest $Layout $Manifest }
}

function Undo-UrgbStageQuietly($Ctx, $Layout, $Manifest, $Version, [bool]$CreatedRoot) {
    try {
        Undo-UrgbStage $Ctx $Layout $Manifest $Version $CreatedRoot
        # Files that could not be deleted (modified or in use) stay listed, so a later run can still account for them.
        if ($Manifest.Versions.Contains($Version)) { return "staged files could only partly be removed ($($Version.Files.Count) kept and still listed in the manifest)" }
        return 'staged files removed'
    }
    catch { return "removing staged files also failed ($($_.Exception.Message))" }
}

# Keeps a staged version listed (not active) instead of deleting files that something may run from or that the task
# may point at. A later successful install prunes it; uninstall removes its unchanged files.
function Save-UrgbKeptStage($Layout, $Manifest, [string]$Id, [string]$Why) {
    $kept = "the staged version $Id was kept because $Why; it stays listed in the manifest (not active) so that a later install or uninstall can remove its unchanged files"
    try { Write-UrgbManifest $Layout $Manifest; return $kept }
    catch { return "$kept, but the manifest could not be rewritten ($($_.Exception.Message))" }
}

# Re-reads the processes just before a step that assumes nothing of this installation runs (switching the task,
# deleting staged files, requesting a start). Returns $null, or a description (PID and path) of every installed Host
# that is not in $Expected (owned Hosts already known to be running) and of every blocker. A process list that cannot
# be read counts as something running.
function Get-UrgbAppeared($Ctx, $Layout, $Manifest, [string[]]$Expected) {
    try { $report = Get-UrgbProcessReport $Ctx $Layout (Get-UrgbOwnedHosts $Layout $Manifest) }
    catch { return "the process list could not be read ($($_.Exception.Message)), so nothing can be proven not to run" }
    $found = [Collections.Generic.List[string]]::new()
    foreach ($h in $report.OwnedHosts) {
        if (@($Expected | Where-Object { [string]::Equals($_, $h, $script:Ignore) }).Count -eq 0) { $found.Add("an installed Host is running ($h)") }
    }
    foreach ($b in $report.Blockers) { $found.Add($b) }
    if ($found.Count -eq 0) { return $null }
    return ($found -join '; ')
}

# What the task points at now, for messages that must not guess.
function Get-UrgbTaskTarget($Ctx, $Layout, $Manifest) {
    try { $t = Get-UrgbTaskReport $Ctx (Get-UrgbOwnedHosts $Layout $Manifest) }
    catch { return "the scheduled task could not be read ($($_.Exception.Message))" }
    if ($t.Kind -eq 'Absent') { return 'no scheduled task is registered' }
    if ($t.Kind -eq 'Foreign') { return "the scheduled task runs '$($t.Command)'" }
    foreach ($v in $Manifest.Versions) {
        if ([string]::Equals((Get-UrgbVersionHost $Layout $v.Id), $t.Host, $script:Ignore)) { return "the scheduled task points at version $($v.Id) ($($t.Host))" }
    }
    return "the scheduled task points at '$($t.Host)'"
}

# A start request only asks the task scheduler. It does not prove which Host runs afterwards: the task ignores a start
# while an instance runs, and a second Host refuses to run.
function Start-UrgbTaskQuietly($Ctx, [string]$Why) {
    try {
        $null = & $Ctx.Platform.StartTask
        Write-UrgbLog $Ctx "Requested a start of the scheduled task ($Why); the request alone does not prove which Host runs."
        return 'a start of the scheduled task was requested (this alone does not prove which Host runs)'
    }
    catch { Add-UrgbWarning $Ctx "Requesting a start of the scheduled task failed: $($_.Exception.Message)"; return 'requesting a start of the scheduled task failed' }
}

# Puts the task back to the previously active version after a failed switch. Never runs an unverified Host.
function Undo-UrgbSwitch($Ctx, $Layout, $Manifest, $OldVersion, [string]$NewHost) {
    $hosts = Get-UrgbOwnedHosts $Layout $Manifest
    $task = Get-UrgbTaskReport $Ctx $hosts
    if ($null -ne $OldVersion) {
        $oldHost = Get-UrgbVersionHost $Layout $OldVersion.Id
        if ($task.Kind -eq 'Owned' -and [string]::Equals($task.Host, $oldHost, $script:Ignore)) {
            return [pscustomobject]@{ Restored = $true; Summary = "the task still runs version $($OldVersion.Id)" }
        }
        $problem = Get-UrgbHostProblem $Layout $OldVersion
        if ($problem) { return [pscustomobject]@{ Restored = $false; Summary = "the previous Host is $problem, so the task was not re-registered to it" } }
        try {
            $code = & $Ctx.Platform.InvokeMaintenance $oldHost '--register-task'
            $after = Get-UrgbTaskReport $Ctx @($oldHost)
            if ($code -eq 0 -and $after.Kind -eq 'Owned') { return [pscustomobject]@{ Restored = $true; Summary = "the task was re-registered to version $($OldVersion.Id)" } }
            return [pscustomobject]@{ Restored = $false; Summary = "re-registering version $($OldVersion.Id) also failed (exit $code)" }
        }
        catch { return [pscustomobject]@{ Restored = $false; Summary = "re-registering version $($OldVersion.Id) also failed ($($_.Exception.Message))" } }
    }
    if ($task.Kind -eq 'Owned' -and [string]::Equals($task.Host, $NewHost, $script:Ignore)) {
        try {
            $null = & $Ctx.Platform.UnregisterTask
            if ((Get-UrgbTaskReport $Ctx $hosts).Kind -eq 'Absent') { return [pscustomobject]@{ Restored = $true; Summary = 'the partially registered task was removed' } }
        }
        catch { }
        return [pscustomobject]@{ Restored = $false; Summary = 'the partially registered task could not be removed' }
    }
    if ($task.Kind -eq 'Absent') { return [pscustomobject]@{ Restored = $true; Summary = 'no task was registered' } }
    return [pscustomobject]@{ Restored = $false; Summary = "the task now runs '$($task.Command)' and was left unchanged" }
}

function Invoke-UrgbInstallCore($Ctx, $Layout, $Cmdlet, [bool]$NoStart, [int]$StopTimeoutSeconds) {
    # ---- inspect ownership; nothing is changed before ShouldProcess ----
    $sourceFiles = Get-UrgbSourceInventory $Layout.Source
    $state = Get-UrgbInstallState $Layout
    $manifest = if ($state.Kind -eq 'Owned') { $state.Manifest } else { New-UrgbManifest $Layout }
    $currentId = $manifest.Current
    $previousId = $manifest.Previous
    $current = if ($null -ne $currentId) { Get-UrgbVersion $manifest $currentId } else { $null }
    $ownedHosts = Get-UrgbOwnedHosts $Layout $manifest
    $task = Get-UrgbTaskReport $Ctx $ownedHosts
    if ($task.Kind -eq 'Foreign') {
        Stop-UrgbRefused "The scheduled task '\$($script:TaskName)' runs '$($task.Command)', which this installation does not own (another copy or a development build?). It was left unchanged."
    }
    # ---- an interrupted switch: the task runs a listed version that the manifest does not record as current ----
    # The configuration to preserve, the upgrade decision and the rollback target all follow the version the task
    # actually runs. It is adopted (in memory; the manifest records it only after ShouldProcess, with the first write
    # below) when it is still exactly what was staged, local configuration edits allowed. Otherwise nothing is changed,
    # so its configuration is never replaced with older bytes or source defaults.
    if ($task.Kind -eq 'Owned' -and ($null -eq $currentId -or -not [string]::Equals($task.Host, (Get-UrgbVersionHost $Layout $currentId), $script:Ignore))) {
        $actual = $null
        foreach ($v in $manifest.Versions) { if ([string]::Equals((Get-UrgbVersionHost $Layout $v.Id), $task.Host, $script:Ignore)) { $actual = $v } }
        $recorded = if ($null -ne $currentId) { "records version $currentId as current" } else { 'records no active version' }
        $problems = [Collections.Generic.List[string]]::new()
        $hostProblem = Get-UrgbHostProblem $Layout $actual
        if ($hostProblem) { $problems.Add("$($script:HostExe) is $hostProblem") }
        else {
            try { foreach ($p in (Get-UrgbVersionProblems $Layout $actual -Exact -AllowEditedConfig)) { $problems.Add($p) } }
            catch { $problems.Add("its files could not be checked ($($_.Exception.Message))") }
        }
        if ($problems.Count -gt 0) {
            Stop-UrgbRefused "The scheduled task runs version $($actual.Id), but the manifest $recorded, so an earlier switch was interrupted. Version $($actual.Id) cannot be verified ($($problems -join '; ')), so it is not adopted and its configuration is not replaced with older bytes or source defaults. Restore its files, or uninstall, and then install again."
        }
        Add-UrgbWarning $Ctx "The scheduled task runs version $($actual.Id), but the manifest ${recorded}: an earlier switch was interrupted. Version $($actual.Id) was verified and is treated as the installed version, so its appsettings.json is the one preserved. The manifest records this only if the operation proceeds."
        if ($null -ne $currentId) { $previousId = $currentId }
        elseif ($previousId -eq $actual.Id) { $previousId = $null }
        $currentId = $actual.Id
        $current = $actual
        $manifest.Current = $currentId
        $manifest.Previous = $previousId
    }
    $run = Get-UrgbRunReport $Ctx $Layout
    $processes = Get-UrgbProcessReport $Ctx $Layout $ownedHosts
    Assert-UrgbNoBlockers $processes
    if ($processes.OwnedHosts.Count -gt 0 -and $task.Kind -ne 'Owned') {
        Stop-UrgbRefused "The installed Host is running without the installed scheduled task ($($processes.OwnedHosts -join '; ')); close it first."
    }
    $installedConfig = $null
    if ($null -ne $current) {
        $path = Join-UrgbOwnedPath (Get-UrgbVersionDir $Layout $current.Id) $script:ConfigName
        $attributes = Get-UrgbItemAttributes $path
        if ($null -ne $attributes) {
            if ((Test-UrgbReparse $attributes) -or (Test-UrgbDirectory $attributes)) { Stop-UrgbRefused "The installed '$path' is not a regular file." }
            $installedConfig = $path
        }
    }
    $upgrade = $null -ne $current
    $id = 'v-' + [DateTime]::UtcNow.ToString('yyyyMMddTHHmmss') + '-' + [guid]::NewGuid().ToString('N').Substring(0, 8)
    $versionDir = Get-UrgbVersionDir $Layout $id
    $action = if ($upgrade) { "Upgrade from version $currentId to $id" } else { "Install version $id" }
    if (-not $Cmdlet.ShouldProcess($Layout.Root, $action)) { return New-UrgbResult $Ctx $Layout 'WhatIf' $null $currentId }
    $ConfirmPreference = 'None'   # the whole operation was confirmed once; its individual steps do not ask again

    # ---- stage: a new directory only, so nothing that may be running is touched ----
    $createdRoot = $state.Kind -eq 'Absent'
    if ($null -ne (Get-UrgbItemAttributes $versionDir)) { Stop-UrgbRefused "'$versionDir' already exists." }
    $files = [Collections.Generic.List[object]]::new()
    foreach ($f in $sourceFiles) {
        if ($upgrade -and $f.Kind -eq 'config') { continue }   # upgrades keep the installed bytes instead (below)
        $files.Add(@{ Path = $f.Path; Length = $f.Length; Sha256 = $f.Sha256; Kind = $f.Kind })
    }
    $version = @{ Id = $id; Files = $files }
    $manifest.Versions.Add($version)
    Write-UrgbLog $Ctx "Staging version $id in $versionDir ($($files.Count) files)."
    try {
        # The version is listed before any of its files exist, so an interrupted stage can still be cleaned up.
        [void][IO.Directory]::CreateDirectory($Layout.Versions)
        Assert-UrgbNoReparse $Layout.Versions 'The versions directory'
        Write-UrgbManifest $Layout $manifest
        [void][IO.Directory]::CreateDirectory($versionDir)
        foreach ($f in $sourceFiles) {
            if ($upgrade -and $f.Kind -eq 'config') { continue }
            $target = Join-UrgbOwnedPath $versionDir $f.Path
            [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target))
            [IO.File]::Copy($f.Source, $target, $false)
        }
        $problems = Get-UrgbVersionProblems $Layout $version -Exact
        if ($problems.Count -gt 0) { throw [InvalidOperationException]::new('staged files failed verification: ' + ($problems -join '; ')) }
    }
    catch {
        $why = $_.Exception.Message
        # Owned Hosts that were already running are expected; anything else that runs now may use the staged files.
        $live = Get-UrgbAppeared $Ctx $Layout $manifest $processes.OwnedHosts
        $undo = if ($live) { Save-UrgbKeptStage $Layout $manifest $id "something is running: $live" } else { Undo-UrgbStageQuietly $Ctx $Layout $manifest $version $createdRoot }
        Stop-UrgbFailed "Staging version $id failed ($why); $undo. The installed version and the Host were not touched."
    }

    # ---- stop only the matching task and confirm every owned Host has exited ----
    $wasRunning = $processes.OwnedHosts.Count -gt 0
    $stopRequested = $false
    $wait = $null
    try {
        if ($task.Kind -eq 'Owned') {
            Write-UrgbLog $Ctx "Stopping scheduled task '\$($script:TaskName)'."
            $null = & $Ctx.Platform.StopTask
            $stopRequested = $true
        }
        $wait = Wait-UrgbOwnedExit $Ctx $Layout $ownedHosts $StopTimeoutSeconds
    }
    catch { $wait = [pscustomobject]@{ Status = 'Error'; Detail = $_.Exception.Message } }
    if ($wait.Status -ne 'Exited') {
        # Owned Hosts that were running before and simply did not exit are expected here; anything else has appeared.
        $live = Get-UrgbAppeared $Ctx $Layout $manifest $processes.OwnedHosts
        $restart = ''
        if ($live) {
            $undo = Save-UrgbKeptStage $Layout $manifest $id "something is running: $live"
            if ($wait.Status -ne 'Timeout' -and $stopRequested -and $wasRunning) { $restart = '; no start of the scheduled task was requested while that runs, so the installed Host may now be stopped' }
        }
        else {
            $undo = Undo-UrgbStageQuietly $Ctx $Layout $manifest $version $createdRoot
            if ($wait.Status -ne 'Timeout' -and $stopRequested -and $wasRunning) { $restart = '; ' + (Start-UrgbTaskQuietly $Ctx 'install aborted') }
        }
        Stop-UrgbFailed "Cannot replace the installed version: $($wait.Detail). No program files were replaced and nothing was killed; $undo$restart."
    }

    # ---- configuration, data directory and a final full verification, still before the switch ----
    try {
        if ($upgrade) {
            if ($null -ne $installedConfig) {
                $bytes = [IO.File]::ReadAllBytes($installedConfig)
                $target = Join-UrgbOwnedPath $versionDir $script:ConfigName
                Write-UrgbNewFile $target $bytes
                if ([Convert]::ToBase64String([IO.File]::ReadAllBytes($target)) -cne [Convert]::ToBase64String($bytes)) { throw [InvalidOperationException]::new('the preserved appsettings.json does not match the installed bytes') }
                $version.Files.Add(@{ Path = $script:ConfigName; Length = [long]$bytes.Length; Sha256 = (Get-UrgbBytesSha256 $bytes); Kind = 'preserved-config' })
                Write-UrgbManifest $Layout $manifest
                Write-UrgbLog $Ctx "Kept the installed appsettings.json byte for byte ($($bytes.Length) bytes); the new source defaults were not applied."
            }
            else { Add-UrgbWarning $Ctx 'The installed version had no appsettings.json, so none was added; source defaults are only used on a first install.' }
        }
        $data = & $Ctx.Platform.EnsureDataDirectory $Layout.Data
        Write-UrgbLog $Ctx "Data directory '$($Layout.Data)': $data."
        $problems = Get-UrgbVersionProblems $Layout $version -Exact
        if ($problems.Count -gt 0) { throw [InvalidOperationException]::new('staged files failed final verification: ' + ($problems -join '; ')) }
    }
    catch {
        $why = $_.Exception.Message
        $active = if ($upgrade) { "version $currentId stays active" } else { 'nothing was activated' }
        # Every owned Host was confirmed to have exited, so anything running now has appeared since.
        $live = Get-UrgbAppeared $Ctx $Layout $manifest @()
        if ($live) {
            $undo = Save-UrgbKeptStage $Layout $manifest $id "something is running: $live"
            $restart = if ($wasRunning) { '; no start of the scheduled task was requested while that runs' } else { '' }
        }
        else {
            $undo = Undo-UrgbStageQuietly $Ctx $Layout $manifest $version $createdRoot
            $restart = if ($wasRunning) { '; ' + (Start-UrgbTaskQuietly $Ctx 'install rolled back') } else { '' }
        }
        Stop-UrgbFailed "Preparing version $id failed ($why); $active; $undo$restart."
    }

    # ---- switch: register the task for the staged Host; roll back on any failure ----
    $newHost = Get-UrgbVersionHost $Layout $id
    # Every owned Host was confirmed to have exited. If anything has started since (the task's triggers, a start
    # requested by the GUI, a manual start), switching could leave the task and the running Host on different versions.
    # A start between this check and the registration below is not prevented (see the remaining race in F6-FIX-RESULT.md).
    $live = Get-UrgbAppeared $Ctx $Layout $manifest @()
    if ($live) {
        $active = if ($upgrade) { "Version $currentId stays active and the task was not changed" } else { 'Nothing was activated and no task was registered' }
        $undo = Save-UrgbKeptStage $Layout $manifest $id "something is running: $live"
        Stop-UrgbFailed "Version $id was staged and verified but not activated, because something started after the check that no installed Host runs. $active; $undo; no start of the scheduled task was requested. Run install.ps1 again once it has exited."
    }
    try {
        Write-UrgbLog $Ctx "Registering scheduled task '\$($script:TaskName)' for $newHost."
        $code = & $Ctx.Platform.InvokeMaintenance $newHost '--register-task'
        if ($code -ne 0) { throw [InvalidOperationException]::new("task registration exited with $code") }
        if ((Get-UrgbTaskReport $Ctx @($newHost)).Kind -ne 'Owned') { throw [InvalidOperationException]::new('after registration the task does not run the staged Host') }
        $manifest.Previous = $currentId
        $manifest.Current = $id
        Write-UrgbManifest $Layout $manifest
    }
    catch {
        $why = $_.Exception.Message
        $manifest.Current = $currentId
        $manifest.Previous = $previousId
        $live = Get-UrgbAppeared $Ctx $Layout $manifest @()
        if ($live) {
            # Something runs, possibly the staged Host started by the registration. Re-registering or removing the task
            # around it, or deleting files under it, could leave the task and the running Host on different versions:
            # leave both as they are and report exactly what is known.
            $target = Get-UrgbTaskTarget $Ctx $Layout $manifest
            $recorded = if ($null -ne $currentId) { "the manifest still records version $currentId as current" } else { 'the manifest records no active version' }
            $undo = Save-UrgbKeptStage $Layout $manifest $id "something is running: $live"
            Stop-UrgbFailed "Switching to version $id failed ($why). The task was neither re-registered nor removed, because something is running; $target; $recorded; $undo; no start of the scheduled task was requested, and which Host should run is unresolved. Run install.ps1 again once it has settled; it reconciles an interrupted switch."
        }
        $rollback = Undo-UrgbSwitch $Ctx $Layout $manifest $current $newHost
        $after = Get-UrgbTaskReport $Ctx @($newHost)
        # Read again: the rollback itself may have started something.
        $live = Get-UrgbAppeared $Ctx $Layout $manifest @()
        $restart = ''
        if ($live) {
            $undo = Save-UrgbKeptStage $Layout $manifest $id "something is running: $live"
            if ($wasRunning) { $restart = '; no start of the scheduled task was requested while that runs' }
        }
        elseif (-not $rollback.Restored) {
            # An unproven rollback is not cleaned up after, and deleting the staged Host would leave a task that points at
            # it pointing at nothing: keep it listed (not active) instead.
            $reason = if ($after.Kind -eq 'Owned') { 'the task still points at it' } else { 'the rollback could not be proven' }
            $undo = Save-UrgbKeptStage $Layout $manifest $id $reason
        }
        else {
            $undo = Undo-UrgbStageQuietly $Ctx $Layout $manifest $version $createdRoot
            if ($wasRunning) { $restart = '; ' + (Start-UrgbTaskQuietly $Ctx 'switch rolled back') }
        }
        Stop-UrgbFailed "Switching to version $id failed ($why). Rollback: $($rollback.Summary); $undo$restart."
    }
    Write-UrgbLog $Ctx "Version $id is active."
    # Registration never starts the Host, so anything running now was started after the last check.
    $live = Get-UrgbAppeared $Ctx $Layout $manifest @()

    # ---- after the switch: tray entry, prune, start ----
    $app = [IO.Path]::Combine($versionDir, $script:AppExe)
    if ($null -ne (Get-UrgbItemAttributes $app)) {
        if ($run.Kind -eq 'Foreign') { Add-UrgbWarning $Ctx "The HKCU Run value '$($script:RunValueName)' points to '$($run.Value)', which this installation does not own; it was left unchanged." }
        else {
            try { $null = & $Ctx.Platform.SetRunValue "`"$app`" --minimized"; Write-UrgbLog $Ctx 'Updated the HKCU Run tray entry.' }
            catch { Add-UrgbWarning $Ctx "Could not update the HKCU Run tray entry: $($_.Exception.Message)" }
        }
    }
    $stale = @($manifest.Versions | Where-Object { $_.Id -ne $manifest.Current -and $_.Id -ne $manifest.Previous })
    if ($live -and $stale.Count -gt 0) { Add-UrgbWarning $Ctx "Older versions were not pruned because something is running ($live); a later install prunes them." }
    elseif (-not $live) {
        foreach ($v in $stale) {
            Write-UrgbLog $Ctx "Removing unchanged files of old version $($v.Id)."
            try { Remove-UrgbOwnedVersion $Ctx $Layout $manifest $v $false }
            catch { Add-UrgbWarning $Ctx "Old version $($v.Id) could not be removed completely and stays listed for a later attempt: $($_.Exception.Message)" }
        }
    }
    Write-UrgbManifest $Layout $manifest
    foreach ($k in $Ctx.Kept) { Add-UrgbWarning $Ctx "Kept $k" }
    if ($NoStart) { Write-UrgbLog $Ctx 'Not starting the Host (-NoStart).' }
    elseif ($live) {
        Add-UrgbWarning $Ctx "Version $id is active, but something is running ($live), so no start of the scheduled task was requested: the task ignores a start while an instance runs, and a request would not prove which Host runs. Once it has exited, start the task (or sign out and in) so that version $id runs."
    }
    else {
        try { $null = & $Ctx.Platform.StartTask; Write-UrgbLog $Ctx "Requested a start of the scheduled task; the request alone does not prove that version $id runs." }
        catch { Add-UrgbWarning $Ctx "Version $id is installed but requesting a start of the task failed: $($_.Exception.Message)" }
    }
    $outcome = if ($upgrade) { 'Upgraded' } else { 'Installed' }
    return New-UrgbResult $Ctx $Layout $outcome $id $currentId
}

<#
.SYNOPSIS
  Installs a published build into a new verified version directory, or upgrades an owned installation.
#>
function Invoke-UrgbInstall {
    [CmdletBinding(SupportsShouldProcess)]
    param(
        [Parameter(Mandatory)] [string] $Source,
        [Parameter(Mandatory)] [string] $InstallDir,
        [Parameter(Mandatory)] [string] $AllowedParent,
        [Parameter(Mandatory)] [string] $DataDir,
        [Parameter(Mandatory)] $Platform,
        [switch] $NoStart,
        [ValidateRange(1, 600)] [int] $StopTimeoutSeconds = 15,
        [scriptblock] $Reporter
    )
    $ctx = New-UrgbContext $Platform $Reporter
    $layout = Resolve-UrgbLayout $InstallDir $AllowedParent $DataDir $Source
    $lock = Enter-UrgbLock $ctx
    try { return Invoke-UrgbInstallCore $ctx $layout $PSCmdlet ([bool]$NoStart) $StopTimeoutSeconds }
    finally { Exit-UrgbLock $lock }
}

# ---------------------------------------------------------------- uninstall

function Invoke-UrgbUninstallCore($Ctx, $Layout, $Cmdlet, [int]$StopTimeoutSeconds) {
    $state = Get-UrgbInstallState $Layout
    if ($state.Kind -ne 'Owned') {
        $task = & $Ctx.Platform.GetTask
        if ($null -ne $task) { Add-UrgbWarning $Ctx "The scheduled task '\$($script:TaskName)' exists ('$($task.Command)') but no installation is at '$($Layout.Root)'; it was left unchanged." }
        Write-UrgbLog $Ctx "No UnifiedRgb installation at '$($Layout.Root)'; nothing was changed."
        return New-UrgbResult $Ctx $Layout 'NotInstalled' $null $null
    }
    $manifest = $state.Manifest
    $ownedHosts = Get-UrgbOwnedHosts $Layout $manifest
    $task = Get-UrgbTaskReport $Ctx $ownedHosts
    if ($task.Kind -eq 'Foreign') {
        Stop-UrgbRefused "The scheduled task '\$($script:TaskName)' runs '$($task.Command)', which this installation does not own; vendor services are not restored under another installation."
    }
    $run = Get-UrgbRunReport $Ctx $Layout
    $processes = Get-UrgbProcessReport $Ctx $Layout $ownedHosts
    Assert-UrgbNoBlockers $processes
    if ($processes.OwnedHosts.Count -gt 0 -and $task.Kind -ne 'Owned') {
        Stop-UrgbRefused "The installed Host is running without the installed scheduled task ($($processes.OwnedHosts -join '; ')); close it first."
    }
    # The Host that restores services stopped by Takeover must be present and exactly the installed binary.
    $restoreVersion = $null
    if ($task.Kind -eq 'Owned') {
        foreach ($v in $manifest.Versions) { if ([string]::Equals((Get-UrgbVersionHost $Layout $v.Id), $task.Host, $script:Ignore)) { $restoreVersion = $v } }
    }
    elseif ($null -ne $manifest.Current) { $restoreVersion = Get-UrgbVersion $manifest $manifest.Current }
    if ($null -ne $restoreVersion) {
        $problem = Get-UrgbHostProblem $Layout $restoreVersion
        if ($problem) {
            Stop-UrgbRefused "The installed Host '$(Get-UrgbVersionHost $Layout $restoreVersion.Id)' is $problem, so vendor services stopped by Takeover cannot be restored with a verified binary, and a missing Host does not permit deleting the installation. Repair it by running install.ps1 with a published build, then uninstall again."
        }
    }
    if (-not $Cmdlet.ShouldProcess($Layout.Root, 'Uninstall UnifiedRgb (owned, unchanged files only)')) { return New-UrgbResult $Ctx $Layout 'WhatIf' $manifest.Current $null }
    $ConfirmPreference = 'None'

    $wasRunning = $processes.OwnedHosts.Count -gt 0
    $stopRequested = $false
    $wait = $null
    try {
        if ($task.Kind -eq 'Owned') {
            Write-UrgbLog $Ctx "Stopping scheduled task '\$($script:TaskName)'."
            $null = & $Ctx.Platform.StopTask
            $stopRequested = $true
        }
        $wait = Wait-UrgbOwnedExit $Ctx $Layout $ownedHosts $StopTimeoutSeconds
    }
    catch { $wait = [pscustomobject]@{ Status = 'Error'; Detail = $_.Exception.Message } }
    if ($wait.Status -ne 'Exited') {
        $restart = ''
        if ($wait.Status -ne 'Timeout' -and $stopRequested -and $wasRunning) {
            $live = Get-UrgbAppeared $Ctx $Layout $manifest $processes.OwnedHosts
            if ($live) { $restart = " No start of the scheduled task was requested because something is running ($live); the installed Host may now be stopped." }
            else { $restart = ' Afterwards ' + (Start-UrgbTaskQuietly $Ctx 'uninstall aborted') + '.' }
        }
        Stop-UrgbFailed "Cannot uninstall: $($wait.Detail). No vendor services were restored, nothing was deleted and nothing was killed.$restart"
    }

    $restoreOk = $true
    if ($null -ne $restoreVersion) {
        $hostPath = Get-UrgbVersionHost $Layout $restoreVersion.Id
        Write-UrgbLog $Ctx "Restoring vendor services stopped by Takeover ($hostPath --restore-services)."
        $code = & $Ctx.Platform.InvokeMaintenance $hostPath '--restore-services'
        if ($code -ne 0) {
            $restoreOk = $false
            Add-UrgbWarning $Ctx "Restoring vendor services exited with $code. takeover.json in '$($Layout.Data)' and all program files are kept so the restore can be re-run by uninstalling again."
        }
    }
    else { Write-UrgbLog $Ctx 'No version of this installation was activated; no vendor-service restore is needed.' }

    if ($task.Kind -eq 'Owned') {
        $why = $null
        try { $null = & $Ctx.Platform.UnregisterTask } catch { $why = $_.Exception.Message }
        if ((Get-UrgbTaskReport $Ctx $ownedHosts).Kind -ne 'Absent') {
            $restored = if ($restoreOk) { 'Vendor services were restored' } else { 'Restoring vendor services also failed' }
            Stop-UrgbFailed "The scheduled task '\$($script:TaskName)' could not be unregistered ($why). $restored; all program files were kept because the task still points at them."
        }
        Write-UrgbLog $Ctx "Unregistered scheduled task '\$($script:TaskName)'."
    }
    if ($run.Kind -eq 'Owned') {
        try { $null = & $Ctx.Platform.RemoveRunValue; Write-UrgbLog $Ctx 'Removed the HKCU Run tray entry.' }
        catch { Add-UrgbWarning $Ctx "Could not remove the HKCU Run tray entry: $($_.Exception.Message)" }
    }
    elseif ($run.Kind -eq 'Foreign') { Add-UrgbWarning $Ctx "The HKCU Run value '$($script:RunValueName)' points to '$($run.Value)', which this installation does not own; it was left unchanged." }
    if (-not $restoreOk) { return New-UrgbResult $Ctx $Layout 'RestoreFailed' $manifest.Current $null }

    foreach ($v in @($manifest.Versions)) { Remove-UrgbOwnedVersion $Ctx $Layout $manifest $v $true }
    if ($manifest.Versions.Count -eq 0) {
        [IO.File]::Delete($Layout.Manifest)
        [void](Remove-UrgbEmptyDirectory $Layout $Layout.Versions)
        if (-not (Remove-UrgbEmptyDirectory $Layout $Layout.Root)) { $Ctx.Kept.Add("$($Layout.Root)\ (not removed: it contains files not installed by UnifiedRgb)") }
    }
    else {
        # Kept owned files stay listed (with no active version), so a later install or uninstall can still account for them.
        $manifest.Current = $null
        $manifest.Previous = $null
        Write-UrgbManifest $Layout $manifest
        $Ctx.Kept.Add("$($Layout.Manifest) (lists the kept files)")
    }
    foreach ($k in $Ctx.Kept) { Add-UrgbWarning $Ctx "Kept $k" }
    Write-UrgbLog $Ctx "Kept the data directory '$($Layout.Data)' (profiles, scenes, calibration and logs) unchanged."
    return New-UrgbResult $Ctx $Layout 'Uninstalled' $null $null
}

<#
.SYNOPSIS
  Uninstalls an owned installation: stops only its task, restores vendor services with the verified Host, then
  deletes only manifest-owned files that are unchanged. User data is never deleted.
#>
function Invoke-UrgbUninstall {
    [CmdletBinding(SupportsShouldProcess)]
    param(
        [Parameter(Mandatory)] [string] $InstallDir,
        [Parameter(Mandatory)] [string] $AllowedParent,
        [Parameter(Mandatory)] [string] $DataDir,
        [Parameter(Mandatory)] $Platform,
        [ValidateRange(1, 600)] [int] $StopTimeoutSeconds = 15,
        [scriptblock] $Reporter
    )
    $ctx = New-UrgbContext $Platform $Reporter
    $layout = Resolve-UrgbLayout $InstallDir $AllowedParent $DataDir ''
    $lock = Enter-UrgbLock $ctx
    try { return Invoke-UrgbUninstallCore $ctx $layout $PSCmdlet $StopTimeoutSeconds }
    finally { Exit-UrgbLock $lock }
}

# ---------------------------------------------------------------- production platform

<#
.SYNOPSIS
  The real Windows actions. Only the root task '\UnifiedRgb Host' and the single HKCU Run value 'UnifiedRgb' are
  addressed; processes are only read, never stopped or killed.
#>
function New-UrgbWindowsPlatform {
    [pscustomobject]@{
        LockName = 'Global\UnifiedRgb.Installation'
        IsAdministrator = {
            $principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
            $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
        }
        GetProcesses = {
            foreach ($p in @(Get-CimInstance -ClassName Win32_Process -Property ProcessId, Name, ExecutablePath)) {
                $name = [string]$p.Name
                if ($name.EndsWith('.exe', [StringComparison]::OrdinalIgnoreCase)) { $name = $name.Substring(0, $name.Length - 4) }
                [pscustomobject]@{ Id = [int]$p.ProcessId; Name = $name; Path = [string]$p.ExecutablePath }
            }
        }
        GetTask = {
            $task = Get-ScheduledTask -TaskPath '\' -TaskName $script:TaskName -ErrorAction SilentlyContinue
            if ($null -eq $task) { return $null }
            $actions = @($task.Actions)
            $command = $null
            $arguments = $null
            if ($actions.Count -gt 0) { $command = [string]$actions[0].Execute; $arguments = [string]$actions[0].Arguments }
            [pscustomobject]@{ Command = $command; Arguments = $arguments; ActionCount = $actions.Count; State = [string]$task.State }
        }
        StopTask = { Stop-ScheduledTask -TaskPath '\' -TaskName $script:TaskName }
        StartTask = { Start-ScheduledTask -TaskPath '\' -TaskName $script:TaskName }
        UnregisterTask = { Unregister-ScheduledTask -TaskPath '\' -TaskName $script:TaskName -Confirm:$false }
        InvokeMaintenance = {
            param([string]$Executable, [string]$Verb)
            # The Host uses the Windows GUI subsystem: always wait explicitly, keep it hidden and read its own exit code.
            $process = Start-Process -FilePath $Executable -ArgumentList $Verb -WindowStyle Hidden -Wait -PassThru
            [int]$process.ExitCode
        }
        GetRunValue = {
            $item = Get-ItemProperty -LiteralPath $script:RunKey -Name $script:RunValueName -ErrorAction SilentlyContinue
            if ($null -eq $item) { return $null }
            [string]$item.PSObject.Properties[$script:RunValueName].Value
        }
        SetRunValue = {
            param([string]$Value)
            if (-not (Test-Path -LiteralPath $script:RunKey)) { $null = New-Item -Path $script:RunKey }
            Set-ItemProperty -LiteralPath $script:RunKey -Name $script:RunValueName -Value $Value
        }
        RemoveRunValue = { Remove-ItemProperty -LiteralPath $script:RunKey -Name $script:RunValueName }
        EnsureDataDirectory = {
            param([string]$Path)
            # An existing data directory (profiles, scenes, calibration) is left exactly as it is, ACL included.
            if (Test-Path -LiteralPath $Path) { return 'kept as it is' }
            $null = New-Item -ItemType Directory -Path $Path
            $acl = New-Object Security.AccessControl.DirectorySecurity
            $acl.SetAccessRuleProtection($true, $false)
            $inherit = [Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit'
            $none = [Security.AccessControl.PropagationFlags]::None
            foreach ($sid in 'S-1-5-32-544', 'S-1-5-18') {
                $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule((New-Object Security.Principal.SecurityIdentifier $sid), 'FullControl', $inherit, $none, 'Allow')))
            }
            $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule((New-Object Security.Principal.SecurityIdentifier 'S-1-5-32-545'), 'ReadAndExecute', $inherit, $none, 'Allow')))
            Set-Acl -LiteralPath $Path -AclObject $acl
            $null = New-Item -ItemType Directory -Path (Join-Path $Path 'profiles'), (Join-Path $Path 'logs')
            return 'created with Administrators/SYSTEM full control and Users read'
        }
        Sleep = { param([int]$Milliseconds) Start-Sleep -Milliseconds $Milliseconds }
        Now = { [DateTime]::UtcNow }
    }
}

Export-ModuleMember -Function Invoke-UrgbInstall, Invoke-UrgbUninstall, New-UrgbWindowsPlatform
