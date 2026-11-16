#!/usr/bin/env bash
# Build every static library CMM_Decoder.dll links, from the sources under third_party/:
#   build/ogg/libogg.a, build/opus/libopus.a   (cmake)
#   build/vorbis/libvorbis.a                   (build_vorbis.sh)
#   build/ffmpeg/lib{avformat,avcodec,avutil}.a (build_ffmpeg.sh)
# Run once after `git submodule update --init`, then build_dll.sh for the DLL itself.
#
# Needs: mingw-w64 gcc + make on PATH, cmake (CMAKE=... to point at one), and nasm for
# FFmpeg's assembly. Tested with gcc 8.1.0 (x86_64-posix-seh), cmake 4.3, nasm 2.14.
#
# Opus flags, each of which once cost a rebuild:
#   OPUS_STACK_PROTECTOR=OFF -- opus forces -fstack-protector-strong, and mingw's canary
#       reads %fs, which segfaults on Windows x64 (crashed in opus_decoder_get_size).
#   OPUS_X86_*_AVX2=OFF      -- gcc 8.1 cannot compile opus's AVX2 file; SSE is fine.
#   CMAKE_POLICY_VERSION_MINIMUM=3.5 -- ogg's and opus's CMakeLists predate cmake 4.
# Release builds define NDEBUG, so no assert() can abort the game (see build_dll.sh).
set -e
cd "$(dirname "$0")"
CMAKE=${CMAKE:-cmake}
MAKE_BIN=$(command -v make || command -v mingw32-make)

COMMON=(-G "MinGW Makefiles" -DCMAKE_MAKE_PROGRAM="$MAKE_BIN" -DCMAKE_C_COMPILER="${GCC:-gcc}"
        -DCMAKE_POLICY_VERSION_MINIMUM=3.5 -DCMAKE_BUILD_TYPE=Release -DBUILD_SHARED_LIBS=OFF)

"$CMAKE" -S third_party/ogg -B build/ogg "${COMMON[@]}" -DINSTALL_DOCS=OFF
"$CMAKE" --build build/ogg -j "${JOBS:-8}"

"$CMAKE" -S third_party/opus -B build/opus "${COMMON[@]}" \
  -DOPUS_BUILD_SHARED_LIBRARY=OFF -DOPUS_BUILD_TESTING=OFF -DOPUS_BUILD_PROGRAMS=OFF \
  -DOPUS_X86_MAY_HAVE_AVX2=OFF -DOPUS_X86_PRESUME_AVX2=OFF \
  -DOPUS_STACK_PROTECTOR=OFF -DOPUS_FORTIFY_SOURCE=OFF
"$CMAKE" --build build/opus -j "${JOBS:-8}"

bash build_vorbis.sh
bash build_ffmpeg.sh

echo "all static libs built; now run build_dll.sh"
