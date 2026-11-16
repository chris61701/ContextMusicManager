#!/usr/bin/env bash
# Build the minimal FFmpeg that CMM_Decoder.dll links: the MP4/M4A demuxer (mov) and the
# AAC + ALAC decoders, as static libs under build/ffmpeg/. Run once (and again only when
# the third_party/ffmpeg submodule moves); build_dll.sh links the result.
#
# Why these and nothing else:
#   mov      -- .m4a/.mp4/.m4v/.3gp, including fragmented MP4 (podcasts, NotebookLM) and the
#               audio track of a video file. Replaces minimp4 (CC0, not OSI-approved).
#   aac      -- AAC-LC, HE-AAC (SBR) and HE-AACv2 (PS). Replaces fdk-aac (FDK license, not
#               OSI-approved). Our own ADTS (.aac) and Matroska framing feed it too.
#   alac     -- Apple Lossless in .m4a. Previously undecodable (52c5577: 104 such files in
#               the author's library never played, silently).
# Everything else is disabled: no programs, no network, no protocols (CMM_Decoder hands
# FFmpeg its own race-free I/O callbacks), no autodetected system libraries.
#
# License: FFmpeg built this way is LGPL-2.1-or-later (no --enable-gpl, no
# --enable-nonfree). Linking it statically is permitted because this repository
# publishes the complete source and this script, so anyone can rebuild the DLL against
# a modified FFmpeg.
#
# Gotchas carried over from the other native libs (see build_dll.sh):
#   - No stack protector: mingw's %fs-based canary segfaults on Windows x64. FFmpeg does
#     not enable one by default; nothing here turns it on.
#   - Threads: keep w32threads (FFmpeg's default on mingw). Without a thread backend,
#     FFmpeg's one-time table initialisation is not thread-safe, and the loudness sweep
#     opens several decoders in parallel. CMM_Decoder still sets thread_count = 1, so no
#     worker threads are ever created.
#   - Asserts: av_assert1/2 are compiled out at the default assert level (0). av_assert0
#     is always live in FFmpeg and guards internal invariants, not input validity.
set -e
cd "$(dirname "$0")"
HERE="$(pwd)"
# C:/... form: the mingw make.exe is a native Windows program and cannot open the
# /c/... paths Git Bash uses, and configure bakes the source path into every Makefile.
SRC="$(cygpath -m "$HERE/third_party/ffmpeg")"
OUT="$HERE/build/ffmpeg"

[ -f "$SRC/configure" ] || { echo "missing $SRC -- run: git submodule update --init CMM_Decoder/third_party/ffmpeg"; exit 1; }
mkdir -p "$OUT/tmp"
cd "$OUT"
# Git Bash exports TMP/TEMP as C:\... with backslashes, which configure mangles into
# "C:Users..." and then reports a misleading "cannot execute files in TEMP".
export TMPDIR="$OUT/tmp"

"$SRC/configure" \
  --target-os=mingw32 --arch=x86_64 --cc="${GCC:-gcc}" \
  --enable-static --disable-shared \
  --disable-programs --disable-doc --disable-debug \
  --disable-autodetect --disable-network \
  --disable-avdevice --disable-avfilter --disable-swscale --disable-swresample \
  --disable-everything \
  --enable-demuxer=mov \
  --enable-decoder=aac,alac \
  --enable-w32threads \
  --malloc-prefix=cmm_ff_ \
  --extra-cflags="-DNDEBUG -fno-stack-protector"

# Route FFmpeg's heap through cmm_mem.c so cmm_meminfo counts it. --malloc-prefix makes
# libavutil/mem.c call cmm_ff_posix_memalign / cmm_ff_realloc / cmm_ff_free, which the DLL
# defines; but configure cannot find those while it runs (they live in our DLL, not a library
# it can link) and so falls back to msvcrt's _aligned_malloc -- which mingw declares
# dllimport, so -Wl,--wrap never sees it and FFmpeg's memory was invisible to the leak
# counters. Select the posix_memalign path by hand; mem.c is the only file that reads these.
sed -i -e 's/^#define HAVE_ALIGNED_MALLOC 1$/#define HAVE_ALIGNED_MALLOC 0/' \
       -e 's/^#define HAVE_POSIX_MEMALIGN 0$/#define HAVE_POSIX_MEMALIGN 1/' config.h
grep -q '^#define HAVE_POSIX_MEMALIGN 1$' config.h || { echo "config.h: allocator switch failed"; exit 1; }

# configure resolves its own location back to a /c/... path no matter how it is invoked. A
# native Windows make (the standalone mingw one used locally) cannot follow those includes,
# so rewrite them to C:/...; MSYS2's make (the GitHub build) reads /c/... fine as it is.
if make --version 2>/dev/null | grep -q 'mingw32'; then
  POSIX_SRC="$(cygpath -u "$SRC")"
  sed -i "s#$POSIX_SRC#$SRC#g" Makefile ffbuild/config.mak
fi

make -j"${JOBS:-8}" libavutil/libavutil.a libavcodec/libavcodec.a libavformat/libavformat.a

echo "built: $OUT/{libavformat,libavcodec,libavutil}/*.a"
grep -E '^EXTRALIBS' ffbuild/config.mak || true
