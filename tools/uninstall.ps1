<#
.SYNOPSIS
  Removes Unified RGB Controller. Run elevated.

.DESCRIPTION
  Stops the task, restores any vendor services Takeover stopped (from %ProgramData%\UnifiedRgb\takeover.json via
  `UnifiedRgb.Host.exe --restore-services`), unregisters the task, removes the HKCU Run entry and the program files.
  Profiles and logs are kept unless -Purge is given (takeover.json is kept if a restore failed).
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string] $InstallDir = (Join-Path $env:ProgramFiles 'UnifiedRgb'),
    [switch] $Purge
)
$ErrorActionPreference = 'Stop'

$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Run this script elevated.' }

$hostExe = Join-Path $InstallDir 'UnifiedRgb.Host.exe'
$task = Get-ScheduledTask -TaskName 'UnifiedRgb Host' -ErrorAction SilentlyContinue
if ($task -and $PSCmdlet.ShouldProcess('UnifiedRgb Host', 'Stop task')) {
    Stop-ScheduledTask -TaskName 'UnifiedRgb Host'
    $deadline = (Get-Date).AddSeconds(15)
    while ((Get-Process -Name 'UnifiedRgb.Host' -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 250 }
}

$restoreOk = $true
if ((Test-Path -LiteralPath $hostExe) -and $PSCmdlet.ShouldProcess('vendor services', 'Restore services stopped by Takeover')) {
    $restoration = Start-Process -FilePath $hostExe -ArgumentList '--restore-services' -WindowStyle Hidden -Wait -PassThru
    if ($restoration.ExitCode -ne 0) { $restoreOk = $false; Write-Warning 'Some services could not be restored; see takeover.json.' }
}

if ($task -and $PSCmdlet.ShouldProcess('UnifiedRgb Host', 'Unregister task')) {
    Unregister-ScheduledTask -TaskName 'UnifiedRgb Host' -Confirm:$false
}

if ($PSCmdlet.ShouldProcess('HKCU Run', 'Remove UnifiedRgb entry')) {
    Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'UnifiedRgb' -ErrorAction SilentlyContinue
}

if (-not $restoreOk) {
    Write-Warning "Keeping $InstallDir so '$hostExe --restore-services' can be re-run."
}
elseif ((Test-Path -LiteralPath $InstallDir) -and $PSCmdlet.ShouldProcess($InstallDir, 'Remove program files')) {
    Remove-Item -LiteralPath $InstallDir -Recurse -Force
}

$data = Join-Path $env:ProgramData 'UnifiedRgb'
if ($Purge -and $restoreOk -and (Test-Path -LiteralPath $data) -and $PSCmdlet.ShouldProcess($data, 'Remove profiles and logs')) {
    Remove-Item -LiteralPath $data -Recurse -Force
}
Write-Host 'Uninstalled.'
