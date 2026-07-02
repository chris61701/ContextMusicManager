# CMM_Decoder

Native audio decoder (`CMM_Decoder.dll`) for Context Music Manager. Decodes every
supported format (Opus, Vorbis, FLAC, MP3, WAV/AIFF, AAC/M4A) into malloc memory the
Unity GC never scans — this is what eliminates the loudness-sweep / context-change
stutter. The managed side (`Harmony/NativeAudio.cs`, `Harmony/WaveShim.cs`,
`Harmony/AudioDecoder.cs`) calls into it via `DllImport("CMM_Decoder")`. There is no
managed decode fallback.

## You do NOT need to build this to run the mod

The compiled `dist/CMM_Decoder.dll` is committed. The mod (and `tools/CmmBench`) load
that prebuilt binary directly. Everything below is only for **rebuilding** the decoder.

## Rebuilding

The build links against the upstream native libraries, which are tracked as **git
submodules** under `third_party/`. A fresh clone has those directories empty until you
initialize them:

```sh
git submodule update --init   # fetch fdk-aac, minimp4, ogg, opus, opusfile
```

`third_party/dr_libs` and `minimp4`'s single header are vendored directly (not
submodules) and need no init. `third_party/vorbis` (libvorbis + libvorbisfile) is
vendored as source and prebuilt once via `build_vorbis.sh` (see below).

Then build the big static libs once (libopus.a / libogg.a / libfdk-aac.a) via cmake —
that recipe lives in the `native-audio-decode` note, not in this repo — and relink the
shim:

```sh
./build_dll.sh               # relinks dist/CMM_Decoder.dll (fast inner loop)
```

`build_dll.sh` expects the prebuilt `build/{opus,ogg,fdk-aac}/*.a` and the generated
`build/ogg/include/ogg/config_types.h`. The whole `build/` tree is regenerable CMake
output and is gitignored, as are the `test_*.exe` scratch binaries.

> mingw gotcha (baked into `build_dll.sh`): `-fno-stack-protector -mstackrealign` are
> required — mingw otherwise emits a Linux-style `%fs` stack canary that SEGVs on
> Windows x64. `-static` keeps the DLL dependency-free (KERNEL32/ADVAPI32/msvcrt only).
