[CmdletBinding()]
param([string]$Output = '.artifacts-windows-release', [string]$Dotnet = 'dotnet', [string]$MakeNsis)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$outputRoot = [IO.Path]::GetFullPath((Join-Path $repo $Output))
if (Test-Path -LiteralPath $outputRoot) { throw 'Use a fresh output directory; existing artifacts are never overwritten.' }
[void][IO.Directory]::CreateDirectory($outputRoot)
$payload = Join-Path $outputRoot 'payload'
[void][IO.Directory]::CreateDirectory($payload)
$facades = [Collections.Generic.List[object]]::new()
if (-not $MakeNsis) { $MakeNsis = & (Join-Path $PSScriptRoot 'Get-Nsis.ps1') -Destination (Join-Path $outputRoot 'toolchain') }
foreach ($app in @('Host','App','Cli','Setup')) {
    $publish = Join-Path $outputRoot "publish-$app"
    & $Dotnet publish (Join-Path $repo "src/UnifiedRgb.$app/UnifiedRgb.$app.csproj") -c Release -r win-x64 --self-contained true `
        --artifacts-path (Join-Path $outputRoot 'artifacts') -o $publish -p:PublishSingleFile=false -p:PublishTrimmed=false -p:DebugType=None
    if ($LASTEXITCODE -ne 0) { throw "Publishing $app failed." }
    foreach ($file in Get-ChildItem -LiteralPath $publish -File -Recurse) {
        $relative = [IO.Path]::GetRelativePath($publish, $file.FullName)
        $target = Join-Path $payload $relative
        [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target))
        if (Test-Path -LiteralPath $target) {
            $oldHash=(Get-FileHash -LiteralPath $target).Hash
            $newHash=(Get-FileHash -LiteralPath $file.FullName).Hash
            if ($oldHash -ne $newHash) {
                # WindowsDesktop's facade forwards the superset used by WPF + Forms. Never silently replace application dependencies.
                if ($app -eq 'App' -and $relative -in @('Microsoft.VisualBasic.dll','System.Drawing.dll')) {
                    $frameworkVersion=((Get-Content (Join-Path $publish 'UnifiedRgb.App.runtimeconfig.json') -Raw|ConvertFrom-Json).runtimeOptions.includedFrameworks|Where-Object name -eq 'Microsoft.WindowsDesktop.App').version
                    $packageCache=if($env:NUGET_PACKAGES){$env:NUGET_PACKAGES}else{Join-Path $env:USERPROFILE '.nuget/packages'}
                    $desktopFacade=Join-Path $packageCache "microsoft.windowsdesktop.app.runtime.win-x64/$frameworkVersion/runtimes/win-x64/lib/net10.0/$relative"
                    if (-not(Test-Path -LiteralPath $desktopFacade) -or (Get-FileHash -LiteralPath $desktopFacade).Hash -ne $newHash) { throw "Unverified desktop facade: $relative" }
                    $facades.Add(@{Path=$relative;Previous=$oldHash;Selected=$newHash;Source="WindowsDesktop $frameworkVersion"})
                    [IO.File]::Copy($file.FullName,$target,$true)
                } elseif ($app -in @('Cli','Setup') -and $relative -in @('Microsoft.VisualBasic.dll','System.Drawing.dll')) {
                    $known=@($facades|Where-Object Path -eq $relative)
                    if($known.Count-ne1-or$known[0].Previous-ne$newHash){throw "Unexpected facade collision: $relative"}
                } else { throw "Payload collision: $relative" }
            }
        } else { [IO.File]::Copy($file.FullName, $target) }
    }
}
$facades|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $outputRoot 'runtime-facade-selection.json') -Encoding utf8
$moduleDirectory = Join-Path $payload 'Resources/PawnIO'
[void][IO.Directory]::CreateDirectory($moduleDirectory)
foreach ($name in @('SmbusPIIX4.bin','SmbusPIIX4.p','sleepmode.inc','COPYING','PROVENANCE.md','SHA256SUMS')) {
    Copy-Item -LiteralPath (Join-Path $repo "tools/pawnio/$name") -Destination $moduleDirectory
}
Copy-Item -LiteralPath (Join-Path $repo 'tools/pawnio/include') -Destination $moduleDirectory -Recurse
foreach ($name in @('THIRD-PARTY-NOTICES.md','SOURCE-PROVENANCE.md')) {
    Copy-Item -LiteralPath (Join-Path $repo $name) -Destination $payload
}
foreach ($relative in @('packaging/Bundle-Lifecycle.ps1','installation/UnifiedRgb.Installation.psm1')) {
    $target = Join-Path $payload "Tools/$relative"
    [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target))
    Copy-Item -LiteralPath (Join-Path $repo "tools/$relative") -Destination $target
}
& (Join-Path $PSScriptRoot 'Export-DependencyNotices.ps1') -Artifacts (Join-Path $outputRoot 'artifacts') -Payload $payload -NsisRoot (Split-Path $MakeNsis)
$sourceDefaults = Get-Content -LiteralPath (Join-Path $payload 'appsettings.json') -Raw | ConvertFrom-Json
if (-not $sourceDefaults.UnifiedRgb.UseVirtualAdapter -or $sourceDefaults.UnifiedRgb.EnableLianLi -or $sourceDefaults.UnifiedRgb.Screens.Enabled) { throw 'Unsafe release defaults.' }
$forbidden = @(Get-ChildItem -LiteralPath $payload -Recurse -File | Where-Object {
    $_.Name -in @('yuz.dll','PawnIOLib.dll','AGENTS.md','mounting.json','native-maps.json','takeover.json','tl-twinkle.bin','h2-twinkle.bin','ui-ripple.bin') `
        -or $_.Extension -in @('.pfx','.p12','.key')
})
if ($forbidden.Count) { throw 'Private/vendor payload files were found.' }
$version = ([xml](Get-Content -LiteralPath (Join-Path $repo 'src/Directory.Build.props') -Raw)).Project.PropertyGroup.Version
$inventory = @(Get-ChildItem -LiteralPath $payload -Recurse -File | ForEach-Object {
    [ordered]@{Path=[IO.Path]::GetRelativePath($payload,$_.FullName);Bytes=$_.Length;Sha256=(Get-FileHash -LiteralPath $_.FullName).Hash}
})
$inventory | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $outputRoot 'payload-sha256.json') -Encoding utf8
$installer = Join-Path $outputRoot "Guangyu-$version-win-x64-Setup.exe"
& $MakeNsis /INPUTCHARSET UTF8 "/DPAYLOAD=$payload" "/DOUTPUT=$installer" "/DRELEASE_VERSION=$version" "/DICON=$(Join-Path $repo 'src/UnifiedRgb.App/Assets/Brand/guangyu.ico')" (Join-Path $PSScriptRoot 'UnifiedRgb.nsi')
if ($LASTEXITCODE -ne 0) { throw 'NSIS compilation failed.' }
$sha = (Get-FileHash -LiteralPath $installer).Hash
"$sha  $([IO.Path]::GetFileName($installer))" | Set-Content -LiteralPath (Join-Path $outputRoot 'SHA256SUMS') -Encoding ascii
Write-Output $installer
