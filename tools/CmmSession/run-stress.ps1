<#
.SYNOPSIS
  Run the CmmSession long-session STRESS benchmark (Arena C) with tunable knobs.

.DESCRIPTION
  Drives the real addon code (Lua resolver + selection + native decode + the real
  native mixer, headless) through a compressed multi-day synthetic session to hunt
  MEMORY LEAKS (managed heap + native RSS) and performance regressions.

  Defaults to a MARATHON (30 sim-days) so slow drift becomes visible. Tune anything
  via the params below — e.g. a quick smoke run:
      pwsh tools/CmmSession/run-stress.ps1 -Hours 6
  a faithful (real-time, muted) mixer soak that actually validates underruns:
      pwsh tools/CmmSession/run-stress.ps1 -Mixer realtime -Hours 1
  regenerate the synthetic library first, then run the marathon:
      pwsh tools/CmmSession/run-stress.ps1 -GenerateLibrary

  NOTE: -Mixer realtime is bound to the wall clock (a 3-min track plays for 3 real
  minutes). Use SMALL -Hours with it. Headless (default) is the one that compresses.
#>
[CmdletBinding()]
param(
    [double]$Hours            = 720,      # 30 sim-days — the leak marathon (tune down for quick runs)
    [double]$Tick             = 0.5,      # scan cadence seconds
    [int]   $Seed             = 1,
    [double]$DayMinutes       = 60,
    [ValidateSet('off','headless','realtime')]
    [string]$Mixer            = 'headless',
    [double]$ReloadMin        = 15,       # sim-minutes between context.lua/config valid<->invalid flips (0=off)
    [string]$MusicDir         = 'C:\_Games\CMM_TestMusic',
    [string]$Configuration    = 'Release',
    [switch]$GenerateLibrary,             # (re)build the synthetic library first (ffmpeg)
    [switch]$NoLoudness,
    [switch]$NoDms,
    [switch]$Sweep,
    [switch]$NoDecode,
    [switch]$VerboseRun,
    [string]$OutFile                       # optional: also tee the report to this file
)

$ErrorActionPreference = 'Stop'
$proj = Join-Path $PSScriptRoot 'CmmSession.csproj'

if ($Mixer -eq 'realtime' -and $Hours -gt 6) {
    Write-Warning "Mixer 'realtime' is wall-clock bound; $Hours sim-hours would take ~$Hours real hours. Consider -Hours 1."
}

if ($GenerateLibrary) {
    Write-Host "== Generating synthetic library ==" -ForegroundColor Cyan
    & (Join-Path $PSScriptRoot 'gen-testlib.ps1') -OutDir $MusicDir -Force
}

# Build the benchmark argument list.
$benchArgs = @(
    $MusicDir,
    '--hours',       $Hours,
    '--tick',        $Tick,
    '--seed',        $Seed,
    '--day-minutes', $DayMinutes,
    '--mixer',       $Mixer,
    '--reload-min',  $ReloadMin
)
if ($NoLoudness) { $benchArgs += '--no-loudness' }
if ($NoDms)      { $benchArgs += '--no-dms' }
if ($Sweep)      { $benchArgs += '--sweep' }
if ($NoDecode)   { $benchArgs += '--no-decode' }
if ($VerboseRun) { $benchArgs += '-v' }

Write-Host "== CmmSession stress run ==" -ForegroundColor Cyan
Write-Host "   $($benchArgs -join ' ')" -ForegroundColor DarkGray

# dotnet run passes everything after `--` to the program.
$runArgs = @('run','--project', $proj, '-c', $Configuration, '--') + $benchArgs

if ($OutFile) {
    & dotnet @runArgs 2>&1 | Tee-Object -FilePath $OutFile
    Write-Host "`nReport saved to $OutFile" -ForegroundColor Green
}
else {
    & dotnet @runArgs
}
