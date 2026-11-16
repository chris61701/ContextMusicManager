/* ─────────────────────────────────────────────────────────────────────────
 * CMM_Decoder — native audio decode shim for ContextMusicManager
 * ─────────────────────────────────────────────────────────────────────────
 * A thin C ABI over battle-tested native decoders, so the managed mod can
 * decode without the heavy managed-heap allocation that fights Unity's
 * non-generational (Boehm) GC. Backends:
 *   - Ogg/Opus via libopusfile (pulls in libopus + libogg)  [phase 1]
 *   - FLAC via dr_flac (single-header, public domain)        [phase 2]
 * Decoders allocate their working memory natively (malloc), invisible to the
 * .NET GC; the only managed allocation left is the reusable output buffer the
 * caller owns.
 *
 * The ABI is format-agnostic in shape so vorbis/mp3 backends (libvorbisfile /
 * dr_mp3) can be added later behind the same handle pattern without disturbing
 * the managed P/Invoke surface.
 *
 * All functions are thread-compatible but NOT thread-safe on a single handle:
 * the caller serialises access to one cmm_decoder (the managed reader holds a
 * lock, exactly as the old SeekableOpusReader did).
 * ───────────────────────────────────────────────────────────────────────── */
#ifndef CMM_DECODER_H
#define CMM_DECODER_H

#include <wchar.h>   /* wchar_t for the *_open_file path params */

