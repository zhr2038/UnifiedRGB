# CI-only: real elevated installation/upgrade/uninstall on a disposable Windows runner, never the owner's workstation.
[CmdletBinding()]
param([Parameter(Mandatory)][string]$Installer, [string]$Evidence = 'InstallerResults')
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_OS -ne 'Windows') { throw 'This destructive lifecycle QA is restricted to a disposable GitHub Windows runner.' }
$root = Join-Path $env:ProgramFiles 'UnifiedRgb-QA'
$data = Join-Path $env:ProgramData 'UnifiedRgb'
if ((Test-Path -LiteralPath $root) -or (Test-Path -LiteralPath $data) -or (Get-ScheduledTask -TaskName 'UnifiedRgb Host' -ErrorAction SilentlyContinue)) { throw 'The runner is not clean; refusing to adopt existing state.' }
$Installer = [IO.Path]::GetFullPath($Installer)
$Evidence = [IO.Path]::GetFullPath($Evidence)
[void][IO.Directory]::CreateDirectory($Evidence)
$startedAt=Get-Date
function Copy-LifecycleLogs {
    foreach($log in Get-ChildItem -LiteralPath ([IO.Path]::GetTempPath()) -Filter 'UnifiedRgb-Setup-*.log' -File|Where-Object CreationTime -ge $startedAt) {
        Copy-Item -LiteralPath $log.FullName -Destination $Evidence -Force
    }
}
function Invoke-Installer {
    $p = Start-Process -FilePath $Installer -ArgumentList @('/S',"/D=$root") -WindowStyle Hidden -PassThru
    if (-not $p.WaitForExit(180000)) { throw 'Installer timed out; not force-killed.' }
    Copy-LifecycleLogs
    if ($p.ExitCode -ne 0) {
        Get-ChildItem -LiteralPath $Evidence -Filter '*.log'|ForEach-Object {Get-Content -LiteralPath $_.FullName -Tail 25}
        throw "Installer exited $($p.ExitCode)."
    }
}
function Read-Manifest { Get-Content -LiteralPath (Join-Path $root 'UnifiedRgb.install.json') -Raw | ConvertFrom-Json }
Invoke-Installer
$first = Read-Manifest
$firstDir = Join-Path $root "versions/$($first.Current)"
foreach ($name in @('UnifiedRgb.Host.exe','UnifiedRgb.App.exe','UnifiedRgb.Setup.exe','coreclr.dll','PresentationFramework.dll','THIRD-PARTY-NOTICES.md','SOURCE-PROVENANCE.md')) {
    if (-not (Test-Path -LiteralPath (Join-Path $firstDir $name))) { throw "Missing self-contained payload: $name" }
}
$task = Get-ScheduledTask -TaskName 'UnifiedRgb Host'
if ($task.Actions.Execute -ne (Join-Path $firstDir 'UnifiedRgb.Host.exe')) { throw 'Task points outside the installed version.' }
$config = Join-Path $firstDir 'appsettings.json'
$json = Get-Content -LiteralPath $config -Raw | ConvertFrom-Json
if (-not $json.UnifiedRgb.UseVirtualAdapter -or $json.UnifiedRgb.EnableLianLi -or $json.UnifiedRgb.Screens.Enabled) { throw 'Hardware was enabled by the installer.' }
$json | Add-Member NoteProperty UpgradeSentinel ([pscustomobject]@{ Chinese='保留本机未知字段'; Value=17 })
$json | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $config -Encoding utf8
$configSha = (Get-FileHash -LiteralPath $config).Hash
$sentinel = Join-Path $data 'user-sentinel.json'
[IO.File]::WriteAllText($sentinel,'{"scene":"keep","rotation":270}')
$sentinelSha = (Get-FileHash -LiteralPath $sentinel).Hash
$unknown = Join-Path $root 'user-note.txt'
[IO.File]::WriteAllText($unknown,'Do not delete unknown files')
Invoke-Installer
$second = Read-Manifest
$secondDir = Join-Path $root "versions/$($second.Current)"
if ($second.Current -eq $first.Current -or $second.Previous -ne $first.Current) { throw 'Upgrade did not retain the previous version.' }
if ((Get-FileHash -LiteralPath (Join-Path $secondDir 'appsettings.json')).Hash -ne $configSha) { throw 'Upgrade changed the exact installed configuration bytes.' }
if ((Get-FileHash -LiteralPath $sentinel).Hash -ne $sentinelSha) { throw 'Upgrade changed user data.' }
$registration = Get-ItemProperty 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\UnifiedRgb'
if ($registration.CurrentVersionDir -ne $secondDir) { throw 'Uninstaller registration is stale.' }
$shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) '光域/光域.lnk'
if (-not (Test-Path -LiteralPath $shortcut)) { throw 'Start-menu shortcut missing.' }
# Start only the newly installed safe virtual Host, without any hardware enables. No production RPC is used.
$hostExe = Join-Path $secondDir 'UnifiedRgb.Host.exe'
$child = Start-Process -FilePath $hostExe -WorkingDirectory $secondDir -WindowStyle Hidden -PassThru
try {
    Start-Sleep -Seconds 3
    if ($child.HasExited) { throw "Virtual Host exited $($child.ExitCode)." }
    $status = Join-Path $data 'status.json'
    if (-not (Test-Path -LiteralPath $status)) { throw 'Virtual Host did not publish status.' }
    Copy-Item -LiteralPath $status -Destination (Join-Path $Evidence 'virtual-status.json')
} finally {
    # This is our verified virtual child on an ephemeral runner, not the product uninstall's stop policy.
    if (-not $child.HasExited -and $child.Path -eq $hostExe) { Stop-Process -Id $child.Id; $child.WaitForExit() }
}
$uninstaller = Join-Path $root 'Uninstall.exe'
$remove = Start-Process -FilePath $uninstaller -ArgumentList '/S' -WindowStyle Hidden -PassThru
if (-not $remove.WaitForExit(180000) -or $remove.ExitCode -ne 0) { throw 'Uninstall failed.' }
# NSIS relocates the uninstaller into its temporary directory. The launching process may exit before that child;
# wait for the actual ownership/registration result instead of accepting the launcher exit as completion.
$deadline=[DateTime]::UtcNow.AddSeconds(180)
while ((Test-Path 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\UnifiedRgb') -or (Get-ScheduledTask -TaskName 'UnifiedRgb Host' -ErrorAction SilentlyContinue)) {
    if([DateTime]::UtcNow-ge$deadline){throw 'The real uninstaller did not complete within its bound.'}
    Start-Sleep -Milliseconds 250
}
if (Get-ScheduledTask -TaskName 'UnifiedRgb Host' -ErrorAction SilentlyContinue) { throw 'Owned task survived uninstall.' }
if (Test-Path 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\UnifiedRgb') { throw 'Application registration survived uninstall.' }
Copy-LifecycleLogs
if (-not (Test-Path -LiteralPath $unknown) -or (Get-FileHash -LiteralPath $sentinel).Hash -ne $sentinelSha) { throw 'Uninstall removed unknown files or user data.' }
@{Passed=$true;FirstVersion=$first.Current;SecondVersion=$second.Current;ConfigurationByteExact=$true;UserDataByteExact=$true;UnknownFilesRetained=$true;TaskRemoved=$true;SelfContainedVirtualHostStarted=$true;HardwareOpened=$false;SignedInstaller=$false} |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $Evidence 'result.json') -Encoding utf8
