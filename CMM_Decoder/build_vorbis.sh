#!/usr/bin/env bash
# ──────────────────────────────────────────────────────────────────────────
# Prebuild build/vorbis/libvorbis.a — the libvorbis codec + libvorbisfile
# (ov_*) high-level file API, from the vendored third_party/vorbis source.
# Run once (like the opus/ogg/fdk-aac cmake prebuilds); build_dll.sh just links
# the resulting .a. Replaces the header-only stb_vorbis (no callback/stream API).
#
# Source list is libvorbis_la_SOURCES + vorbisfile.c from lib/Makefile.am
# (vorbisenc.c and the barkmel/tone/psytune tools are omitted — decode only).
#
# Same mandatory mingw flags as build_dll.sh (see that file's header):
#   -DNDEBUG               : compile out assert() → no _wassert/abort() on a bad file.
#   -fno-stack-protector   : mingw's %fs canary SEGVs on Windows x64.
#   -mstackrealign         : keep SSE 16-byte alignment.
# ──────────────────────────────────────────────────────────────────────────
set -e
cd "$(dirname "$0")"

GCC=${GCC:-gcc}
AR=${AR:-ar}
SRCDIR=third_party/vorbis/lib
OBJDIR=build/vorbis/obj
OUT=build/vorbis/libvorbis.a

INC=(
  -Ithird_party/vorbis/include   # vorbis/codec.h, vorbisfile.h
  -Ithird_party/vorbis/lib       # internal headers (codec_internal.h, os.h, …)
  -Ithird_party/ogg/include      # ogg/ogg.h
  -Ibuild/ogg/include            # generated ogg/config_types.h
)

SRC=(
  mdct smallft block envelope window lsp lpc analysis synthesis psy info
  floor1 floor0 res0 mapping0 registry codebook sharedbook lookup bitrate
  vorbisfile
)

mkdir -p "$OBJDIR"
OBJS=()
for s in "${SRC[@]}"; do
  "$GCC" -O2 -DNDEBUG -fno-stack-protector -mstackrealign \
    "${INC[@]}" -c "$SRCDIR/$s.c" -o "$OBJDIR/$s.o"
  OBJS+=("$OBJDIR/$s.o")
done

rm -f "$OUT"
"$AR" rcs "$OUT" "${OBJS[@]}"
echo "built: $OUT"