#ifdef __cplusplus
extern "C" {
#endif

#ifdef _WIN32
#define CMM_API __declspec(dllexport)
#else
#define CMM_API __attribute__((visibility("default")))
#endif

typedef struct cmm_decoder cmm_decoder;

/* ABI version, bumped on any breaking change to the surface below. The managed
 * side checks this on load so a mismatched DLL fails loudly instead of crashing.
 *   v1: Opus.   v2: + FLAC.   v3: + Vorbis.   v4: + open_file (large-file path).
 *   v5: + MP3 (dr_mp3) and WAV/AIFF (dr_wav) — the last managed-decoder holdouts.
 *   v6: + AAC (fdk-aac + minimp4) — AAC-LC / HE-AAC in MP4/M4A or raw ADTS.
 *   v7: + container (Matroska/WebM) — demux one audio track and remux it into an
 *        existing backend (Opus/Vorbis→Ogg, AAC→ADTS, MP3 raw, FLAC native).
 *   v8: + audio OUTPUT (miniaudio) — cmm_out_* plays interleaved-f32 PCM straight
 *        to the OS default endpoint, independent of Unity's audio engine.
 *   v9: + format converter (miniaudio) — cmm_dc_* resamples + channel-maps a voice
 *        to the mixer's fixed 48k-stereo bus so any-rate tracks crossfade.
 *   v10:+ cmm_out_queued (live cushion depth) + cmm_out_reset (consumer-side flush)
 *        for the adaptive output ring (grow under CPU starvation, snap tight on a
 *        context change).
 *   v11:+ cmm_out_set_gain (consumer-side output gain ramp so menu↔game transition fades
 *        are heard in real output time, not buried behind the ring cushion) + cmm_out_reset_gain
 *        (flush + restore gain atomically so restarting after a fade can't bleed the old track).
 *   v12:+ native MIXER (cmm_mix_*) — the per-sample decode→convert→gain→sum→write producer loop
 *        moved out of managed C# so the GC can't suspend it mid-stream (see cmm_mix.c).
 *   v13:+ native LOUDNESS analyzer (cmm_loudness_*) — the EBU R128 sweep's K-weighting + gating
 *        moved out of managed C# so the sweep allocates nothing on the GC heap (see cmm_loudness.c).
 *   v14:+ cmm_mix_get_output_stats — live output-ring health (queued/target/min depth, voices,
 *        underruns) for the debug HUD's real-time buffer tracker.
 *   v15:+ HEADLESS output for the offline stress harness — cmm_out_open_null / cmm_out_drain and
 *        cmm_mix_start_ex(headless) / cmm_mix_pump_out: run the real mixer with no ma_device,
 *        drained on the caller's clock, so the mixer's native lifecycle churns at compressed
 *        speed for leak hunting (in-game paths unchanged).
 *   v16:+ EMITTER voices (cmm_mix_add_emitter / cmm_mix_remove_emitter) — an independent
 *        positional layer that plays ALONGSIDE the music voices (a diegetic world speaker keeps
 *        playing while the soundtrack crossfades underneath), exempt from the music crossfade and
 *        the voice-count eviction. v16 renders them centered; spatialization/coloration follow.
 *   v17:+ SPATIAL emitters — cmm_mix_add_emitter now takes a world position + min/max distance,
 *        cmm_mix_set_listener pushes the listener basis (pos + forward + right, straight from the
 *        camera so there's no handedness ambiguity), and cmm_mix_set_emitter_pos moves a live
 *        emitter. Emitter voices are mono-downmixed then panned L/R by bearing and attenuated by
 *        distance (hand-rolled, no HRTF).
 *   v18:+ MULTI-POINT emitters — cmm_mix_set_emitter_points gives one emitter voice the positions of
 *        EVERY speaker playing the station; the render sums each point's pan+attenuation, so they are
 *        all heard at once (a smooth blend, no nearest-only snapping) from a single decode.
 *   v19:+ per-speaker OCCLUSION gain — cmm_mix_set_emitter_points also takes a per-point gain (walls
 *        between listener and speaker muffle it), folded into that point's contribution.
 *   v20:  CONSUMER-SIDE emitter spatialization — no managed-ABI signature change, but the pan/distance/
 *        occlusion now runs in the audio OUTPUT callback (cmm_out) from the freshest listener state
 *        (the producer feeds a dry-mono emitter ring + relays the spatial state), so panning tracks
 *        head movement at output latency (~10ms) instead of behind the ~200ms producer ring cushion.
 *   v21:+ LIVE emitter TUNING — cmm_mix_set_emitter_tuning(minDist, maxDist, falloffExp, muffle) pushes
 *        the spatial curve params (full-volume plateau radius, audible range, falloff steepness, and
 *        distance-muffle low-pass strength) live from config, so they can be dialled in-game without a
 *        rebuild. The output spatializer reads them each callback in place of the old baked constants.
 *   v22:+ STEREO WIDTH — cmm_mix_set_emitter_tuning gains a `stereoWidth` (0..1): the pan position is
 *        scaled by it so a hard-left/right source no longer NULLS the far ear (equal-power puts it at
 *        cos=0); <1 keeps both ears alive, matching how a source at 90° is still heard on the far side.
 *   v23:+ EMITTER REVERB — cmm_mix_set_emitter_reverb(wet, room, damp) drives a Freeverb-style room
 *        reverb on the emitter voice from the managed room probe. The WET path is attenuated by REAL
 *        distance only (not occlusion), so behind a wall the dry sound drops but the reflections carry
 *        — the "it bounced / it's in another room" cue. (Relayed via cmm_out_set_emitter_reverb.)
 *   v24:+ EMITTER MASTER — cmm_mix_set_emitter_master(vol) gives the world-radio its OWN master, separate
 *        from the music master (g_masterVol). Managed feeds the game music SLIDER × the Radio context's
 *        volume, so the radio scales only to the slider (+ context level), not VolumeModifier or the
 *        current music context's volume.
 *   v25:+ EMITTER CONTINUITY — cmm_mix_add_emitter gains a `startFrame` so a station rejoined mid-song
 *        seeks to its virtual playhead (the station keeps "broadcasting" on a wall-clock while idle;
 *        managed computes the offset). Mirrors cmm_mix_add's resume seek.
 *   v26:+ RADIO COLORATION — cmm_mix_set_emitter_color(drive, staticLvl, voice) drives the "through a
 *        speaker" DSP on the emitter mono: band-pass voicing (small-speaker body), soft-clip drive
 *        (level-compensated), and a static-hiss bed, all applied BEFORE the distance low-pass and the
 *        reverb feed so the room hears the colored speaker output. All three 0..1, live-tunable.
 *   v27:+ DEPTH-GRADED REVERB — cmm_mix_set_emitter_reverb gains `depth` (air-route blocks from the
 *        listener to the station, walls excluded) + `depthAmt` (0..1 strength): the wet gets QUIETER,
 *        DARKER (a low-pass on the reverb output) and MORE DAMPED with depth, the way a reverberant
 *        field really decays down a tunnel — near the radio a bright splash, deep in the cave a dark
 *        sinking rumble, continuous everywhere (replaces "flat wet until the range edge").
 *   v28:+ TWO-BUS REVERB — cmm_mix_set_emitter_reverb gains `localWet`: a SECOND Freeverb (the LOCAL
 *        bus) is fed the ARRIVING dry (post-attenuation/occlusion/muffle), so your own space's reverb
 *        follows every dry rule for free — walls, water, distance and the hard-stop silence it with the
 *        dry (no seal knob). The original bus becomes the CARRY (next-room cue): un-occluded feed,
 *        seal-gated + depth-graded + route-panned by the managed side.
 *   v29:  CARRY range curve reshaped (no interface change): the wet's distance attenuation is now a
 *        power decay over the whole reach (exponent = dry falloff × 0.5) instead of flat-until-85% —
 *        kills the constant-loudness reverb plateau that trailed the player after the dry died.
 *   v30:+ EMITTER ACOUSTIC RESET (cmm_mix_emitter_reset_acoustics): clears the emitter's reverb tails
 *        + LPF/pan/mix slews + per-slot smooth state before a FRESH station voice starts, so a
 *        destroyed radio's still-ringing reverb never re-voices under its replacement. Also (no
 *        interface change) a freshly-activated point slot now initializes its smoothed occlusion/
 *        position from the target instead of gliding from the previous occupant's stale state.
 *   v31:+ MULTI-STATION emitters — up to CMM_MAX_EMIT_VOICES stations render SIMULTANEOUSLY, each on
 *        its own SLOT (own dry-mono ring, point set, position/occlusion slews, distance LPF and
 *        coloration filter state). cmm_mix_add_emitter gains a `slot` (0..CMM_MAX_EMIT_VOICES-1) the
 *        managed layer assigns per station; voices sharing a slot sum (a track-advance crossfade).
 *        REVERB stays ONE shared pair of room buses (reverb is a property of the listener's room,
 *        not the station): cmm_mix_set_emitter_reverb now takes PER-SLOT carry-send + depth arrays —
 *        each slot feeds the CARRY bus input scaled by its own (seal-gated, wet-attenuated,
 *        depth-graded) send, and the LOCAL bus off its arriving dry; output darkening/damping/pan
 *        follow the DOMINANT slot. Input-side send scaling replaces the old output wet gain
 *        (linear-equivalent for one station; the out-of-range tail now decays naturally).
 *   v32:+ SLOT FLUSH (cmm_mix_emitter_flush_slot → per-slot flag serviced by the callback): discard
 *        a slot's queued dry ring + reset its render state (LPF/color/slews) when a DIFFERENT
 *        station acquires the slot — without it, up to a cushion's worth of the previous station's
 *        audio played through the new station's speaker positions on a steal/identity change.
 *   v33:+ OUTPUT-GAIN HOLD (cmm_mix_hold_output) — suppresses cmm_mix_add's output-gain revive
 *        while the SP pause duck owns the gain, so a voice added mid-pause (music EOF advance,
 *        radio/emitter start) can no longer un-duck the device through the pause screen.
 *   v34:+ PER-POINT RENDER FLAGS + PER-CLASS COLORATION — cmm_mix_set_emitter_points gains a
 *        per-point `flags` array (bit0 = HAM class; bits 1-2 reserved for the stereo-pair channel
 *        split). Each class (speaker/ham) is an independently colored + low-passed signal within
 *        its slot; cmm_mix_set_emitter_color gains the ham set's drive/static/voice so a radioHam
 *        always sounds like a small communications speaker while shelf speakers stay clean. The
 *        carry reverb feeds the DOMINANT (loudest) class's signal.
 *   v35:+ RADIO UI SFX (cmm_mix_play_sfx) — synthesized one-shots (tuning static burst, power
 *        on/off clicks) rendered into the music bus outside the voice list, so the crossfade/
 *        stinger/eviction machinery never touches them and no audio assets ship.
 *   v36:  KEEPALIVE STATIC + cmm_mix_stop_sfx — a static ping while one is live EXTENDS the hash
 *        (one seamless noise however long the station join takes; overlapping one-shot bursts
 *        audibly double-fired), and stop_sfx releases it (~90ms) the moment the voice lands.
 *   v37:+ TUNEABLE STATIC (cmm_mix_set_static_tone) — band edges (Hz) + CRACKLE depth (sample-
 *        and-hold amplitude sputter; 0 = smooth hiss) captured at every ping, so settings.xml
 *        edits are heard mid-hash while dialing.
 *   v38:+ TUNEABLE CLICKS (cmm_mix_set_click_tone) — per-click tone cap Hz, SNAP (bright ping vs
 *        low thump balance) and length ms for the power-on/off clicks, captured per click.
 *   v39:  DIAL-DETENT CLICKS — cmm_mix_set_click_tone gains per-click BODY (a damped two-pole
 *        resonator at BodyHz: the knock of the dial's shell) and DOUBLE (a second micro-impact
 *        ~12ms after the first: the detent's spring release then the stop landing). Body/Double
 *        at 0 reproduce the v38 click exactly.
 *   v40:  DEVICE LIFECYCLE OFF g_lock (no interface change) — ma_device_init/start/uninit used
 *        to run while HOLDING the mixer lock (lazy open inside cmm_mix_add = the spawn stinger,
 *        reinit inside the producer). A wedged WASAPI endpoint there froze every lock caller —
 *        including the game main thread (SetOutputGain/get_state at spawn) — forever. Control
 *        threads now only flag g_deviceWanted; the producer thread is the sole device owner and
 *        opens/closes OUTSIDE the lock, so a stalled backend idles the producer (which has
 *        nothing to render anyway) while every control ABI keeps returning immediately. Also:
 *        cmm_mix_play_sfx no longer drops the click during the open window — the voice arms and
 *        renders when the device arrives.
 *   v42:+ TONE FROM OCCLUSION — cmm_mix_set_emitter_occl_tone(enable, fullDb). The emitter's
 *         dullness was read off EFFECTIVE distance, which carries occlusion as added blocks; how
 *         many blocks an attenuation is worth depends on where you stand, so one wall dulled far
 *         more up close than at range and a listener on top of a speaker heard it muffled with
 *         nothing in the way. Enabled, the mixer recovers the pure occlusion ratio (this point's
 *         attenuation over what it would be unobstructed) and dulls from that; real distance still
 *         dulls on its own. Off by default — the shipped model is unchanged.
 *   v41:  SLEWED MASTERS (no interface change) — cmm_mix_set_master / cmm_mix_set_emitter_master
 *        now set a TARGET the producer walks toward at a bounded rate (25ms full-scale) instead
 *        of a value applied as a per-block constant. An instant master change was a step
 *        discontinuity at a block boundary, i.e. an audible click; the game's volume sliders move
 *        in 5% notches and managed re-pushes at 10Hz, so dragging one produced a train of them.
 *        Surfaced when CMM started honouring the Overall/Ambient sliders (both masters now move
 *        with them). The ramp is per FRAME so channels share a gain (no stereo shift), emitter
 *        slots all ramp from one start value so the K slots stay locked, and both masters snap to
 *        target on device attach so a fresh device opens at level instead of fading in.
 *
 *   v47: THE ARRIVAL. cmm_mix_set_receiver_arrival + cmm_mix_set_receiver_radio. Losing a station
 *        was already right ("when it fades out, it's perfect"); coming back was wrong for four
 *        rounds because NOTHING was happening to the music. Measured, the deepest treatment an
 *        arriving station got was -3 dB at 3.75 kHz with its bass, dynamics and image intact --
 *        the source file, fading up, with noise over it, which is what it sounded like. Now the
 *        programme is band-limited HARD at both ends (400 Hz - 2.6 kHz at full depth, two poles
 *        each, low cut BEFORE the saturator so the drive makes grit and not mud), driven, squashed
 *        8:1 and made up, then released as it locks. Also: the hiss outlasts the tuning instead of
 *        clearing while the station is still opening; the treble window is keyed to ramp position
 *        rather than SNR (which saturates too early to be usable); and the stereo decoder LATCHES
 *        late with hysteresis instead of gliding -- without the authentic decoder clunk, which was
 *        a one-sample 0.34 DC step and measured as the single largest discontinuity in the whole
 *        transition.
 *
 *   v48: ONE RECEIVER, AT THE BUS. cmm_mix_set_voice_receiver is replaced by
 *        cmm_mix_set_voice_radio (mark a voice as programme) + cmm_mix_set_receiver_signal
 *        (the signal, and it belongs to nothing). The noise used to be generated per VOICE,
 *        which was right when a radio meant one voice and wrong the moment a dial press meant
 *        two: changing station overlaps the station being left with the station being loaded,
 *        and the loading one HOLDS AT SIGNAL 0, which is maximum noise. Signal does not
 *        attenuate a voice, so both emitted full-scale hiss at once from independent generators
 *        with independent wander and flutter -- two uncorrelated roars, which is not a louder
 *        receiver but two receivers. A THIRD noise existed as well: a separate synthesized hash
 *        pinged every 250 ms whenever no voice existed at all, on a completely different
 *        generator (fixed band-pass, sample-and-hold sputter, no de-emphasis, no wander, no
 *        clicks). Three sounds for one event. Now radio voices contribute programme only into a
 *        radio sub-bus and one bus-level stage generates the noise, sums, and applies the shared
 *        hi-cut and width. It runs with no voices at all, which is what retires the hash.
 *
 *   v49: cmm_out_drain_into / cmm_mix_pump_capture -- the headless pump hands the frames back
 *        instead of discarding them. The headless path existed only for leak-hunting, so it had
 *        no way to return audio; that meant every question about how the radio SOUNDS could only
 *        be answered by starting the game and loading a world, several minutes a go, and judging
 *        by ear. With this a station change renders to a WAV in seconds and can be diffed against
 *        the reference render directly. *
 *   v52: cmm_meminfo / cmm_meminfo_reset_peak -- NATIVE heap accounting. Every memory
 *        instrument the mod had watched the MANAGED heap (the HUD's Heap/GC rows, CMMPerf's
 *        per-phase _allocB, the CmmSession soak's forced-GC drift); decode and mix are C, so
 *        the only thing aimed at them was the process working set, which cannot resolve a few
 *        MB/hour -- four seeds of identical code gave RSS slopes from -1.51 to +2.49 MB/sim-h,
 *        not even a stable sign. The DLL is one gcc link, so -Wl,--wrap on malloc/free/realloc/
 *        calloc routes our allocations AND the static libs' (fdk-aac, vorbis, opus, ogg,
 *        libstdc++'s operator new) through cmm_mem.c's counters.
 *
 *   v53: cmm_lprog_begin/_step/_free -- PROGRESSIVE loudness. Integrated loudness is a
 *        whole-file quantity, so the analyzer could only answer after decoding to EOF;
 *        a 47-minute podcast therefore played at neutral gain (measured ~7 dB under the
 *        target) for the ~15 s that took, on every rejoin. The blocks are already
 *        accumulated incrementally and cmm_loudness_finish is read-only over them, so
 *        the whole change is a handle that keeps the decoder and analyzer alive between
 *        bounded decode slices and gates over everything fed so far. The value at eof is
 *        identical to the one-shot's; what is new is having a usable one before then. */
#define CMM_DECODER_ABI 62

/* Max speaker positions summed onto one emitter voice — shared by the mixer (voice storage) and the
 * output (consumer-side spatializer). */
#define CMM_MAX_EMIT_PTS 8

/* Max SIMULTANEOUSLY-VOICED stations (native slots). The station catalog can be any size; the
 * managed layer voices only the K nearest audible stations, each pinned to one slot here. */
/* Six lanes: three carry STATIONS, three are reserved for TUNING (a sweep, or a speaker sitting
 * on the off-air carrier). The split is enforced on the managed side; native just sizes for it.
 *
 * Three was the whole cause of the "static never played" class. A sweep needs a lane of its own,
 * and so does a carrier -- but they competed with stations for the same three, and the condition
 * that makes a sweep necessary (two or three other stations audible nearby) is exactly the
 * condition that leaves none spare. A tuning lane costs a ring and a memcpy: it carries a MIRROR
 * of an already-decoded station or pure synthesis, never a decoder of its own. */
#define CMM_MAX_EMIT_VOICES 6
CMM_API int CMM_Decoder_abi(void);

/* ── Native heap accounting (see cmm_mem.c) ───────────────────────────────
 * `live` is the leak-hunt number: flat across hours of play is a clean bill of health for
 * the native side, rising is the leak the working set could never resolve. Any pointer may
 * be NULL. reset_peak re-arms the high-water mark to the current live figure so a soak can
 * measure one phase instead of everything since load; it never touches `live`. */
CMM_API void cmm_meminfo(long long *live, long long *peak,
                         long long *allocs, long long *frees, long long *bytesTotal);
CMM_API void cmm_meminfo_reset_peak(void);

/* ── File-based opens (large-file path) ───────────────────────────────────
 * The *_open_memory functions copy the whole file into RAM; for files too big
 * to buffer the managed side calls these instead, which stream from disk via a
 * native FILE* the decoder owns for its lifetime. `path` is a wide (UTF-16 on
 * Windows) string — opened with _wfopen so non-ASCII music paths work (bare
 * fopen is ANSI on Windows). Same out-params / read / seek / close contract as
 * the matching *_open_memory + *_read_float + *_seek + *_close functions: a
 * file-opened handle is used and freed through the exact same family (e.g. open
 * with cmm_flac_open_file, read with cmm_flac_read_float, free with
 * cmm_flac_close). Returns NULL on error (file missing / decode-init failure). */
CMM_API cmm_decoder* cmm_opus_open_file(const wchar_t* path, int* channels,
                                        int* sampleRate, long long* totalFrames, int* err);
CMM_API cmm_decoder* cmm_flac_open_file(const wchar_t* path, int* channels,
                                        int* sampleRate, long long* totalFrames, int* err);
CMM_API cmm_decoder* cmm_vorbis_open_file(const wchar_t* path, int* channels,
                                          int* sampleRate, long long* totalFrames, int* err);
CMM_API cmm_decoder* cmm_mp3_open_file(const wchar_t* path, int* channels,
                                       int* sampleRate, long long* totalFrames, int* err);
CMM_API cmm_decoder* cmm_wav_open_file(const wchar_t* path, int* channels,
                                       int* sampleRate, long long* totalFrames, int* err);
CMM_API cmm_decoder* cmm_aac_open_file(const wchar_t* path, int* channels,
                                       int* sampleRate, long long* totalFrames, int* err);

/* Open an Ogg/Opus stream from an in-memory buffer. The shim COPIES `data` into
 * its own native allocation, so the caller's (possibly pooled/reused) buffer can
 * be overwritten or freed immediately after this returns.
 *
 * On success returns a non-NULL handle and fills:
 *   channels    — decoded channel count
 *   sampleRate  — always 48000 for Opus
 *   totalFrames — total frames per channel (may be -1 for unseekable/unknown)
 * On failure returns NULL and sets *err to the libopusfile error code (negative).
 * Any out-param pointer may be NULL if the caller doesn't want it. */
CMM_API cmm_decoder* cmm_opus_open_memory(const unsigned char* data, long long len,
                                          int* channels, int* sampleRate,
                                          long long* totalFrames, int* err);

/* Decode up to `maxFrames` frames (per channel) of interleaved float PCM into
 * `out` (which must hold at least maxFrames*channels floats). Loops internally
 * to fill the request, minimising P/Invoke crossings. Returns the number of
 * frames actually written: 0 at end-of-stream, <0 on a decode error. */
CMM_API int cmm_opus_read_float(cmm_decoder* d, float* out, int maxFrames);

/* Seek to absolute frame `frame` (per channel). Returns 0 on success, <0 on error. */
CMM_API int cmm_opus_seek(cmm_decoder* d, long long frame);

/* Free the decoder and the copied source buffer. NULL-safe. */
CMM_API void cmm_opus_close(cmm_decoder* d);

/* ── FLAC (dr_flac) ───────────────────────────────────────────────────────
 * Same handle/lifetime contract as the Opus functions. Unlike Opus, FLAC
 * preserves its NATIVE sample rate (e.g. 44100/48000/96000), so *sampleRate is
 * read from the stream rather than fixed. The shim COPIES `data` (dr_flac reads
 * the buffer on demand for the life of the decoder, so the caller's pooled
 * buffer may be overwritten/freed immediately after this returns). */
CMM_API cmm_decoder* cmm_flac_open_memory(const unsigned char* data, long long len,
                                          int* channels, int* sampleRate,
                                          long long* totalFrames, int* err);

/* Decode up to `maxFrames` frames (per channel) of interleaved float PCM into
 * `out` (>= maxFrames*channels floats). Returns frames written: 0 at EOF. */
CMM_API int cmm_flac_read_float(cmm_decoder* d, float* out, int maxFrames);

/* Seek to absolute frame `frame` (per channel). Returns 0 on success, <0 on error. */
CMM_API int cmm_flac_seek(cmm_decoder* d, long long frame);

/* Free the decoder and the copied source buffer. NULL-safe. */
CMM_API void cmm_flac_close(cmm_decoder* d);

/* ── Vorbis (libvorbisfile) ────────────────────────────────────────────────
 * Same handle/lifetime contract as the others. Like FLAC, Vorbis preserves its
 * NATIVE sample rate, so *sampleRate is read from the stream. open_memory COPIES
 * `data` (the caller's pooled buffer may be overwritten/freed immediately after
 * this returns) and feeds it to libvorbisfile via in-memory ov_callbacks; the
 * open_file path streams big files over raw HANDLE/FILE* callbacks like Opus. */
CMM_API cmm_decoder* cmm_vorbis_open_memory(const unsigned char* data, long long len,
                                            int* channels, int* sampleRate,
                                            long long* totalFrames, int* err);

/* Decode up to `maxFrames` frames (per channel) of interleaved float PCM into
 * `out` (>= maxFrames*channels floats). Returns frames written: 0 at EOF. */
CMM_API int cmm_vorbis_read_float(cmm_decoder* d, float* out, int maxFrames);

/* Seek to absolute frame `frame` (per channel). Returns 0 on success, <0 on error. */
CMM_API int cmm_vorbis_seek(cmm_decoder* d, long long frame);

/* Free the decoder and the copied source buffer. NULL-safe. */
CMM_API void cmm_vorbis_close(cmm_decoder* d);

/* ── MP3 (dr_mp3) ─────────────────────────────────────────────────────────
 * Same handle/lifetime contract as the others; preserves the stream's native
 * sample rate. The shim COPIES `data` (dr_mp3 reads it on demand for the life of
 * the decoder). Total frame count is computed at open by a one-pass scan (as
 * NAudio's Mp3FileReader indexed at construction), so *totalFrames is exact. */
CMM_API cmm_decoder* cmm_mp3_open_memory(const unsigned char* data, long long len,
                                         int* channels, int* sampleRate,
                                         long long* totalFrames, int* err);

/* Decode up to `maxFrames` frames (per channel) of interleaved float PCM into
 * `out` (>= maxFrames*channels floats). Returns frames written: 0 at EOF. */
CMM_API int cmm_mp3_read_float(cmm_decoder* d, float* out, int maxFrames);

/* Seek to absolute frame `frame` (per channel). Returns 0 on success, <0 on error. */
CMM_API int cmm_mp3_seek(cmm_decoder* d, long long frame);

/* Free the decoder and the copied source buffer. NULL-safe. */
CMM_API void cmm_mp3_close(cmm_decoder* d);

/* ── WAV / AIFF (dr_wav) ──────────────────────────────────────────────────
 * One backend covers both RIFF/WAVE and AIFF containers (dr_wav detects the
 * container and converts any sample format to f32 on read). Same handle/lifetime
 * contract; preserves the native sample rate. The shim COPIES `data`. */
CMM_API cmm_decoder* cmm_wav_open_memory(const unsigned char* data, long long len,
                                         int* channels, int* sampleRate,
                                         long long* totalFrames, int* err);

/* Decode up to `maxFrames` frames (per channel) of interleaved float PCM into
 * `out` (>= maxFrames*channels floats). Returns frames written: 0 at EOF. */
CMM_API int cmm_wav_read_float(cmm_decoder* d, float* out, int maxFrames);

/* Seek to absolute frame `frame` (per channel). Returns 0 on success, <0 on error. */
CMM_API int cmm_wav_seek(cmm_decoder* d, long long frame);

/* Free the decoder and the copied source buffer. NULL-safe. */
CMM_API void cmm_wav_close(cmm_decoder* d);

/* ── AAC / ALAC (FFmpeg: mov demuxer + aac/alac decoders) ──────────────────────────────────────────────
 * Covers AAC-LC, HE-AAC (v1/v2) and ALAC in the MP4 family (.m4a/.mp4/.m4v/.3gp/.mov,
 * ordinary or fragmented, including the audio track of a video) and AAC in a raw
 * ADTS .aac stream. A single open detects the container from the leading bytes;
 * same handle/lifetime contract as the others. Output is interleaved f32 at the
 * native rate (HE-AAC's SBR rate is known after the first frame is decoded), and
 * MP4 encoder priming/padding is trimmed (gapless) when the file records it.
 * Memory opens COPY `data`. File opens read the whole file (raw Win32) up to
 * CMM_BUFFER_MAX and stream larger MP4s over a raw HANDLE. *totalFrames is exact:
 * from the MP4 index / edit list / iTunSMPB, or for ADTS by walking frame headers;
 * ADTS seeks restart from the stream origin and decode forward. */
CMM_API cmm_decoder* cmm_aac_open_memory(const unsigned char* data, long long len,
                                         int* channels, int* sampleRate,
                                         long long* totalFrames, int* err);

/* Decode up to `maxFrames` frames (per channel) of interleaved float PCM into
 * `out` (>= maxFrames*channels floats). Returns frames written: 0 at EOF. */
CMM_API int cmm_aac_read_float(cmm_decoder* d, float* out, int maxFrames);

/* Seek to absolute frame `frame` (per channel). Returns 0 on success, <0 on error. */
CMM_API int cmm_aac_seek(cmm_decoder* d, long long frame);

/* Free the decoder and the copied source buffer. NULL-safe. */
CMM_API void cmm_aac_close(cmm_decoder* d);

/* ── Container (Matroska / WebM) ──────────────────────────────────────────
 * Demux ONE audio track from a .mkv/.webm and losslessly remux it into a
 * stream an existing backend opens (Opus/Vorbis → Ogg, AAC → ADTS, MP3 raw,
 * FLAC native), then decode through that backend. The whole track is demuxed
 * once at open, so the file open streams a large movie from disk without
 * buffering the video; the in-memory open is for small files / the loudness
 * sweep. Same handle/lifetime contract as the other backends — use the handle
 * only through the cmm_container_* family. Returns NULL if the file is not
 * Matroska/WebM, has no audio track, or uses an unsupported audio codec. */
CMM_API cmm_decoder* cmm_container_open_memory(const unsigned char* data, long long len,
                                               int* channels, int* sampleRate,
                                               long long* totalFrames, int* err);
CMM_API cmm_decoder* cmm_container_open_file(const wchar_t* path, int* channels,
                                             int* sampleRate, long long* totalFrames, int* err);

/* Decode up to `maxFrames` frames (per channel) of interleaved float PCM into
 * `out` (>= maxFrames*channels floats). Returns frames written: 0 at EOF. */
CMM_API int cmm_container_read_float(cmm_decoder* d, float* out, int maxFrames);

/* Seek to absolute frame `frame` (per channel). Returns 0 on success, <0 on error. */
CMM_API int cmm_container_seek(cmm_decoder* d, long long frame);

/* Free the decoder (and the inner backend it owns). NULL-safe. */
CMM_API void cmm_container_close(cmm_decoder* d);

/* ── Audio output (miniaudio) ─────────────────────────────────────────────
 * Plays interleaved 32-bit-float PCM to the OS DEFAULT output endpoint in shared
 * mode — i.e. through the same device, and under the same Windows volume-mixer
 * entry, as the host process's other audio (so CMM shares 7 Days' volume slider
 * rather than appearing separately). This is the playback counterpart to the
 * decoders above: the managed mixer thread decodes/mixes/gains into f32 frames
 * and pushes them here, bypassing Unity's AudioSource/AudioClip entirely.
 *
 * Threading: a handle backs a single-producer/single-consumer ring buffer. The
 * caller's mixer thread is the only writer (cmm_out_write); miniaudio's realtime
 * audio thread is the only reader. Do NOT call cmm_out_write from two threads.
 * cmm_out_open starts the device immediately (it emits silence until fed). */
typedef struct cmm_out cmm_out;

/* Open + start a playback device. `ringFrames` is the ring capacity in frames
 * (<=0 selects ~200ms at the given rate). Returns NULL on failure with *err set
 * to the miniaudio ma_result, or a small negative for bad args / OOM. */
CMM_API cmm_out* cmm_out_open(int sampleRate, int channels, int ringFrames, int* err);

/* Enqueue up to `frames` interleaved-f32 frames (data holds frames*channels
 * floats). Non-blocking: returns frames actually accepted; retry the remainder
 * after a short sleep when the ring is full. */
CMM_API int cmm_out_write(cmm_out* o, const float* data, int frames);

/* Frames of free space in the ring right now (lets the producer pace itself). */
CMM_API int cmm_out_avail(cmm_out* o);

/* Frames currently queued for playback — the live cushion / output latency. The
 * producer targets a fill level from this rather than always topping the ring off. */
CMM_API int cmm_out_queued(cmm_out* o);
/* v43 — the EMITTER slot rings had no cushion of their own: the producer paces off the MUSIC
 * ring's deficit and writes that same frame count to every slot, and the consumer drains both
 * equally, so a slot that started empty stayed empty for its whole life. Music had 200 ms of
 * protection against a late producer; the radios had one poll. Short reads were zero-filled with
 * no counter, so every stat said "underruns 0" while the radios dropped out.
 *   set_emitter_prime: cushion depth the slots should hold (pushed from the music ring's depth).
 *   emitter_stat: 0 = frames queued, 1 = frames lost to starvation, 2 = times re-cushioned. */
CMM_API void cmm_out_set_emitter_prime(cmm_out* o, int frames);
CMM_API int  cmm_out_emitter_stat(cmm_out* o, int slot, int what);

/* v56 — make emitter lane `dst` render a COPY of lane `src`'s dry audio (src < 0 clears).
 * One decode, two lanes, two independent receivers: the source keeps playing untouched for every
 * speaker on it while the copy belongs to a single speaker being retuned. Sample-aligned by
 * construction, so the two never comb. Copied after the voices land and before any receiver runs. */
CMM_API void cmm_mix_set_lane_mirror(int dst, int src);

/* v60 — scale a mirrored lane's COPY, before its receiver runs. A mirrored lane carries a
 * station and its receiver carries the static; by the time a per-point gain applies they are
 * one signal, so fading the point fades both. This fades only the programme, which is what
 * "the speaker fades, not the stream" requires. */
CMM_API void cmm_mix_set_lane_mirror_gain(int slot, float gain, float rampSeconds);

/* v61 — what a lane is ACTUALLY doing, as opposed to what it was asked to do.
 * what: 0 = receiver signal (<0 when not armed), 1 = the dry lane's RMS after its receiver
 * and before the master, 2 = mirror gain (<0 when not mirroring), 3 = carrier envelope
 * (<0 when no carrier). Level was the one thing never logged, and "the static was not
 * audible" and "the music came in hot" are both statements about level. */
CMM_API float cmm_mix_emitter_lane_stat(int slot, int what);

/* v57 — a steady off-air tone on one emitter lane, in place of both programme and hiss
 * (hz <= 0 clears). What a station that will not open gets instead of silence: a set that finds
 * nothing sits on the carrier rather than going quiet. Positional, like everything else on a
 * lane — which the SFX bus can never be. */
CMM_API void cmm_mix_set_emitter_tone(int slot, const float* hz, const float* levels,
                                      int count, float rampSeconds);

/* Request a flush: the next audio callback discards all queued frames, snapping the
 * cushion to empty (then it refills tight). Only raises a flag — safe from the
 * producer/control thread; the actual discard happens on the callback. */
CMM_API void cmm_out_reset(cmm_out* o);

/* Set the consumer-side output gain (0..1), optionally ramped over rampFrames output
 * frames (<=0 snaps). Applied by the audio callback, so a transition fade is heard in
 * real output time regardless of cushion depth / producer starvation. */
CMM_API void cmm_out_set_gain(cmm_out* o, float target, int rampFrames);

/* Flush the queued (stale) frames AND restore the output gain to `target` atomically inside
 * the callback — use when restarting music after a fade so no stale full-volume frame bleeds
 * through at the restored gain. */
CMM_API void cmm_out_reset_gain(cmm_out* o, float target);

/* Stop the device and free the handle. NULL-safe. */
CMM_API void cmm_out_close(cmm_out* o);

/* v15 — HEADLESS output for the offline stress harness. Same ring, NO ma_device: the
 * caller drains it itself via cmm_out_drain, so the mixer producer runs at whatever
 * pace the caller pumps (compressed), with no audio hardware. Everything else behaves
 * identically to a real device. cmm_out_close skips device teardown for these. */
CMM_API cmm_out* cmm_out_open_null(int sampleRate, int channels, int ringFrames, int* err);

/* Drain a headless ring INTO a caller buffer (interleaved floats, frames * channels), applying
 * the consumer gain ramp exactly as the realtime callback does. The offline radio simulator's
 * only route to the mixer's real output. */
CMM_API int cmm_out_drain_into(cmm_out* o, float* dst, int frames);
CMM_API int cmm_mix_pump_capture(float* dst, int frames);

/* Consume up to `frames` from a headless ring (services a pending flush first, like the
 * realtime callback), discarding them. Returns frames consumed. SPSC: one consumer only. */
CMM_API int cmm_out_drain(cmm_out* o, int frames);

/* ── Consumer-side emitter spatialization (v20, internal — the mixer calls these) ──
 * The producer (cmm_mix) feeds the emitter's DRY MONO into a second ring and relays the live
 * spatial state; the audio callback pans/attenuates/occludes it from that freshest state and adds
 * it to the output, so head-turn panning is heard at output latency, not behind the ring cushion.
 * These are not part of the managed ABI (same-DLL cross-TU calls), so they are plain (unexported). */

/* Enqueue up to `frames` mono f32 frames of one SLOT's dry signal (v31: one ring per station slot).
 * Non-blocking; returns frames accepted (SPSC — the producer is the sole writer per ring). */
/* Consumer-side truth for one emitter slot: 0 ptCount, 1 applied dry gain, 2 slewed virtual
 * distance, 3 real distance in blocks. What the ear got, not what we published. */
float cmm_out_slot_applied(cmm_out* o, int slot, int what);

int cmm_out_write_emitter(cmm_out* o, int slot, const float* mono, int frames);

/* Relay one SLOT's live spatial state: `count` speaker positions (interleaved xyz) with per-speaker
 * occlusion virtual distances and per-speaker render flags (v34: bit0 = ham class; NULL = all 0).
 * count 0 = this slot inaudible this block (residual mono discarded).
 * The listener basis + curve tuning are GLOBAL (shared by every slot). Lock-free. */
void cmm_out_set_emitter_occl_tone(cmm_out* o, int enable, float fullDb);
/* v51 - one occlusion/tone smoothing time constant, SECONDS. See cmm_mix_set_emitter_slew. */
void cmm_out_set_emitter_slew(cmm_out* o, float occlSec);
void cmm_out_set_emitter_spatial(cmm_out* o, int slot, const float* lisPos3, const float* lisRight3,
                                 const float* pts, const float* gains, const int* flags, int count,
                                 float minDist, float maxDist, float falloffExp, float muffle,
                                 float stereoWidth);

/* Relay the live emitter reverb params (from the managed room probe). Lock-free.
 * v31: wets/depths are PER-SLOT arrays (CMM_MAX_EMIT_VOICES long): each slot's CARRY send level
 * (seal-gated next-room cue; 0 = none) and its air-route depth. room/damp/pan/localWet describe the
 * LISTENER's room and stay shared. */
void cmm_out_set_emitter_reverb(cmm_out* o, const float* wets, const float* depths,
                                float room, float damp, float rangeMult, float pan,
                                float depthAmt, float localWet);

/* Relay the live radio-coloration params (v26; v34 adds the HAM class's own set). All 0..1;
 * all 0 = clean bypass. Lock-free. */
void cmm_out_set_emitter_color(cmm_out* o, float drive, float staticLvl, float voice,
                               float hamDrive, float hamStatic, float hamVoice);

/* Request a full emitter acoustic-state reset (v30): reverb tails, LPF/pan/mix slews, per-slot
 * smooth state — serviced by the audio callback before its next block. Called when a NEW station
 * voice starts fresh so nothing from the previous voice bleeds under it. Lock-free flag. */
void cmm_out_emitter_reset(cmm_out* o);

/* Request ONE slot's flush (v32): the callback discards the slot's queued dry mono and resets its
 * per-slot render state (LPF, coloration filters, send slew, point-smooth init) before its next
 * drain. Raised when a DIFFERENT station acquires the slot. Lock-free flag. */
void cmm_out_emitter_flush_slot(cmm_out* o, int slot);

/* ── Format converter (resample + channel map) ────────────────────────────
 * Streaming f32→f32 sample-rate + channel converter (miniaudio's data
 * converter). The mixer gives each voice whose native format differs from the
 * 48k-stereo bus one of these, feeds it decoded input frames, and pulls bus-
 * format output frames — so tracks at any sample rate mix and crossfade on a
 * single fixed bus instead of forcing a device reopen on every rate change. */
typedef struct cmm_dc cmm_dc;

/* Open a converter from (inRate,inChannels) to (outRate,outChannels). f32 both
 * ends. Returns NULL on failure with *err set (ma_result, or small negative). */
CMM_API cmm_dc* cmm_dc_open(int inRate, int inChannels, int outRate, int outChannels, int* err);

/* Convert: consume up to inFrames input frames, produce up to outCap output
 * frames. *inConsumed / *outWritten report what actually happened (the converter
 * buffers internally, so neither need equal the request). inFrames=0 drains the
 * tail at end-of-stream. Returns 0 on success, <0 on error. */
CMM_API int cmm_dc_process(cmm_dc* c, const float* in, long long inFrames, long long* inConsumed,
                           float* out, long long outCap, long long* outWritten);

/* Free the converter. NULL-safe. */
CMM_API void cmm_dc_close(cmm_dc* c);

/* ── Native mixer (producer loop) ─────────────────────────────────────────
 * Moves the whole decode→convert→gain-ramp→sum→write producer loop into native
 * code on its own thread, so the game's GC (stop-the-world for managed threads)
 * can't suspend it and underrun the output ring. The managed side keeps DECIDING
 * what plays (which file, when to switch, fades, resume); it opens each decoder
 * (off the audio loop) and hands the cmm_decoder* + a format `kind` to the mixer,
 * which then reads/mixes it entirely natively. Reuses cmm_out (output) + cmm_dc
 * (per-voice converters). A faithful port of the managed CmmMixer; a singleton.
 *
 * `kind` matches the managed AudioDecoder.ClassifyReader:
 *   0=Opus 1=FLAC 2=Vorbis 3=MP3 4=WAV 5=AAC 6=Container(Matroska/WebM).
 * All control calls are thread-safe against the producer thread (one internal mutex). */

/* Create the mixer + start the producer thread (idempotent). busRate/busChannels =
 * the fixed mix bus (48000/2). tightFrames/maxFrames = the tight and deep output-ring
 * cushion depths in FRAMES (the managed side computes them from config). The device
 * opens lazily on the first cmm_mix_add. Returns 0 on success. */
CMM_API int cmm_mix_start(int busRate, int busChannels, int tightFrames, int maxFrames, int* err);

/* v15 — as cmm_mix_start, plus headless!=0 opens a NULL output (no ma_device) drained by
 * cmm_mix_pump_out. For the offline stress harness (tools/CmmSession): the real producer/
 * voice/retire loop runs at compressed speed with no audio hardware. Returns 0 on success. */
CMM_API int cmm_mix_start_ex(int busRate, int busChannels, int tightFrames, int maxFrames,
                             int headless, int* err);

/* v15 — headless drain: consume up to `frames` from the output ring so the producer keeps
 * rendering (voices advance/fade/retire). No-op (returns 0) unless started headless. Call on
 * the harness clock as the stand-in for the realtime device callback. */
CMM_API int cmm_mix_pump_out(int frames);

/* Install a track and crossfade to it. `dec`+`kind` name a decoder the caller opened;
 * srcRate/srcChannels/totalFrames describe it (totalFrames<=0 = unknown). fade = crossfade
 * seconds; loop restarts at EOF; startFrame seeks before play (resume, 0 = from start);
 * trackGain = per-track loudness gain; duckOthersTo>=0 marks a PlayOnce stinger overlay
 * (existing voices DUCK to that level instead of fading out, and swell back when it ends);
 * snapTight!=0 flushes a deep cushion (a context change). Takes ownership of `dec` (frees
 * it on retire). Returns a voice id (0 on failure) to poll via cmm_mix_get_state/remaining. */
CMM_API long long cmm_mix_add(cmm_decoder* dec, int kind, int srcRate, int srcChannels,
                              long long totalFrames, float fade, int loop, long long startFrame,
                              float trackGain, float duckOthersTo, int snapTight);

/* ── Emitter voices (independent positional layer) ────────────────────────
 * An emitter voice plays ALONGSIDE the music voices instead of crossfading them: cmm_mix_add
 * fades every existing voice out (a music context change), but an emitter voice is EXEMPT from
 * that fade and from the voice-count eviction, so a diegetic world speaker keeps playing while
 * the soundtrack changes underneath — and starting/stopping an emitter never disturbs the music.
 * Same decoder handoff as cmm_mix_add: the caller opens `dec` (+ `kind`) off the audio thread and
 * the mixer TAKES OWNERSHIP (frees it on retire or on failure). fadeIn = ramp-up seconds; loop
 * restarts at EOF; trackGain = per-track loudness gain. Returns an emitter voice id (0 on failure)
 * to remove via cmm_mix_remove_emitter. v31: `slot` (0..CMM_MAX_EMIT_VOICES-1) pins the voice to a
 * station render slot — voices sharing a slot sum into its ring (a track-advance crossfade); the
 * managed layer keeps one station per slot. Out-of-range slot fails the add. */
CMM_API long long cmm_mix_add_emitter(cmm_decoder* dec, int kind, int srcRate, int srcChannels,
                                      long long totalFrames, float fadeIn, int loop, float trackGain,
                                      float posX, float posY, float posZ, float minDist, float maxDist,
                                      long long startFrame, int slot);

/* Fade an emitter voice out over `fade` (<=0 snaps) and retire it. Unknown id is a no-op. */
CMM_API void cmm_mix_remove_emitter(long long id, float fade);

/* v30 — reset the emitter layer's acoustic render state (reverb tails, LPF/pan/mix slews, per-slot
 * smooth state). Call BEFORE adding a station voice that starts FRESH (not a track advance) so the
 * previous voice's leftover reverb energy / stale slews never bleed under the new one. */
CMM_API void cmm_mix_emitter_reset_acoustics(void);

/* v32 — flush ONE render slot: discard its queued dry ring + reset its per-slot render state. Call
 * BEFORE adding a voice when the slot's previous occupant was a DIFFERENT station, so the old
 * station's buffered audio never plays through the new station's speakers. NOT called on a track
 * advance (same station keeps its ring/tail continuity). */
CMM_API void cmm_mix_emitter_flush_slot(int slot);

/* Set the LISTENER basis for emitter spatialization: world position + forward + right unit
 * vectors (pass them straight from the camera transform — the mixer uses them as-is, so there is
 * no left/right-handed coordinate ambiguity). Lock-free (like cmm_mix_set_master); the producer
 * reads them per render block. Call every frame while emitters may be audible. */
CMM_API void cmm_mix_set_listener(float px, float py, float pz,
                                  float fx, float fy, float fz,
                                  float rx, float ry, float rz);

/* Move a live emitter voice to a new world position (e.g. the nearest speaker changed). Unknown
 * or non-emitter id is a no-op. */
CMM_API void cmm_mix_set_emitter_pos(long long id, float x, float y, float z);

/* v18/v19 — set the FULL set of speaker positions playing this station: `xyz` holds `count`
 * interleaved x,y,z triples (clamped to 8), `gains` holds `count` per-speaker gains (occlusion:
 * 1=clear, <1=muffled; NULL = all clear). The render sums each point's pan+attenuation×gain so every
 * speaker is heard at once from the one decode. count<=0/xyz NULL silences until the next update.
 * v34: `flags` holds `count` per-speaker render flags — bit0 = HAM class (that point's signal gets
 * the ham coloration set); bits 1-2 reserved for the stereo-pair channel split. NULL = all speaker. */
/* v42: drive emitter TONE from occlusion instead of virtual distance (see cmm_out.c). */
CMM_API void cmm_mix_set_emitter_occl_tone(int enable, float fullDb);

CMM_API void cmm_mix_set_emitter_points(long long id, const float* xyz, const float* gains,
                                        const int* flags, int count);

/* v21 — set the LIVE spatial-curve tuning applied by the output spatializer, so it can be dialled
 * in-game from config without a rebuild. minDist = full-volume plateau radius; maxDist = audible
 * range (silence beyond); falloffExp = steepness of the plateau→range fade (1 = linear amplitude,
 * >1 steeper near the plateau / more "responsive", <1 gentler); muffle = distance low-pass strength
 * 0..1 (0 = no distance dulling, higher = duller with distance). Lock-free; call whenever config
 * changes (cheap to call every tick). Applies to the single shared station. */
CMM_API void cmm_mix_set_emitter_tuning(float minDist, float maxDist, float falloffExp, float muffle,
                                        float stereoWidth);

/* v51 - the ONE occlusion smoothing time constant, in milliseconds (default 50). Replaces an
 * adaptive per-callback coefficient whose effective time constant was 328 ms at a 10 ms audio
 * buffer, varied with the audio endpoint, varied with how fast the door happened to be moving and
 * varied with the listener's distance from the speaker. The door model leads the pipeline by a
 * fixed time to cancel this lag, and a fixed lead can only cancel a fixed lag. Also governs the
 * muffle's slew, so tone and level arrive together. Lock-free. */
CMM_API void cmm_mix_set_emitter_slew(float occlMs);

/* v23 — set the LIVE emitter reverb params from the managed room probe. wet = master wet level
 * (0 = off), room = size→decay/tail, damp = HF damping (dead room = darker/shorter). All 0..1.
 * rangeMult (≥1) = wet audible range as a multiple of the dry range (reverb reaches farther than the dry).
 * pan (0..1) = how much the diffuse reverb is panned toward the source's traced bearing (0 = diffuse).
 * v27: depth = the listener's ACOUSTIC DEPTH from the station (air-route blocks — real distance plus the
 * open-air detour around bends; walls excluded, the managed seal gate covers those); depthAmt = 0..1 how
 * strongly depth grades the wet (0 = flat wet at any depth, the pre-v27 behaviour). With grading on, the
 * wet's level, brightness (output low-pass) and damping all sink smoothly with depth.
 * v28: two buses. CARRY = the deliberate next-room / down-the-tunnel cue: un-occluded feed; managed
 * applies the solid-seal gate + carry level; native applies range + depth grading + route pan.
 * `localWet` = the LOCAL bus: a second reverb fed the ARRIVING dry (post-attenuation/occlusion),
 * so your own room's reverb inherits every dry rule — walls, water, hard-stop — with no seal knob;
 * pass the ear-room wetness × master only.
 * v31: `wets`/`depths` are PER-SLOT arrays (CMM_MAX_EMIT_VOICES floats): each voiced station's own
 * carry-send level + air-route depth (its seal/solid gating differs per station); the room-shape
 * params (room/damp/pan/localWet) describe the LISTENER's room and stay shared.
 * Lock-free; call every probe tick. */
CMM_API void cmm_mix_set_emitter_reverb(const float* wets, const float* depths,
                                        float room, float damp, float rangeMult, float pan,
                                        float depthAmt, float localWet);

/* v26 — set the LIVE radio-coloration ("through a speaker") params. drive = soft-clip amount (0 = clean,
 * 1 = crunchy; level-compensated so it adds grit, not volume); staticLvl = static-hiss bed level
 * (0 = none); voice = band-pass voicing blend (0 = full-range, 1 = fully "small speaker": no lows below
 * ~250 Hz, no highs above ~4 kHz). Applied to the emitter mono BEFORE the distance low-pass and the
 * reverb feed, so distance muffle, occlusion, pan and reflections all act on the colored signal — the
 * static hisses from the radio's position like everything else. All 0 = exact bypass. Lock-free;
 * call every tick.
 * v34: the ham* trio is the HAM class's own set (same semantics) — per-point flags decide which set
 * a speaker's signal gets, so a radioHam can stay slightly distorted while shelf speakers are clean. */
CMM_API void cmm_mix_set_emitter_color(float drive, float staticLvl, float voice,
                                       float hamDrive, float hamStatic, float hamVoice);

/* v24 — set the EMITTER (world-radio) master volume, separate from the music master. Feed the game's
 * music slider × the Radio context's own volume. Clamped to >= 0. Lock-free; call every tick. */
CMM_API void cmm_mix_set_emitter_master(float vol);

/* v35 — fire one synthesized radio-UI sound: kind 1 = tuning static (v36: KEEPALIVE — ping at any
 * rate ≥ ~2 Hz to sustain one seamless band-passed hash; it releases ~0.4s after the pings stop,
 * or immediately via cmm_mix_stop_sfx), 2 = power-on click, 3 = power-off clunk. `gain` 0..1,
 * applied pre-master (the SFX ride the music master exactly like the radio voice they decorate).
 * Ensures the output device like cmm_mix_add; never touches the output gain (a click during the
 * SP pause duck stays ducked). Fixed 4-slot pool, oldest stolen. */
CMM_API void cmm_mix_play_sfx(int kind, float gain);

/* v36 — release every live SFX of `kind` now (its release ramp runs, ~90ms for the static).
 * Call when the sound's REASON ends — e.g. the dialed station's voice just landed. */
CMM_API void cmm_mix_stop_sfx(int kind);

/* v37 — set the tuning static's TONE: lowHz = low cut (thin ↔ full-bodied; clamped 50..4000),
 * highHz = top cap (muffled ↔ bright hiss; clamped 400..12000, always above the low cut),
 * crackle = 0..1 sputter depth (0 = smooth hiss, 1 = full radio crackle). Captured at every
 * ping — including keepalive extensions — so config edits are heard live mid-hash. */
CMM_API void cmm_mix_set_static_tone(float lowHz, float highHz, float crackle);

/* v54 - A RECEIVER PER SPEAKER. The world speakers had none: retuning one across the room was
 * silent until the music appeared, and the only sound that covered it (the SFX tuning static)
 * plays on the music bus, which has no position -- so it arrived inside the listener's head
 * instead of out of the radio.
 *
 * Each emitter SLOT now has its own receiver, running on that slot's dry mono lane BEFORE the
 * emitter master, so the hiss inherits distance, occlusion, doors, room reverb and the speaker's
 * coloration exactly as the programme does. State is per slot (three speakers searching are three
 * sets, not one in triplicate); every TUNING PARAMETER is shared with the personal radio's
 * receiver, so the radio.xml knobs move both.
 *
 *   signal 0 = searching (full noise), 1 = locked. Ramped over rampSeconds.
 *   clear_emitter_recv takes the receiver out of the path entirely. */
CMM_API void cmm_mix_set_emitter_recv(int slot, float signal, float rampSeconds);

/* v54 - the slot's speakers. Replaces cmm_mix_set_emitter_points(voiceId, ...): a station's
 * speakers belong to the SLOT, not to whichever track happens to be playing on it, and a slot
 * with no voice still needs somewhere in the world to put its receiver's noise. */
CMM_API void cmm_mix_set_slot_points(int slot, const float* xyz, const float* gains,
                                     const int* flags, int count);
CMM_API void cmm_mix_clear_emitter_recv(int slot);

/* v55 - how loud the emitter receiver's noise is, relative to the personal radio's. Its own knob
 * because g_recvNoiseTrim is shared: turning that down to quieten a speaker across the room would
 * quieten the radio in your pocket too. Default 0.5. */
CMM_API void cmm_mix_set_emitter_recv_trim(float trim);

/* v38/v39 — set the power-click TONES, both kinds at once. Per kind: `hz` = tone cap (bright
 * tick ↔ dull thud; clamped 300..10000), `snap` = 0..1 bright-noise-ping vs low-thump balance
 * (1 = all snap, 0 = all thud), `lenMs` = the thump's decay in ms (clamped 5..120; the ping
 * decays 4× faster, the whole click runs ~3.3× this), `body` = 0..1 mix of the shell-knock
 * resonator (v39), `bodyHz` = its ring pitch (clamped 200..3000; the "tok" of the dial body),
 * `dbl` = 0..1 level of the second detent micro-impact ~12ms after the first (0 = single).
 * Captured when a click fires, so config edits are heard on the next dial. */
/* v41 — one kind at a time: 0 = power-on toggle, 1 = power-off toggle, 2 = dial detent. Replaces
 * the both-kinds-at-once form, which would have needed eighteen positional floats to gain a third
 * kind. `vary` (0..1, shared by all kinds) is how much each firing differs from the last. */
CMM_API void cmm_mix_set_click_tone_kind(int which, float hz, float snap, float lenMs,
                                         float body, float bodyHz, float dbl, float vary);

/* v44 — give a click kind a RECORDED sound (0 = power on, 1 = power off, 2 = dial); mono float
 * PCM, copied. NULL/0 frames reverts that kind to synthesis. The rate jitter is how far playback
 * speed wanders per firing, in Hz either side of the file's own rate — repeating one recording
 * verbatim is what gives a UI sound away. */
CMM_API int  cmm_mix_set_sfx_sample(int which, const float* pcm, int frames, int rate);
CMM_API void cmm_mix_set_sfx_rate_jitter(float hz);

/* v41 — render ONE click in isolation for auditioning; see cmm_mix.c. Returns frames (mono). */
CMM_API int cmm_mix_render_click(int kind, int rate, float* out, int maxFrames);

/* v45 — mix a mono sound into ALREADY-QUEUED output, `skip` frames past the play head, so a UI
 * sound is not delayed by the output cushion. Returns 0 if it will not fit contiguously (the ring
 * wrapped); the caller should then arm it normally rather than play a truncated one. */
CMM_API int cmm_out_inject(cmm_out* o, const float* mono, int frames, float gain, int skip);

/* Fade ALL voices out over `fade`. closeWhenIdle!=0 closes the device once silent;
 * pass 0 to keep it open for a hand-off (e.g. a menu→game fade into the spawn stinger). */
CMM_API void cmm_mix_stop(float fade, int closeWhenIdle);

/* Retire + discard every voice immediately (no fade) and flush the queued output. */
CMM_API void cmm_mix_stop_all_now(void);

/* Master volume (music slider × modifier), applied to the summed bus. */
CMM_API void cmm_mix_set_master(float vol);

/* Update a live voice's per-track gain in place (loudness settle / trim keybind), ramped
 * over rampSeconds (<=0 snaps). Returns 0 if the voice is gone. */
CMM_API int cmm_mix_set_voice_gain(long long id, float trackGain, float rampSeconds);

/* Reverse-fade reclaim: ramp voice `id` back to full and fade the others out. Returns 0
 * when the voice is gone/inaudible (caller falls back to a fresh cmm_mix_add). */
CMM_API int cmm_mix_reclaim(long long id, float fade);

/* Playhead + length (seconds) of a voice; *active=0 (return 0) when the id is gone. */
CMM_API int cmm_mix_get_state(long long id, double* posSec, double* lenSec, int* active);

/* Seconds left in a voice: -1 gone, +Inf unknown length, else remaining play time. */
CMM_API double cmm_mix_remaining(long long id);

/* Consumer-side output-gain ramp over `seconds` (<=0 snaps) — menu↔game transition fades,
 * heard in real output time regardless of cushion depth (forwards to cmm_out_set_gain). */
CMM_API void cmm_mix_set_output_gain(float target, float seconds);

/* v33: while hold != 0, cmm_mix_add's output-gain REVIVE is suppressed. The revive exists
 * for menu-transition fades that park the device silent (new music must wake it); the SP
 * pause duck uses the SAME gain, so without the hold any voice added mid-pause (music EOF
 * auto-advance, a radio advance, an emitter station start) snapped the whole device — music
 * AND emitters — back to full through the pause screen. Managed sets it on the pause-duck
 * edge and clears it on unpause; cmm_mix_start / cmm_mix_stop_all_now clear it defensively
 * so a missed edge can never leave the mixer permanently un-revivable. */
CMM_API void cmm_mix_hold_output(int hold);

/* Proactive deep-cushion flags: deep = world loading (its falling edge snap-flushes tight),
 * extra = post-spawn/teleport grace (drains naturally, no flush → no track teleport). */
CMM_API void cmm_mix_set_cushion(int deep, int extra);

/* Ask the producer thread to reopen the output device (OS default endpoint changed). */
CMM_API void cmm_mix_request_reinit(void);

/* Live output-ring health for the debug HUD's buffer tracker. queued/target/minQueued in
 * FRAMES; minQueued is the lowest depth since the last call (reset on read) — near 0 means a
 * near/actual underrun. voices = live mix voices; deep = DeepBuffer; underruns = cumulative
 * ring-empty events during playback. Any out-pointer may be NULL. Do NOT call before
 * cmm_mix_start (it locks the mixer mutex). */
CMM_API void cmm_mix_get_output_stats(int* queued, int* target, int* minQueued,
                                      int* voices, int* deep, int* underruns, int* busRate);

/* ── Loudness analyzer (EBU R128 / ITU-R BS.1770 integrated loudness) ──────
 * A native port of the managed CMM.R128Analyzer, so the loudness sweep decodes AND
 * measures entirely in native memory — no managed-heap churn to trip Mono's stop-the-
 * world GC and hitch the game's main thread. Two surfaces:
 *   - streaming (cmm_loudness_new/_feed/_finish/_free): feed interleaved-f32 PCM in any
 *     chunking, then finish once. Mirrors R128Analyzer; used by the self-test.
 *   - one-shot (cmm_loudness_analyze): the caller opens a decoder (as for cmm_mix_add) and
 *     hands the cmm_decoder* + a format `kind` (same 0..6 mapping as cmm_mix); this reads it
 *     to EOF, measures, and TAKES OWNERSHIP (closes the decoder). This is the identical
 *     C#→native decoder handoff the mixer uses. */
typedef struct cmm_loudness cmm_loudness;

/* Create a streaming analyzer for the given source rate/channels (coefficients are
 * sample-rate-derived). Free with cmm_loudness_free. NULL on allocation failure. */
CMM_API cmm_loudness* cmm_loudness_new(int sampleRate, int channels);

/* Feed `count` interleaved floats (frames × channels). Partial trailing frames ignored. */
CMM_API void cmm_loudness_feed(cmm_loudness* a, const float* data, int count);

/* Finalise: *outLufs = integrated LUFS (only meaningful when the return is 1), *outPeak =
 * highest absolute sample seen (always set). Returns 1 when a gated result exists, else 0
 * (track too short/quiet — caller treats as "no normalization"). */
CMM_API int cmm_loudness_finish(cmm_loudness* a, double* outLufs, double* outPeak);

CMM_API void cmm_loudness_free(cmm_loudness* a);

/* One-shot: read `dec` (opened by the caller; `kind` 0..6 as in cmm_mix) to EOF and measure.
 * TAKES OWNERSHIP of `dec` (closes it before returning). Same out-params/return as finish. */
CMM_API int cmm_loudness_analyze(cmm_decoder* dec, int kind, int srcRate, int srcChannels,
                                 double* outLufs, double* outPeak);

/* ── Progressive (chunked) measurement ────────────────────────────────────
 * Same measurement as the one-shot, answered incrementally. A long file cannot be
 * normalized until it has been decoded end to end, so a 47-minute podcast plays at
 * neutral gain for as long as that takes; this decodes a bounded slice at a time and
 * reports the integrated loudness of everything read SO FAR, letting the caller
 * publish a usable gain seconds in and refine it as the decode continues.
 *
 * Gating is over ALL blocks fed so far (not per-slice), so successive answers are
 * refinements of ONE measurement, and the value returned once eof is set is identical
 * to what cmm_loudness_analyze would have produced for the same file.
 *
 * Ownership matches the one-shot: begin() takes the decoder, free() closes it —
 * whether or not the stream was read to the end. */
typedef struct cmm_lprog cmm_lprog;

/* Start a progressive measurement over `dec` (opened by the caller; `kind` 0..6 as in
 * cmm_mix). TAKES OWNERSHIP of `dec` — including on failure, where it is closed before
 * NULL is returned. */
CMM_API cmm_lprog* cmm_lprog_begin(cmm_decoder* dec, int kind, int srcRate, int srcChannels);

/* Decode ~`chunkSeconds` more of audio (rounded up to a whole decoder read) and report
 * the measurement so far. Returns 1 when a gated result exists (*outLufs meaningful),
 * else 0 — a slice of silence, or under 400 ms fed, yields no gated block. *outEof is
 * set when the stream is exhausted; the caller stops there. *outSecondsFed is the total
 * decoded so far, for progress reporting. Any out-pointer may be NULL.
 *
 * chunkSeconds <= 0 (or absurdly large) means UNLIMITED: read to eof in this one step, which
 * is the plain whole-file measurement. Ask for it that way rather than by passing a huge
 * number of seconds -- seconds x sample rate overflows a long long long before a double runs
 * out of range, and the overflowed target would clamp to a single frame per step. */
CMM_API int cmm_lprog_step(cmm_lprog* p, double chunkSeconds,
                           double* outLufs, double* outPeak,
                           double* outSecondsFed, int* outEof);

/* Free the analyzer and close the owned decoder. Safe on NULL, and at any point —
 * abandoning a half-measured file is the normal path when its track stops mattering. */
CMM_API void cmm_lprog_free(cmm_lprog* p);

#ifdef __cplusplus
}
#endif
#endif /* CMM_DECODER_H */
