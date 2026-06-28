#!/usr/bin/env bash
# ──────────────────────────────────────────────────────────────────────────
# Rebuild dist/CMM_Decoder.dll from the shim + dr_flac + opusfile sources,
# linking the PREBUILT static libopus.a / libogg.a under build/. Those big
# libs are built once via cmake (see native-audio-decode memory for that
# recipe); this script just relinks the shim, which is the fast inner loop.
#
# Gotchas baked in (each cost a rebuild — see memory):
#   -fno-stack-protector -mstackrealign : mingw emits a Linux-style %fs canary
#       that SEGVs on Windows x64; must be off. -mstackrealign keeps SSE happy.
#   -static : DLL needs only KERNEL32/ADVAPI32/msvcrt (no libgcc/ssp to ship).
#   -DOP_DISABLE_HTTP : opusfile without the HTTP/SSL stack.
# ──────────────────────────────────────────────────────────────────────────
set -e
cd "$(dirname "$0")"

GCC=${GCC:-gcc}
OUT=dist/CMM_Decoder.dll

INC=(
  -Ithird_party/opusfile/include
  -Ithird_party/opusfile/src
  -Ithird_party/opus/include
  -Ithird_party/ogg/include
  -Ibuild/ogg/include          # generated ogg/config_types.h
  -Ithird_party/dr_libs        # dr_flac.h
  -Ithird_party/stb            # stb_vorbis.c
  -Ithird_party/fdk-aac/libAACdec/include   # aacdecoder_lib.h
  -Ithird_party/fdk-aac/libSYS/include      # FDK_audio.h / machine_type.h
  -Ithird_party/minimp4        # minimp4.h
  -I.                          # CMM_Decoder.h
)

SRC=(
  CMM_Decoder.c
  dr_flac_impl.c
  dr_mp3_impl.c
  dr_wav_impl.c
  stb_vorbis_impl.c
  minimp4_impl.c
  third_party/opusfile/src/info.c
  third_party/opusfile/src/internal.c
  third_party/opusfile/src/opusfile.c
  third_party/opusfile/src/stream.c
)

# libfdk-aac.a is C++ (built once via cmake; see native-audio-decode memory), so
# the link pulls libstdc++ — kept static so the DLL still ships standalone.
mkdir -p dist
"$GCC" -O2 -shared -o "$OUT" \
  -DOP_DISABLE_HTTP \
  "${INC[@]}" \
  "${SRC[@]}" \
  build/fdk-aac/libfdk-aac.a build/opus/libopus.a build/ogg/libogg.a \
  -lm -lstdc++ -static -fno-stack-protector -mstackrealign

echo "built: $OUT"
