/* matroska.c — minimal forward-only Matroska/WebM audio demuxer. See matroska.h.
 *
 * Scope on purpose: one audio track, forward packet iteration, no Cues/seek
 * index (the CMM container backend strips the whole track once and lets an
 * existing decoder own seeking). EBML/Matroska element layout per the spec:
 * https://www.matroska.org/technical/elements.html
 */
#include "matroska.h"

#include <stdlib.h>
#include <string.h>

/* ── EBML element IDs (4-byte forms include the length-descriptor marker) ── */
#define ID_EBML            0x1A45DFA3u
#define ID_SEGMENT         0x18538067u
#define ID_INFO            0x1549A966u
#define ID_TIMECODESCALE   0x2AD7B1u
#define ID_DURATION        0x4489u
#define ID_TRACKS          0x1654AE6Bu
#define ID_TRACKENTRY      0xAEu
#define ID_TRACKNUMBER     0xD7u
#define ID_TRACKTYPE       0x83u
#define ID_CODECID         0x86u
#define ID_CODECPRIVATE    0x63A2u
#define ID_AUDIO           0xE1u
#define ID_SAMPLINGFREQ    0xB5u
#define ID_CHANNELS        0x9Fu
#define ID_CLUSTER         0x1F43B675u
#define ID_TIMECODE        0xE7u
#define ID_SIMPLEBLOCK     0xA3u
#define ID_BLOCKGROUP      0xA0u
#define ID_BLOCK           0xA1u

#define TRACKTYPE_AUDIO    2

#define MKV_MAX_PRIVATE    (1 << 20)   /* 1 MB cap on CodecPrivate */
#define MKV_MAX_BLOCK      (32 << 20)  /* 32 MB cap on one block element */
#define MKV_MAX_LACES      256

struct mkv_demux {
    mkv_read_cb cb;
    void*       user;
    long long   filesize;

    long long   timecode_scale;   /* ns per timecode unit (default 1,000,000) */
    double      duration_sec;

    int            track_number;  /* selected audio track */
    int            codec;
    int            channels;
    int            sample_rate;
    unsigned char* codec_private;
    int            codec_private_len;

    /* Segment child range (where Clusters live). */
    long long   seg_pos;          /* forward cursor over Segment children */
    long long   seg_end;

    /* Cluster state. */
    int         in_cluster;
    long long   cluster_pos;      /* cursor over Cluster children */
    long long   cluster_end;
    long long   cluster_tc;       /* cluster Timecode (in timecode units) */

    /* Laced-frame queue for the block currently being handed out. */
    unsigned char* block;         /* reusable block-payload buffer */
    int            block_cap;
    int            lace_off[MKV_MAX_LACES + 1];
    int            lace_count;
    int            lace_idx;
    long long      block_tc_ns;
};

/* ── Low-level reads over the callback ─────────────────────────────────── */

static int rd_full(mkv_demux* d, long long pos, void* buf, int n)
{
    if (pos < 0 || pos + n > d->filesize) return 0;
    return d->cb(d->user, pos, buf, n) == n;
}

static int rd1(mkv_demux* d, long long pos, unsigned char* b)
{
    return rd_full(d, pos, b, 1);
}

/* Read an EBML variable-length integer at `pos`. If keep_marker, the first
 * byte's length-descriptor bit is retained (element-ID form); otherwise it is
 * stripped (size/data form). *unknown is set when all value bits are 1 (an
 * "unknown size", used for streamed Segment/Cluster). Returns the byte length,
 * or 0 on error. */
static int read_vint(mkv_demux* d, long long pos, unsigned long long* val,
                     int keep_marker, int* unknown)
{
    unsigned char b;
    if (!rd1(d, pos, &b)) return 0;
    int n = 1;
    unsigned char mask = 0x80;
    while (n <= 8 && !(b & mask)) { mask >>= 1; n++; }
    if (n > 8) return 0;

    unsigned long long v = keep_marker ? b : (unsigned long long)(b & (mask - 1));
    unsigned long long datamax = keep_marker ? 0 : (unsigned long long)(mask - 1);
    for (int i = 1; i < n; i++) {
        unsigned char c;
        if (!rd1(d, pos + i, &c)) return 0;
        v = (v << 8) | c;
        datamax = (datamax << 8) | 0xFF;
    }
    if (unknown) *unknown = (!keep_marker && v == datamax) ? 1 : 0;
    *val = v;
    return n;
}

