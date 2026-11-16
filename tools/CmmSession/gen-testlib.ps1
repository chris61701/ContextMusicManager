<#
.SYNOPSIS
  Generate a synthetic CMM test music library for the CmmSession stress benchmark.

.DESCRIPTION
  Parses Config/contexts.xml and, for every context with a relative subfolder path,
  writes N tracks (default 5) of VARYING formats — including the video containers
  (.mp4, .m4v, .mkv) — using ffmpeg. Non-PlayOnce tracks are 3 minutes; PlayOnce
  (once="true") stingers are 10 seconds. Each track is a distinct sine tone at a
  varied loudness so the loudness-normalization model has real differences to measure.
  Tracks get believable names.

  The output dir is the designated synthetic library (NOT the real, off-limits music
  folder). Safe to wipe/regenerate.

.EXAMPLE
  pwsh tools/CmmSession/gen-testlib.ps1 -Force
  pwsh tools/CmmSession/gen-testlib.ps1 -OutDir 'D:\CMM_TestMusic' -TracksPerContext 5 -Force
#>
[CmdletBinding()]
param(
    [string]$OutDir            = 'C:\_Games\CMM_TestMusic',
    [string]$ContextsXml       = (Join-Path $PSScriptRoot '..\..\Config\contexts.xml'),
    [int]   $TracksPerContext  = 5,
    [int]   $Minutes           = 3,      # non-PlayOnce track length
    [int]   $StingerSeconds    = 10,     # PlayOnce track length
    [string]$Ffmpeg            = 'ffmpeg',
    [switch]$Force                       # wipe OutDir before generating
)

$ErrorActionPreference = 'Stop'

if (-not (Get-Command $Ffmpeg -ErrorAction SilentlyContinue)) {
    throw "ffmpeg not found on PATH (looked for '$Ffmpeg'). Install it or pass -Ffmpeg <path>."
}
if (-not (Test-Path $ContextsXml)) { throw "contexts.xml not found at $ContextsXml" }

# ── Believable-name word bank (two-word combos; themed-neutral so any context fits) ──
$adjectives = @('Crimson','Hollow','Ashen','Silent','Fractured','Distant','Molten','Frozen','Restless','Fading',
                'Buried','Endless','Broken','Shrouded','Rising','Withered','Savage','Gilded','Somber','Feral',
                'Iron','Ember','Pale','Vacant','Twilight','Wretched','Static','Velvet','Obsidian','Hallowed')
$nouns      = @('Requiem','Nightfall','Horizon','Reverie','Tide','Vigil','Descent','Echoes','Pulse','Lament',
                'Passage','Ruin','Ascent','Signal','Hymn','Dusk','Wake','Verge','Drift','Mirage',
                'Threnody','Bastion','Cascade','Reckoning','Solace','Torrent','Aftermath','Interlude','Undertow','Requital')

# ── Format assignment: always the three video containers the mixer must handle, plus two
#    rotating audio formats so every native decoder gets exercised across the library. ──
$audioPool = @('.ogg','.mp3','.flac','.wav','.opus','.m4a','.webm')
# A fragmented-mp4 track carries the .m4a extension on disk (that is how such files arrive);
# '.fmp4' is only this script's internal key for the ffmpeg flags above.
$fragExt = '.m4a'

function Get-FfmpegCodecArgs([string]$ext) {
    switch ($ext) {
        '.ogg'  { @('-c:a','libvorbis','-q:a','5') }
        '.mp3'  { @('-c:a','libmp3lame','-q:a','4') }
        '.flac' { @('-c:a','flac') }
        '.wav'  { @('-c:a','pcm_s16le') }
        '.opus' { @('-c:a','libopus','-b:a','128k') }
        # FRAGMENTED mp4 (moof/trun, empty moov). Not a curiosity: it is what NotebookLM
        # "Audio Overview" and most podcast/DASH tooling export, and it silently failed to open
        # until 2026-08-28 because minimp4's demuxer reads sample tables from 'moov' alone.
        # One in the corpus keeps the fragment-index path exercised by every soak.
        '.fmp4' { @('-c:a','aac','-b:a','160k','-movflags','frag_keyframe+empty_moov+default_base_moof','-f','mp4') }
        '.m4a'  { @('-c:a','aac','-b:a','160k') }
        '.mp4'  { @('-c:a','aac','-b:a','160k') }   # audio-only mp4 (video container)
        '.m4v'  { @('-c:a','aac','-b:a','160k') }   # audio-only mp4, .m4v ext (video container)
        '.mkv'  { @('-c:a','libvorbis','-q:a','5') }  # Matroska (container reader)
        '.webm' { @('-c:a','libopus','-b:a','128k') } # WebM (container reader)
        default { throw "no codec mapping for $ext" }
    }
}

