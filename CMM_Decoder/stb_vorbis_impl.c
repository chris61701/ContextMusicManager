/* stb_vorbis implementation TU. Kept separate from CMM_Decoder.c so the (large)
 * decoder isn't recompiled on every shim edit — same pattern as dr_flac_impl.c.
 *
 * stdio is LEFT ENABLED so stb_vorbis_open_file is available — the large-file
 * path hands stb_vorbis a FILE* we open ourselves via _wfopen (unicode-safe),
 * rather than its ANSI-only stb_vorbis_open_filename.
 * STB_VORBIS_NO_PUSHDATA_API : we only use the pull-data API. */
#define STB_VORBIS_NO_PUSHDATA_API
#include "stb_vorbis.c"