/* Read an element header: ID (marker kept) + size (marker stripped). Sets
 * *data_pos to where the payload begins and *data_end to its end (clamped to
 * parent_end; an unknown size extends to parent_end). Returns 0 on error. */
static int read_elem(mkv_demux* d, long long pos, long long parent_end,
                     unsigned long long* id, long long* data_pos, long long* data_end)
{
    unsigned long long idv, sz;
    int idlen = read_vint(d, pos, &idv, 1, NULL);
    if (idlen == 0) return 0;
    int unknown = 0;
    int szlen = read_vint(d, pos + idlen, &sz, 0, &unknown);
    if (szlen == 0) return 0;
    long long dp = pos + idlen + szlen;
    long long de = unknown ? parent_end : dp + (long long)sz;
    if (de > parent_end) de = parent_end;
    if (dp > parent_end) return 0;
    *id = idv; *data_pos = dp; *data_end = de;
    return 1;
}

static unsigned long long read_uint(mkv_demux* d, long long pos, int len)
{
    unsigned long long v = 0;
    unsigned char b;
    for (int i = 0; i < len; i++) { if (!rd1(d, pos + i, &b)) break; v = (v << 8) | b; }
    return v;
}

static double read_float(mkv_demux* d, long long pos, int len)
{
    unsigned char buf[8];
    if (len == 4) {
        if (!rd_full(d, pos, buf, 4)) return 0.0;
        unsigned int u = ((unsigned)buf[0] << 24) | ((unsigned)buf[1] << 16) |
                         ((unsigned)buf[2] << 8) | buf[3];
        float f; memcpy(&f, &u, 4); return (double)f;
    }
    if (len == 8) {
        if (!rd_full(d, pos, buf, 8)) return 0.0;
        unsigned long long u = 0;
        for (int i = 0; i < 8; i++) u = (u << 8) | buf[i];
        double f; memcpy(&f, &u, 8); return f;
    }
    return 0.0;
}

static int codec_from_id(const char* s, int n)
{
    if (n >= 6  && memcmp(s, "A_OPUS", 6) == 0)        return MKV_CODEC_OPUS;
    if (n >= 8  && memcmp(s, "A_VORBIS", 8) == 0)      return MKV_CODEC_VORBIS;
    if (n >= 5  && memcmp(s, "A_AAC", 5) == 0)         return MKV_CODEC_AAC;
    if (n >= 8  && memcmp(s, "A_MPEG/L3", 8) == 0)     return MKV_CODEC_MP3;
    if (n >= 6  && memcmp(s, "A_FLAC", 6) == 0)        return MKV_CODEC_FLAC;
    return MKV_CODEC_UNKNOWN;
}

/* ── Tracks parsing ─────────────────────────────────────────────────────── */

/* Parse one TrackEntry; if it's an audio track in a codec we support and we
 * haven't picked one yet, record it. */
