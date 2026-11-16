# CMM_Decoder

Native audio library (`CMM_Decoder.dll`) for Context Music Manager. It decodes every
supported format (Opus, Vorbis, FLAC, MP3, WAV/AIFF, AAC and ALAC in the MP4 family,
raw AAC, Matroska/WebM) into native memory the Unity GC never scans, mixes and plays
the result on its own thread, and runs the loudness and world-acoustics models. The
managed side (`Harmony/NativeAudio.cs` and friends) calls into it through
`DllImport("CMM_Decoder")`. There is no managed decode fallback.

## You do not need to build this to run the mod

The compiled `dist/CMM_Decoder.dll` is committed, and the mod and `tools/CmmBench` load
that binary directly. Everything below is only for rebuilding it.

## Rebuilding

Toolchain: mingw-w64 gcc and make (tested with gcc 8.1.0, x86_64-posix-seh), cmake, and
nasm, run from Git Bash or MSYS2.

```sh
git submodule update --init      # ffmpeg (shallow), ogg, opus, opusfile
./build_deps.sh                  # once: libogg, libopus, libvorbis, FFmpeg -> build/
./build_dll.sh                   # the DLL itself -> dist/CMM_Decoder.dll (fast inner loop)
```

`third_party/dr_libs`, `third_party/miniaudio` and `third_party/vorbis` are vendored
source and need no init. The whole `build/` tree is regenerable and gitignored, as are
the `test_*.exe` scratch binaries.

| Library | Where it comes from | Built by |
| --- | --- | --- |
| libogg, libopus, libopusfile | submodules (Xiph) | `build_deps.sh` (cmake); opusfile compiles inside `build_dll.sh` |
| libvorbis, libvorbisfile | vendored v1.3.7 | `build_vorbis.sh` |
| FFmpeg (mov demuxer, aac + alac decoders) | submodule, tag n9.0.2 | `build_ffmpeg.sh` |
| dr_flac / dr_mp3 / dr_wav, miniaudio | vendored single headers | `build_dll.sh` |

Gotchas, each baked into the scripts with the reason next to it:

- `-fno-stack-protector`: mingw emits a Linux-style `%fs` stack canary that segfaults on
  Windows x64. Opus needs `-DOPUS_STACK_PROTECTOR=OFF` as well, because it forces one.
- `-DNDEBUG`: a live `assert()` in a decoder once hard-crashed the game on a real file.
- `-static`: the DLL depends only on Windows itself (KERNEL32, msvcrt, bcrypt).
- `-Wl,--wrap` on the allocators, `_aligned_*` included: every native allocation is
  counted by `cmm_meminfo`, FFmpeg's included.

## Licensing

FFmpeg is built LGPL-2.1-or-later (no GPL or nonfree components). Because it is linked
statically, this source tree and these scripts are what let anyone rebuild the DLL against
a modified FFmpeg. Every bundled library's license is in `THIRD_PARTY_NOTICES.txt`.

## Tests

`test_aac_ab.c` compares two builds of the DLL on any set of files: open, exact length,
audio against each other (and against a source WAV with `CMM_REF`), seek accuracy, memory
vs file parity, native heap leaks, plus `CMM_PROBE=1` (bulk open/play check) and
`CMM_STRESS=<threads>` (concurrent decodes against single-threaded ones). Build it with
`gcc -O2 -municode -o test_aac_ab.exe test_aac_ab.c`.
