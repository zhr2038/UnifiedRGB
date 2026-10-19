<#
.SYNOPSIS
  M8 soak (IMPLEMENTATION_PLAN): runs the Host soak tests for -Hours (default 24) against the Virtual adapter.
  The real-adapter soak is the installed Host itself: leave it running and compare
  `urgb status --json` and Task Manager memory at start and end (see design/SELF_AUDIT.md, M8 pending [HW]).
#>
param([double]$Hours = 24, [string]$Dotnet = 'dotnet')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$env:URGB_SOAK_SECONDS = [int]($Hours * 3600)
& $Dotnet test (Join-Path $root 'tests\UnifiedRgb.Host.Tests') --filter 'FullyQualifiedName~SoakTests|FullyQualifiedName~Host_runs_60s' --logger 'console;verbosity=normal'
exit $LASTEXITCODE