static void parse_track_entry(mkv_demux* d, long long pos, long long end)
{
    int    track_no = -1, ttype = -1, codec = MKV_CODEC_UNKNOWN;
    int    channels = 0, rate = 0;
    long long priv_pos = 0; int priv_len = 0;

    while (pos < end) {
        unsigned long long id; long long dp, de;
        if (!read_elem(d, pos, end, &id, &dp, &de)) return;
        int len = (int)(de - dp);
        switch (id) {
            case ID_TRACKNUMBER: track_no = (int)read_uint(d, dp, len); break;
            case ID_TRACKTYPE:   ttype    = (int)read_uint(d, dp, len); break;
            case ID_CODECID: {
                char tmp[32];
                int rn = len < (int)sizeof(tmp) ? len : (int)sizeof(tmp);
                if (rd_full(d, dp, tmp, rn)) codec = codec_from_id(tmp, rn);
                break;
            }
            case ID_CODECPRIVATE: priv_pos = dp; priv_len = len; break;
            case ID_AUDIO: {
                long long ap = dp;
                while (ap < de) {
                    unsigned long long aid; long long adp, ade;
                    if (!read_elem(d, ap, de, &aid, &adp, &ade)) break;
                    int al = (int)(ade - adp);
                    if (aid == ID_SAMPLINGFREQ) rate = (int)(read_float(d, adp, al) + 0.5);
                    else if (aid == ID_CHANNELS) channels = (int)read_uint(d, adp, al);
                    ap = ade;
                }
                break;
            }
            default: break;
        }
        pos = de;
    }

    if (d->track_number >= 0) return;                 /* already chose a track */
    if (ttype != TRACKTYPE_AUDIO || codec == MKV_CODEC_UNKNOWN) return;

    d->track_number = track_no;
    d->codec        = codec;
    d->channels     = channels;
    d->sample_rate  = rate;
    if (priv_len > 0 && priv_len <= MKV_MAX_PRIVATE) {
        d->codec_private = (unsigned char*)malloc((size_t)priv_len);
        if (d->codec_private && rd_full(d, priv_pos, d->codec_private, priv_len))
            d->codec_private_len = priv_len;
        else { free(d->codec_private); d->codec_private = NULL; }
    }
}

static void parse_tracks(mkv_demux* d, long long pos, long long end)
{
    while (pos < end) {
        unsigned long long id; long long dp, de;
        if (!read_elem(d, pos, end, &id, &dp, &de)) return;
        if (id == ID_TRACKENTRY) parse_track_entry(d, dp, de);
        pos = de;
    }
}

static void parse_info(mkv_demux* d, long long pos, long long end)
{
    double duration_units = 0;
    while (pos < end) {
        unsigned long long id; long long dp, de;
        if (!read_elem(d, pos, end, &id, &dp, &de)) return;
        int len = (int)(de - dp);
        if (id == ID_TIMECODESCALE) d->timecode_scale = (long long)read_uint(d, dp, len);
        else if (id == ID_DURATION) duration_units = read_float(d, dp, len);
        pos = de;
    }
    if (duration_units > 0)
        d->duration_sec = duration_units * (double)d->timecode_scale / 1e9;
}

/* ── Open ───────────────────────────────────────────────────────────────── */

mkv_demux* mkv_open(mkv_read_cb cb, void* user, long long filesize)
{
    if (!cb || filesize < 16) return NULL;

    mkv_demux* d = (mkv_demux*)calloc(1, sizeof(mkv_demux));
    if (!d) return NULL;
    d->cb = cb; d->user = user; d->filesize = filesize;
    d->timecode_scale = 1000000;   /* spec default */
    d->track_number = -1;

    /* Top level: an EBML header element followed by a Segment. */
    long long pos = 0;
    unsigned long long id; long long dp, de;
    if (!read_elem(d, pos, filesize, &id, &dp, &de) || id != ID_EBML) { free(d); return NULL; }
    pos = de;
    if (!read_elem(d, pos, filesize, &id, &dp, &de) || id != ID_SEGMENT) { free(d); return NULL; }

    d->seg_pos = dp;   /* first Segment child — start of forward cluster walk */
    d->seg_end = de;

    /* Pre-scan Segment children for Info + Tracks (metadata precedes Clusters
     * in every muxer we target). Clusters are walked lazily during iteration. */
    long long sp = dp;
    while (sp < de) {
        unsigned long long sid; long long sdp, sde;
        if (!read_elem(d, sp, de, &sid, &sdp, &sde)) break;
        if (sid == ID_INFO)        parse_info(d, sdp, sde);
        else if (sid == ID_TRACKS) parse_tracks(d, sdp, sde);
        else if (sid == ID_CLUSTER) break;   /* metadata done */
        sp = sde;
    }

    if (d->track_number < 0) { mkv_close(d); return NULL; }
    return d;
}

