/* dr_flac implementation translation unit. Kept separate from CMM_Decoder.c so the
 * ~500KB single-header library is only recompiled when dr_flac itself changes.
 * We decode only from memory, so stdio file I/O is compiled out. */
#define DR_FLAC_IMPLEMENTATION
#define DR_FLAC_NO_STDIO
#include "dr_flac.h"
