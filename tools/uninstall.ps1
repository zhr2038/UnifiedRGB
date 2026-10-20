<#
.SYNOPSIS
  Removes Unified RGB Controller. Run elevated.

.DESCRIPTION
  The work is done by tools/installation/UnifiedRgb.Installation.psm1 (audit F6):
  - Only a manifest-owned installation under %ProgramFiles% is accepted; anything else is refused unchanged.
  - Refuses while the GUI is open or while a Host the installation does not own is running; refuses (nothing
    changed) when the installed Host is missing or modified, because it is needed to restore vendor services.
  - Stops only the installed task "UnifiedRgb Host" and waits until the Host has really exited. Nothing is ever
    killed: a timeout aborts before vendor services are restored or any file is deleted.
  - Restores vendor services stopped by Takeover (`UnifiedRgb.Host.exe --restore-services`, hidden and waited). If
    that fails, the task is removed but every program file is kept so the uninstall can be re-run.
  - Unregisters the task, removes the HKCU Run entry if it points at this installation, then deletes only the files
    listed in the installation manifest that are still unchanged. Modified or unknown files and carried-over
    configuration are kept and listed; directories are only removed when empty.
  - %ProgramData%\UnifiedRgb (profiles, scenes, calibration, logs) is always kept.

  -Purge is kept for compatibility but refused: deleting %ProgramData%\UnifiedRgb recursively cannot be limited to
  files this installer owns. Remove that directory yourself after checking it, if you really want to.
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string] $InstallDir = (Join-Path $env:ProgramFiles 'UnifiedRgb'),
    [switch] $Purge
)
$ErrorActionPreference = 'Stop'

if ($Purge) {
    throw "Refused: -Purge (recursive deletion of '$(Join-Path $env:ProgramData 'UnifiedRgb')') is no longer supported because that directory holds user profiles, scenes and calibration that the installer does not own. Run uninstall.ps1 without -Purge; delete the data directory yourself after checking it. Nothing was changed."
}

Import-Module (Join-Path $PSScriptRoot 'installation\UnifiedRgb.Installation.psm1') -Force
$platform = New-UrgbWindowsPlatform
if (-not (& $platform.IsAdministrator)) { throw 'Run this script elevated.' }

$arguments = @{
    InstallDir = $InstallDir
    AllowedParent = $env:ProgramFiles
    DataDir = (Join-Path $env:ProgramData 'UnifiedRgb')
    Platform = $platform
    Reporter = { param([string]$Line) Write-Host $Line }
    WhatIf = $WhatIfPreference
}
if ($PSBoundParameters.ContainsKey('Confirm')) { $arguments.Confirm = $PSBoundParameters['Confirm'] }
$result = Invoke-UrgbUninstall @arguments
switch ($result.Outcome) {
    'WhatIf' { return }
    'NotInstalled' { Write-Host "No UnifiedRgb installation in $($result.InstallRoot); nothing was changed." }
    'RestoreFailed' { throw "Failed: vendor services could not be fully restored. The task was removed but all program files and takeover.json were kept; fix the cause and run uninstall.ps1 again." }
    default { Write-Host 'Uninstalled.' }
}
