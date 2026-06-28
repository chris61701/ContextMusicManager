/* dr_wav implementation translation unit. Kept separate from CMM_Decoder.c so the
 * single-header library is only recompiled when dr_wav itself changes. We open
 * via callbacks / a FILE* we own (see CMM_Decoder.c), so stdio file I/O is off.
 * DR_WAV_NO_CONVERSION_API is NOT set — we need drwav_read_pcm_frames_f32. */
#define DR_WAV_IMPLEMENTATION
#define DR_WAV_NO_STDIO
#include "dr_wav.h"
