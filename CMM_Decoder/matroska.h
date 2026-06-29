/* ─────────────────────────────────────────────────────────────────────────
 * matroska.h — minimal Matroska / WebM audio demuxer for CMM_Decoder
 * ─────────────────────────────────────────────────────────────────────────
 * A compact, forward-only EBML walker that extracts ONE audio track from a
 * Matroska (.mkv) or WebM (.webm) container: its codec, setup blob
 * (CodecPrivate), channel count / sample rate, and a forward stream of
 * compressed packets. It does NOT decode and does NOT parse the Cues seek
 * index — the CMM container backend strips the whole audio track once at open
 * and remuxes it into a stream an existing decoder reads (which then owns
 * seeking), so only forward iteration is needed.
 *
 * Input is pulled through a random-access read callback, so the same demuxer
 * serves both an in-memory buffer and an on-disk FILE* (a multi-GB movie is
 * read on demand, never fully buffered).
 *
 * Not thread-safe on a single handle; the caller serialises access.
 * ───────────────────────────────────────────────────────────────────────── */
#ifndef CMM_MATROSKA_H
#define CMM_MATROSKA_H

#ifdef __cplusplus
extern "C" {
#endif

/* Audio codecs we can hand on to an existing CMM decoder backend. */
enum {
    MKV_CODEC_UNKNOWN = 0,
    MKV_CODEC_OPUS,    /* A_OPUS    */
    MKV_CODEC_VORBIS,  /* A_VORBIS  */
    MKV_CODEC_AAC,     /* A_AAC     */
    MKV_CODEC_MP3,     /* A_MPEG/L3 */
    MKV_CODEC_FLAC     /* A_FLAC    */
};

/* Random-access read: copy `size` bytes at absolute `offset` into `buf`.
 * Returns the number of bytes read (short read allowed only at EOF), or <0 on
 * error. `user` is the token passed to mkv_open. */
typedef int (*mkv_read_cb)(void* user, long long offset, void* buf, int size);

typedef struct mkv_demux mkv_demux;

typedef struct {
    int                  codec;            /* MKV_CODEC_* */
    int                  channels;
    int                  sample_rate;      /* Hz (rounded from the float in the file) */
    const unsigned char* codec_private;    /* setup blob; valid for the demuxer's life */
    int                  codec_private_len;
    double               duration_sec;     /* track/segment duration, or <=0 if unknown */
} mkv_audio_info;

/* Open a demuxer over `cb`/`user`; `filesize` is the total readable length.
 * Parses the EBML header + Segment metadata and selects the first audio track.
 * Returns NULL if the file is not Matroska/WebM or has no usable audio track. */
mkv_demux* mkv_open(mkv_read_cb cb, void* user, long long filesize);

/* Fill `out` with the selected audio track's parameters. Returns 0 on success. */
int mkv_get_audio(mkv_demux* d, mkv_audio_info* out);

/* Advance to the next compressed packet of the selected audio track.
 * On success returns 1 and points `*data`/`*len` at the packet bytes (owned by
 * the demuxer, valid until the next call) and `*timecode_ns` at its absolute
 * timestamp in nanoseconds (or -1 if unknown). Returns 0 at end-of-stream,
 * <0 on a parse error. */
int mkv_next_packet(mkv_demux* d, const unsigned char** data, int* len,
                    long long* timecode_ns);

void mkv_close(mkv_demux* d);

#ifdef __cplusplus
}
#endif
#endif /* CMM_MATROSKA_H */
