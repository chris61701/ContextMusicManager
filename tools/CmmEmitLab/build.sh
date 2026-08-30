#!/usr/bin/env bash
# Build CmmEmitLab. Run from anywhere; it finds the repo from its own location.
#
# It compiles FROM CMM_Decoder/ because emitlab.c #includes cmm_out.c and cmm_mix.c
# directly -- that is what makes the file-statics (g_emRx, g_emTone*, g_out) observable
# and lets the probe drive cmm_out_data_callback in place of a real device.
#
# The mingw flags are the same ones build_dll.sh explains at length: the stack protector
# emits a Linux-style %fs canary that SEGVs on Windows x64, and -mstackrealign keeps SSE
# happy. NDEBUG compiles out dr_flac's asserts.
set -e
here="$(cd "$(dirname "$0")" && pwd)"
cd "$here/../../CMM_Decoder"

GCC=${GCC:-gcc}
"$GCC" -O1 -std=gnu99 -o "$here/emitlab.exe" "$here/emitlab.c" miniaudio_impl.c \
    -Ithird_party/miniaudio -I. \
    -DMA_NO_DECODING -DMA_NO_ENCODING -DMA_NO_GENERATION \
    -DMA_NO_RESOURCE_MANAGER -DMA_NO_NODE_GRAPH -DNDEBUG \
    -lm -fno-stack-protector -mstackrealign

echo "built: tools/CmmEmitLab/emitlab.exe"
