/* miniaudio_impl.c — the single MINIAUDIO_IMPLEMENTATION translation unit.
 *
 * Kept separate (like dr_flac_impl.c) so the ~95k-line
 * header is compiled ONCE and not recompiled every time the cmm_out shim is
 * touched. cmm_out.c includes miniaudio.h header-only.
 *
 * We disable everything we don't use — miniaudio's own decoders/encoders, the
 * waveform generators, the high-level resource manager and node graph. CMM owns
 * all decoding; here we want only a raw playback ma_device + the ma_pcm_rb ring
 * buffer (both core, not gated by the MA_NO_* switches below). These same
 * switches are also passed on the gcc line so the header declarations match in
 * cmm_out.c — keep them in sync if you change one. */
#ifndef MA_NO_DECODING
#define MA_NO_DECODING
#endif
#ifndef MA_NO_ENCODING
#define MA_NO_ENCODING
#endif
#ifndef MA_NO_GENERATION
#define MA_NO_GENERATION
#endif
#ifndef MA_NO_RESOURCE_MANAGER
#define MA_NO_RESOURCE_MANAGER
#endif
#ifndef MA_NO_NODE_GRAPH
#define MA_NO_NODE_GRAPH
#endif

#define MINIAUDIO_IMPLEMENTATION
#include "miniaudio.h"
