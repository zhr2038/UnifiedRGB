#Requires -Version 5.1
param([ValidateSet('Install','Uninstall')][string]$Mode, [Parameter(Mandatory)][string]$InstallRoot,
      [string]$Source, [string]$Receipt, [switch]$NoStart)
$ErrorActionPreference = 'Stop'
# The installer may be launched from PowerShell 7. Do not inherit its module search path into Windows PowerShell 5.1,
# especially for elevated ACL/task operations. This changes this short-lived process only, never user/machine settings.
$env:PSModulePath=[IO.Path]::Combine($PSHOME,'Modules')
$logPath=Join-Path ([IO.Path]::GetTempPath()) ('UnifiedRgb-Setup-'+[Guid]::NewGuid().ToString('N')+'.log')
Start-Transcript -LiteralPath $logPath -NoClobber | Out-Null
try {
if(-not[Environment]::Is64BitProcess){throw 'The x64 installer lifecycle requires 64-bit PowerShell.'}
Import-Module (Join-Path $PSScriptRoot '../installation/UnifiedRgb.Installation.psm1') -Force
$platform = New-UrgbWindowsPlatform
if (-not (& $platform.IsAdministrator)) { throw 'Administrator permission is required.' }
$arguments = @{ InstallDir=$InstallRoot; AllowedParent=$env:ProgramFiles; DataDir=(Join-Path $env:ProgramData 'UnifiedRgb'); Platform=$platform; Confirm=$false }
if ($Mode -eq 'Install') {
    $result = Invoke-UrgbInstall @arguments -Source $Source -NoStart:$NoStart
    $manifest = Get-Content -LiteralPath (Join-Path $InstallRoot 'UnifiedRgb.install.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    # The manifest is owned and validated by the lifecycle module before this result is produced.
    $currentDirectory = Join-Path $InstallRoot ('versions\' + $manifest.Current)
    if ($Receipt) { [IO.File]::WriteAllText($Receipt, $currentDirectory, [Text.UnicodeEncoding]::new($false,$false)) }
} else {
    $result = Invoke-UrgbUninstall @arguments
}
$result | ConvertTo-Json -Depth 8 | Write-Output
} catch {
    $_ | Out-String | Write-Output
    throw
} finally {
    Stop-Transcript | Out-Null
}