# Deterministic RNG per context so re-runs produce the same library.
function New-SeededRandom([string]$key) {
    $h = 17
    foreach ($c in $key.ToCharArray()) { $h = ($h * 31 + [int]$c) -band 0x7fffffff }
    return [System.Random]::new($h)
}

[xml]$xml = Get-Content -LiteralPath $ContextsXml -Raw
# The attribute is `source`, not `path`: contexts.xml renamed it and this script was never
# updated, so the filter matched nothing and the generator quietly produced ZERO tracks for
# every context -- the failure even looked like success ("Generated 0 tracks across 0 contexts").
$contexts = $xml.Contexts.Context | Where-Object {
    $_.source -and $_.source.Trim() -ne '' -and -not ($_.source -match '[:\\]')   # relative subfolders only
}

if ($Force -and (Test-Path $OutDir)) {
    Write-Host "Wiping $OutDir ..." -ForegroundColor Yellow
    Remove-Item -LiteralPath $OutDir -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

$totalFiles = 0
$sw = [System.Diagnostics.Stopwatch]::StartNew()

foreach ($ctx in $contexts) {
    $rel      = $ctx.source -replace '/', '\'
    $isOnce   = ($ctx.once -eq 'true')
    $dur      = if ($isOnce) { $StingerSeconds } else { $Minutes * 60 }
    $ctxDir   = Join-Path $OutDir $rel
    New-Item -ItemType Directory -Force -Path $ctxDir | Out-Null

    $rng   = New-SeededRandom $ctx.name
    $a1    = $audioPool[$rng.Next($audioPool.Count)]
    $a2    = $audioPool[$rng.Next($audioPool.Count)]
    # '.fmp4' is guaranteed in every context alongside the video containers: fragmented mp4 is
    # its own demux path (moof/trun fragments, not 'moov' sample tables) and went unnoticed as
    # broken precisely because nothing in the corpus had that shape.
    $formats = @('.mp4','.m4v','.mkv','.fmp4', $a1, $a2)   # containers guaranteed + two rotating audio
    while ($formats.Count -lt $TracksPerContext) { $formats += $audioPool[$rng.Next($audioPool.Count)] }
    $formats = $formats[0..($TracksPerContext-1)]

    $usedNames = @{}
    for ($i = 0; $i -lt $TracksPerContext; $i++) {
        # Believable, unique-per-folder name.
        do {
            $name = "$($adjectives[$rng.Next($adjectives.Count)]) $($nouns[$rng.Next($nouns.Count)])"
        } while ($usedNames.ContainsKey($name))
        $usedNames[$name] = $true

        $ext  = $formats[$i]
        # A fragmented mp4 is written with the extension such files really carry on disk; $ext
        # stays '.fmp4' so Get-FfmpegCodecArgs picks the fragmenting flags.
        $diskExt = if ($ext -eq '.fmp4') { $fragExt } else { $ext }
        $freq = 180 + $rng.Next(480)                       # 180..660 Hz distinct tone
        $vol  = [math]::Round(0.15 + $rng.NextDouble() * 0.75, 3)  # 0.15..0.90 varied loudness
        # Occasionally 44.1k to exercise the resampler — but Opus only supports 48k.
        $rate = if ($ext -in @('.opus','.webm')) { 48000 } elseif ($rng.Next(4) -eq 0) { 44100 } else { 48000 }
        $out  = Join-Path $ctxDir ($name + $diskExt)

        $codec = Get-FfmpegCodecArgs $ext
        $ffArgs = @('-hide_banner','-loglevel','error','-y',
                    '-f','lavfi','-i',"sine=frequency=$freq`:sample_rate=$rate`:duration=$dur",
                    '-af',"volume=$vol",'-ar',"$rate") + $codec + @($out)
        # Native ffmpeg writes progress/errors to stderr; keep that from tripping Stop.
        $eap = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
        try { & $Ffmpeg @ffArgs 2>&1 | Out-Null } finally { $ErrorActionPreference = $eap }
        if ($LASTEXITCODE -ne 0) { Write-Warning "ffmpeg failed for $out (exit $LASTEXITCODE)" }
        else { $totalFiles++ }
    }
    Write-Host ("  {0,-40} {1} tracks [{2}]" -f $rel, $TracksPerContext, ($formats -join ',')) -ForegroundColor DarkGray
}

$sw.Stop()
$sizeMB = [math]::Round(((Get-ChildItem -LiteralPath $OutDir -Recurse -File | Measure-Object Length -Sum).Sum / 1MB), 1)
Write-Host ""
Write-Host ("Generated {0} tracks across {1} contexts in {2:N0}s  ->  {3} ({4} MB)" -f `
    $totalFiles, $contexts.Count, $sw.Elapsed.TotalSeconds, $OutDir, $sizeMB) -ForegroundColor Green