int mkv_get_audio(mkv_demux* d, mkv_audio_info* out)
{
    if (!d || !out) return -1;
    out->codec             = d->codec;
    out->channels          = d->channels;
    out->sample_rate       = d->sample_rate;
    out->codec_private     = d->codec_private;
    out->codec_private_len = d->codec_private_len;
    out->duration_sec      = d->duration_sec;
    return 0;
}

/* ── Block / lacing ─────────────────────────────────────────────────────── */

/* Parse the laced frame layout of the block payload already loaded in d->block
 * (length `n`, with `payload_off` pointing past track-number/timecode/flags).
 * Fills d->lace_off[0..lace_count] (cumulative byte offsets into d->block).
 * Returns 0 on success. */
static int parse_laces(mkv_demux* d, int n, int payload_off, int flags)
{
    int lacing = (flags >> 1) & 0x03;   /* 00 none, 01 Xiph, 11 EBML, 10 fixed */
    if (lacing == 0) {
        d->lace_count  = 1;
        d->lace_off[0] = payload_off;
        d->lace_off[1] = n;
        return 0;
    }

    if (payload_off >= n) return -1;
    int laces = d->block[payload_off] + 1;   /* frame count = (count-1)+1 */
    int p = payload_off + 1;
    if (laces < 1 || laces > MKV_MAX_LACES) return -1;
    d->lace_count = laces;

    int sizes[MKV_MAX_LACES];
    if (lacing == 2) {                       /* fixed: equal-sized frames */
        int total = n - p;
        if (total <= 0 || total % laces != 0) return -1;
        for (int i = 0; i < laces; i++) sizes[i] = total / laces;
    } else if (lacing == 1) {                /* Xiph: 255-summed sizes */
        for (int i = 0; i < laces - 1; i++) {
            int s = 0; unsigned char b;
            do { if (p >= n) return -1; b = d->block[p++]; s += b; } while (b == 255);
            sizes[i] = s;
        }
        sizes[laces - 1] = -1;               /* remainder */
    } else {                                 /* EBML: first vint, then deltas */
        /* Lace sizes live in d->block (already in memory), so parse the vints
         * directly from the buffer rather than via the read callback. */
        int q = p;
        /* first size: unsigned vint */
        {
            unsigned char b = d->block[q];
            int ln = 1; unsigned char mask = 0x80;
            while (ln <= 8 && !(b & mask)) { mask >>= 1; ln++; }
            if (ln > 8 || q + ln > n) return -1;
            long long v = b & (mask - 1);
            for (int i = 1; i < ln; i++) v = (v << 8) | d->block[q + i];
            q += ln;
            sizes[0] = (int)v;
        }
        for (int i = 1; i < laces - 1; i++) {   /* signed-vint deltas */
            unsigned char b = d->block[q];
            int ln = 1; unsigned char mask = 0x80;
            while (ln <= 8 && !(b & mask)) { mask >>= 1; ln++; }
            if (ln > 8 || q + ln > n) return -1;
            long long v = b & (mask - 1);
            for (int j = 1; j < ln; j++) v = (v << 8) | d->block[q + j];
            long long bias = ((long long)1 << (7 * ln - 1)) - 1;   /* EBML signed bias */
            v -= bias;
            q += ln;
            sizes[i] = sizes[i - 1] + (int)v;
        }
        sizes[laces - 1] = -1;               /* remainder */
        p = q;
    }

    /* Resolve cumulative offsets; the last frame (size -1) takes the remainder. */
    int off = p;
    for (int i = 0; i < laces; i++) {
        d->lace_off[i] = off;
        int s = sizes[i];
        if (s < 0) { off = n; }
        else { off += s; if (off > n) return -1; }
    }
    d->lace_off[laces] = n;
    /* For Xiph/EBML the last frame consumed the remainder — ensure final bound. */
    if (d->lace_off[laces - 1] > n) return -1;
    return 0;
}

