# CmmBench — standalone decode / loudness micro-benchmark

Drives the mod's **real** decode + EBU-R128 pipeline
([AudioDecoder.MeasureLoudness](../../Harmony/AudioDecoder.cs) and the bare
native-handle open path used at a context transition) over a set of audio files,
outside the game. This is the fast measure-then-optimize loop for the CPU-bound
sweep work — no Unity launch needed.

It **links** the actual source files (`AudioDecoder.cs`, `NativeAudio.cs`)
so the bench can never drift from shipped code, and supplies
thin shims (`Shims.cs`) for the handful of non-audio symbols the decoder touches
(`CMMConfig`, `Log`, `LoudnessStore.HashFile`, `Mathf`).

## Build & run

```sh
dotnet build tools/CmmBench -c Release
dotnet run   --project tools/CmmBench -c Release -- "J:\7D2D_Custom_Music\Combat" -n 5 --open
```

Or run the built exe directly (faster, no rebuild check):

```
tools/CmmBench/bin/Release/net48/CmmBench.exe <dir-or-files> [options]
```

### Options

| Flag                | Meaning                                                                          |
|---------------------|----------------------------------------------------------------------------------|
| `-n <count>`        | Timed repeats per file (default 5; plus 1 discarded warmup).                     |
| `-v`                | Verbose — enables the decoder's own per-file read/hash/index/decode split.       |
| `--open`            | Also benchmark `OpenNativeHandle` (the per-transition open latency).             |
| `--verify-fileopen` | Decode via `open_memory` **and** `open_file`, assert identical frames + peak.    |
| `--cold`            | Cache-bypassed cold read + full decode (true load-from-disk cost on slow media). |

## Output

Per file: size, measure avg/min/max ms, decode throughput (MB/s), GC gen0/1/2
deltas, and (with `--open`) open latency. Then a per-format rollup and a p50/p95/max
summary. Opus is expected to be the slowest format to measure.

## Relationship to the in-game profiler

This is **Arena B**. **Arena A** is the in-game instrumentation
([CMMPerf](../../Harmony/CMMPerf.cs), enabled via `PerfTiming` in `settings.xml`),
which times the actual context-change hot path (poll / resolve / gate / transition),
logs per-transition GC + wall lines, fires a `SPIKE` line for any synchronous span
over `PerfSpikeMs`, and can stress-fire rapid switches with **Ctrl+Alt+P**. Use
Arena A to find *which phase* hitches; use this bench to fix the CPU-bound ones fast.
