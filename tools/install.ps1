<#
.SYNOPSIS
  Installs Unified RGB Controller (ARCHITECTURE §9). Run elevated, as the user who will log on.

.DESCRIPTION
  - Copies the published Host/App/Cli into -InstallDir (default %ProgramFiles%\UnifiedRgb).
  - Creates %ProgramData%\UnifiedRgb (profiles, logs) with Administrators/SYSTEM full control and Users read.
  - Registers scheduled task "UnifiedRgb Host" (logon, highest privileges, 20 s delay, restart 3x/min) via
    `UnifiedRgb.Host.exe --register-task` (definition in ScheduledTaskInstaller, unit-tested).
  - Adds HKCU Run entry "UnifiedRgb" = UnifiedRgb.App.exe --minimized (tray only).
  Vendor software, services and files are not touched; Takeover is off until enabled in the GUI.

.EXAMPLE
  ./tools/install.ps1 -Source .\publish
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)] [string] $Source,
    [string] $InstallDir = (Join-Path $env:ProgramFiles 'UnifiedRgb'),
    [switch] $NoStart
)
$ErrorActionPreference = 'Stop'

$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Run this script elevated.' }

$hostExe = Join-Path $Source 'UnifiedRgb.Host.exe'
if (-not (Test-Path -LiteralPath $hostExe -PathType Leaf)) { throw "UnifiedRgb.Host.exe not found in $Source" }

if ($PSCmdlet.ShouldProcess($InstallDir, 'Copy program files')) {
    New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
    Copy-Item -Path (Join-Path $Source '*') -Destination $InstallDir -Recurse -Force
}

$data = Join-Path $env:ProgramData 'UnifiedRgb'
if ($PSCmdlet.ShouldProcess($data, 'Create data directory with restricted ACL')) {
    New-Item -ItemType Directory -Force -Path (Join-Path $data 'profiles'), (Join-Path $data 'logs') | Out-Null
    $acl = New-Object Security.AccessControl.DirectorySecurity
    $acl.SetAccessRuleProtection($true, $false)
    $inherit = [Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit'
    $none = [Security.AccessControl.PropagationFlags]::None
    foreach ($sid in 'S-1-5-32-544', 'S-1-5-18') {
        $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule((New-Object Security.Principal.SecurityIdentifier $sid), 'FullControl', $inherit, $none, 'Allow')))
    }
    $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule((New-Object Security.Principal.SecurityIdentifier 'S-1-5-32-545'), 'ReadAndExecute', $inherit, $none, 'Allow')))
    Set-Acl -LiteralPath $data -AclObject $acl
}

$installedHost = Join-Path $InstallDir 'UnifiedRgb.Host.exe'
if ($PSCmdlet.ShouldProcess('UnifiedRgb Host', 'Register scheduled task')) {
    & $installedHost --register-task
    if ($LASTEXITCODE -ne 0) { throw "Task registration failed ($LASTEXITCODE)" }
}

$app = Join-Path $InstallDir 'UnifiedRgb.App.exe'
if ((Test-Path -LiteralPath $app) -and $PSCmdlet.ShouldProcess('HKCU Run', 'Add UnifiedRgb tray entry')) {
    Set-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'UnifiedRgb' -Value "`"$app`" --minimized"
}

if (-not $NoStart -and $PSCmdlet.ShouldProcess('UnifiedRgb Host', 'Start now')) {
    Start-ScheduledTask -TaskName 'UnifiedRgb Host'
}
Write-Host 'Installed. Close vendor RGB software (or enable Takeover in the GUI) for devices to leave the Blocked state.'