/* Load the next SimpleBlock/Block for our track into d->block and set up the
 * lace queue. Returns 1 if a block was loaded, 0 at end-of-segment, <0 error. */
static int load_next_block(mkv_demux* d)
{
    for (;;) {
        if (!d->in_cluster) {
            /* Find the next Cluster among Segment children. */
            if (d->seg_pos >= d->seg_end) return 0;
            unsigned long long id; long long dp, de;
            if (!read_elem(d, d->seg_pos, d->seg_end, &id, &dp, &de)) return 0;
            d->seg_pos = de;
            if (id != ID_CLUSTER) continue;
            d->in_cluster  = 1;
            d->cluster_pos = dp;
            d->cluster_end = de;
            d->cluster_tc  = 0;
        }

        /* Walk Cluster children. */
        while (d->cluster_pos < d->cluster_end) {
            unsigned long long id; long long dp, de;
            if (!read_elem(d, d->cluster_pos, d->cluster_end, &id, &dp, &de)) {
                d->in_cluster = 0; break;
            }
            d->cluster_pos = de;

            if (id == ID_TIMECODE) {
                d->cluster_tc = (long long)read_uint(d, dp, (int)(de - dp));
                continue;
            }

            long long blk_pos = dp, blk_end = de;
            if (id == ID_BLOCKGROUP) {
                /* Find the Block child. */
                int found = 0;
                long long bp = dp;
                while (bp < de) {
                    unsigned long long bid; long long bdp, bde;
                    if (!read_elem(d, bp, de, &bid, &bdp, &bde)) break;
                    if (bid == ID_BLOCK) { blk_pos = bdp; blk_end = bde; found = 1; break; }
                    bp = bde;
                }
                if (!found) continue;
            } else if (id != ID_SIMPLEBLOCK) {
                continue;
            }

            /* Block header: track number (vint), int16 rel timecode, flags. */
            unsigned long long trackv;
            int tlen = read_vint(d, blk_pos, &trackv, 0, NULL);
            if (tlen == 0) continue;
            if ((int)trackv != d->track_number) continue;   /* other track */

            long long hp = blk_pos + tlen;
            unsigned char tc_hi, tc_lo, flags;
            if (!rd1(d, hp, &tc_hi) || !rd1(d, hp + 1, &tc_lo) || !rd1(d, hp + 2, &flags))
                continue;
            short rel = (short)((tc_hi << 8) | tc_lo);
            long long blk_data = hp + 3;
            int blk_len = (int)(blk_end - blk_data);
            if (blk_len <= 0 || blk_len > MKV_MAX_BLOCK) continue;

            if (blk_len + 1 > d->block_cap) {
                int ncap = blk_len + 16;
                unsigned char* nb = (unsigned char*)realloc(d->block, (size_t)ncap);
                if (!nb) return -1;
                d->block = nb; d->block_cap = ncap;
            }
            if (!rd_full(d, blk_data, d->block, blk_len)) continue;

            /* Lace layout is parsed over d->block; reuse parse_laces with a
             * payload that starts at 0 (we already stripped the header). */
            if (parse_laces(d, blk_len, 0, flags) != 0) continue;
            d->lace_idx = 0;
            d->block_tc_ns = (d->cluster_tc + rel) * d->timecode_scale;
            return 1;
        }
        d->in_cluster = 0;   /* cluster exhausted → look for the next one */
    }
}

int mkv_next_packet(mkv_demux* d, const unsigned char** data, int* len,
                    long long* timecode_ns)
{
    if (!d) return -1;
    if (d->lace_idx >= d->lace_count) {
        int r = load_next_block(d);
        if (r <= 0) return r;
    }
    int i = d->lace_idx++;
    int start = d->lace_off[i];
    int end   = d->lace_off[i + 1];
    if (end < start) return -1;
    *data = d->block + start;
    *len  = end - start;
    if (timecode_ns) *timecode_ns = d->block_tc_ns;   /* lace ≈ block start */
    return 1;
}

void mkv_close(mkv_demux* d)
{
    if (!d) return;
    free(d->codec_private);
    free(d->block);
    free(d);
}
