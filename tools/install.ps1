<#
.SYNOPSIS
  Installs or upgrades Unified RGB Controller (ARCHITECTURE section 9). Run elevated, as the user who will log on.

.DESCRIPTION
  The work is done by tools/installation/UnifiedRgb.Installation.psm1 (audit F6):
  - -InstallDir must be a sub-directory of %ProgramFiles% (default %ProgramFiles%\UnifiedRgb): never a drive root,
    a junction/symbolic link or another product's directory. An unrelated non-empty directory is refused, and so is
    a directory left by the earlier unmanaged installer (it has no installation manifest).
  - The published -Source tree is copied into a NEW directory versions\<id> and verified file by file (SHA-256)
    before anything that runs is touched. Running binaries are never overwritten.
  - An upgrade refuses while the GUI is open (so an unsaved draft is not lost) or while a Host it does not own is
    running; then it stops only the installed task "UnifiedRgb Host" and waits until the Host has really exited.
    Nothing is ever killed: a timeout aborts the upgrade and leaves the installed version in place.
  - An upgrade keeps the installed appsettings.json byte for byte (unknown fields included). The source defaults
    are used only on a first install.
  - The new version is activated by `UnifiedRgb.Host.exe --register-task` (hidden, waited, exit code checked).
    If that fails the previous version is re-registered and the staged files are removed. The previous version is
    kept for rollback; older ones lose their unchanged files.
  - Creates %ProgramData%\UnifiedRgb (profiles, logs) with Administrators/SYSTEM full control and Users read only if
    it does not exist yet; an existing data directory is left exactly as it is.
  - Sets the HKCU Run entry "UnifiedRgb" = UnifiedRgb.App.exe --minimized unless it belongs to something else.
  Vendor software, services, drivers and files are not touched; Takeover is off until enabled in the GUI.

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

Import-Module (Join-Path $PSScriptRoot 'installation\UnifiedRgb.Installation.psm1') -Force
$platform = New-UrgbWindowsPlatform
if (-not (& $platform.IsAdministrator)) { throw 'Run this script elevated.' }

$arguments = @{
    Source = $Source
    InstallDir = $InstallDir
    AllowedParent = $env:ProgramFiles
    DataDir = (Join-Path $env:ProgramData 'UnifiedRgb')
    Platform = $platform
    NoStart = $NoStart
    Reporter = { param([string]$Line) Write-Host $Line }
    WhatIf = $WhatIfPreference
}
if ($PSBoundParameters.ContainsKey('Confirm')) { $arguments.Confirm = $PSBoundParameters['Confirm'] }
$result = Invoke-UrgbInstall @arguments
if ($result.Outcome -eq 'WhatIf') { return }
Write-Host "$($result.Outcome) version $($result.VersionId) in $($result.InstallRoot)."
Write-Host 'Close vendor RGB software (or enable Takeover in the GUI) for devices to leave the Blocked state.'
