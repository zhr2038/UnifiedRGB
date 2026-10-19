param([string]$Round = 'round1', [string]$Artifacts = '.artifacts-ui-m1-20261019')
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
$qaExe = Join-Path $projectRoot ($Artifacts + '/bin/UnifiedRgb.UiQa/debug/UnifiedRgb.UiQa.exe')
if (-not (Test-Path -LiteralPath $qaExe)) { throw 'Build UnifiedRgb.UiQa first with the isolated artifacts path.' }
$evidenceRoot = Join-Path $projectRoot ('bringup/ui-m1-20261019/evidence/' + $Round)
[IO.Directory]::CreateDirectory($evidenceRoot) | Out-Null
$cases = @(
    @{Name='standard-dark'; Width=2240; Height=1400; Theme='dark'},
    @{Name='standard-light'; Width=2240; Height=1400; Theme='light'},
    @{Name='wide-dark'; Width=3360; Height=1890; Theme='dark'},
    @{Name='wide-light'; Width=3360; Height=1890; Theme='light'},
    @{Name='short-split'; Width=1920; Height=1080; Theme='dark'; State='draft'},
    @{Name='compact-preview'; Width=1280; Height=800; Theme='dark'},
    @{Name='compact-properties'; Width=1280; Height=800; Theme='light'; Panel='properties'; Details='true'; Scroll='bottom'},
    @{Name='compact-error'; Width=1280; Height=800; Theme='light'; State='error'},
    @{Name='compact-busy'; Width=1280; Height=800; Theme='dark'; State='busy'},
    @{Name='compact-error-details'; Width=1280; Height=800; Theme='light'; State='error'; ErrorDetails='true'},
    @{Name='empty'; Width=2240; Height=1400; Theme='light'; State='empty'},
    @{Name='error'; Width=2240; Height=1400; Theme='light'; State='error'},
    @{Name='busy'; Width=2240; Height=1400; Theme='dark'; State='busy'},
    @{Name='readonly'; Width=2240; Height=1400; Theme='dark'; State='readonly'},
    @{Name='settings'; Width=2240; Height=1400; Theme='light'; Details='true'; Scroll='bottom'},
    @{Name='fake-writes'; Width=2240; Height=1400; Theme='dark'; Writes='true'},
    @{Name='maximized'; Width=2240; Height=1400; Theme='dark'; Maximized='true'}
)
$summary = foreach ($case in $cases) {
    $record = Join-Path $evidenceRoot ($case.Name + '.json')
    $qaOptions = @('--shell=true', '--software=true', '--verify=true', '--diagnostic-render=true', '--exit-after-check=true',
        "--width=$($case.Width)", "--height=$($case.Height)", "--theme=$($case.Theme)", "--record=$record")
    foreach ($pair in @(@('State','--state'),@('Panel','--panel'),@('Details','--details'),@('ErrorDetails','--error-details'),@('Scroll','--scroll'),@('Writes','--verify-writes'),@('Maximized','--maximized'))) {
        if ($case.ContainsKey($pair[0])) { $qaOptions += $pair[1] + '=' + $case[$pair[0]] }
    }
    ConvertTo-Json -InputObject $qaOptions | Set-Content -LiteralPath (Join-Path (Split-Path $qaExe) 'qa-options.json') -Encoding utf8
    $process = Start-Process -FilePath $qaExe -PassThru
    $finished = $process.WaitForExit(20000)
    if (-not $finished) {
        if ($process.Path -ieq $qaExe) { Stop-Process -Id $process.Id }
        throw "QA case timed out: $($case.Name)"
    }
    $checks = if (Test-Path -LiteralPath ($record + '.checks.json')) { Get-Content -LiteralPath ($record + '.checks.json') -Raw | ConvertFrom-Json } else { @() }
    $failures = @($checks | Where-Object { -not $_.passed })
    [pscustomobject]@{ name=$case.Name; exitCode=$process.ExitCode; checks=@($checks).Count; failures=$failures; record=$record }
}
$summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $evidenceRoot 'checks-summary.json') -Encoding utf8
$summary | Select-Object name,exitCode,checks,@{Name='failed';Expression={@($_.failures).Count}} | Format-Table -AutoSize
if (@($summary | Where-Object { $_.exitCode -ne 0 -or $_.checks -eq 0 -or @($_.failures).Count -gt 0 }).Count -gt 0) { throw 'Native WPF component checks failed; retain and inspect checks-summary.json.' }
