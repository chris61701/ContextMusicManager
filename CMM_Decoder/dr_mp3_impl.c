/* dr_mp3 implementation translation unit. Kept separate from CMM_Decoder.c so the
 * single-header library is only recompiled when dr_mp3 itself changes. We open
 * via callbacks / a FILE* we own (see CMM_Decoder.c), so stdio file I/O is off. */
#define DR_MP3_IMPLEMENTATION
#define DR_MP3_NO_STDIO
#include "dr_mp3.h"
