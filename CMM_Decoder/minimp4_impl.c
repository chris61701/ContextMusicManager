/* minimp4 implementation TU. Kept separate from CMM_Decoder.c so the big MP4
 * demuxer isn't recompiled on every shim edit (same pattern as dr_*_impl.c).
 * We only use the MP4D_* DEMUX side (the MP4E_* muxer comes
 * along in the single header but is dropped by the static linker as unused). */
#define MINIMP4_IMPLEMENTATION
#include "minimp4.h"
