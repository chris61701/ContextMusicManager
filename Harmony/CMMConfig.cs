
using System.Collections.Generic;
using MusicUtils.Enums;
using UnityEngine;

namespace CMM
{
    public static class CMMConfig
    {
        // ── Disabled vanilla DMS sections (Config/settings.xml) ────────────────
        // Vanilla Dynamic Music sections the user has switched OFF: the game is never
        // allowed to cut to them while DMS is enabled and CMM is letting vanilla play
        // (the "Vanilla" fallback). A disabled section obeys its linked context's rules —
        // the VanillaIf* fallbacks divert to the rest of the CMM stack first (so a POI
        // theme or your own ambient keeps playing), and only when nothing else is available
        // does governance play the Exploration ambient in its place (or silence, if
        // Exploration is disabled too). Populated from the <DisabledVanillaSections> block in
        // settings.xml — one true/false child per section (see CMMConfigLoader); consulted in
        // ResolvePlayableContextCore
        // (ProposedVanillaSectionDisabled / PanicHandoffSectionDisabled) and GovernVanillaSection.
        // Empty by default (every vanilla section allowed). Main-thread only — read
        // in the section-governance postfix, rewritten on each settings (re)load.
        // Disabling a section does NOT suppress CMM's OWN music for the linked
        // context; it only mutes the game's version.
        public static readonly HashSet<SectionType> DisabledVanillaSections =
            new HashSet<SectionType>();

        public static float VolumeModifier = 0.5f;

        // ── The game's "Overall Volume" slider ────────────────────────────────
        // The game applies this pref as `AudioListener.volume` (GameOptionsManager), a Unity-side
        // global that attenuates Unity AudioSources — and NOTHING CMM emits is one. Music, the
        // world radios and the synthesized radio SFX all leave through our own miniaudio device
        // on our own mix thread, so the master slider physically cannot reach them: without this
        // read, Overall=0 silences the whole game EXCEPT CMM (confirmed in-game — vanilla music
        // attenuates, ours doesn't). So we apply it ourselves, as the outermost gain stage.
        //
        // Vanilla composes its sliders as independent gain stages in series (per-group mixer
        // param, then AudioListener.volume at the listener), so the stages MULTIPLY: Overall 0.7
        // with Music 0.2 lands at 0.14. The group params go through LogarithmicCurve(2, 6, -80, 0)
        // into a dB mixer, but that curve is near-identity in amplitude (slider s → s^0.9966,
        // ≤0.9% error at the bottom of the range) — so multiplying the raw pref values reproduces
        // vanilla to well under a tenth of a dB. No curve port needed.
        //
        // Clamped to [0,1], mirroring the game's own startup clamp (GameOptionsManager
        // .ApplyAllOptions does Math.Min(pref, 1f)). The pref carries no min/max validator in
        // GamePrefs — the 0-100% range is enforced only by the options UI — so a hand-edited
        // prefs file could otherwise hand us a value >1 and drive the native bus into clipping.
        // Direct enum use is safe: OptionsOverallAudioVolumeLevel sits at ordinal 5 in A20, A21,
        // 2.6 AND 3.1 (verified against all four decompiles).
        public static float OverallVolume
            => Mathf.Clamp01(GamePrefs.GetFloat(EnumGamePrefs.OptionsOverallAudioVolumeLevel));

        // ── The game's "Ambient Sound Volume" slider ──────────────────────────
        // The slider the game labels "sets environmental sound volume" (mixer param ambVol).
        // The world emitters ride THIS, not the music slider: a radio playing out of a block is a
        // diegetic object in the world, the same category as every other environmental sound, so
        // turning the MUSIC down must not silence it. Same near-identity curve as Overall above,
        // so the raw pref value is the right multiplier. Ordinal 2 in A20/A21/2.6/3.1 (verified),
        // so the direct enum reference is safe. Music playback does NOT use this.
        public static float AmbientVolume
            => Mathf.Clamp01(GamePrefs.GetFloat(EnumGamePrefs.OptionsAmbientVolumeLevel));

        // A non-negative volumeOverride replaces the global VolumeModifier for this
        // call (used by contexts that set their own volume, e.g. POI_Cleared).
        public static float GetVolume(bool inGame, float volumeOverride = -1f)
        {
            // Direct enum use is safe HERE ONLY because these two members sit at ordinals 3/4 in
            // every supported version. EnumGamePrefs ordinals drift deeper into the enum — any
            // other member must go through GamePrefsCompat (compiled constants read the wrong
            // pref cross-version otherwise).
            float inGameVolume = GamePrefs.GetFloat(
                    inGame ? EnumGamePrefs.OptionsMusicVolumeLevel : EnumGamePrefs.OptionsMenuMusicVolumeLevel);

            float modifier = volumeOverride >= 0f ? volumeOverride : VolumeModifier;
            // Overall multiplies LAST — every caller of this method is setting the native bus
            // master (CmmMixer.MasterVolume), which is the one place all our audio passes through.
            return OverallVolume * inGameVolume * modifier;
        }

        // ── Native audio output (miniaudio) ───────────────────────────────────
        // Music plays through CMM's OWN miniaudio output device + mixer thread
        // (CmmMixer) — decode, mix and output all run on threads CMM owns, off
        // Unity's main and DSP threads. This is the sole output path; the legacy
        // AudioSource/AudioClip path has been removed.
        // Output ring TIGHT target (ms): the cushion the mixer keeps queued in normal
        // play — also the output latency for volume/crossfade, so kept small (~200ms).
        public static int NativeOutputRingMs = 200;
        // Output ring MAX depth (ms): the ceiling the cushion can ADAPTIVELY grow to when
        // the producer is being starved (game world-gen / other programs hogging the CPU)
        // or proactively during loading. The ring is allocated at this size; the live fill
        // level floats between the tight target and this max, and snaps back tight on a
        // context change (see CmmMixer). 15s ≈ 5.8 MB of native ring at 48k stereo f32.
        public static int NativeOutputMaxRingMs = 15000;

        // ── Off-frame-loop control thread (experimental) ──────────────────────
        // When true, periodic housekeeping that can run off Unity's frame loop is paced
        // and executed on CMM's own CmmScheduler threads (two lanes: housekeeping +
        // critical, see CmmScheduler) instead of as WaitForSeconds coroutines,
        // marshalling back to the main thread via MainThreadDispatcher only for the
        // parts that must observe game state or touch the lock-free structures.
        // Lets that work keep ticking through a main-thread freeze (world load/unload).
        // When false, the original coroutine paths run instead (the fallback).
        public static bool NativeScheduler = true;
        // Pre-stage the next track's fully-formed start request when the CURRENT one begins,
        // so the scheduler thread can dispatch the auto-advance straight to the start worker
        // at track-end WITHOUT a main-thread hop — the next track begins even while Unity's
        // main thread is frozen (world load/unload). The main-thread cache bookkeeping is
        // reconciled afterwards. Requires NativeScheduler. When false, auto-advance falls back
        // to marshalling StartNativeContext to the main thread (still off-frame-loop polled,
        // but the advance waits for a frame). See ContextMusicManager.PreStageNextAdvance.
        public static bool NativePreStageAdvance = true;

        // ── World emitters (in-world speakers/radios — Config/radio.xml) ────
        // EXPERIMENTAL (3.0 only). When enabled, detected in-world speaker/radio blocks
        // (see EmitterRegistry) play a shared "station" as an independent positional layer
        // ALONGSIDE the normal soundtrack — a diegetic radio you hear when near it. v1 is
        // non-spatial (centered); spatialization + radio coloration come in later phases.
        //   Source: a folder (relative to the Music dir, or absolute) OR an .m3u playlist,
        //   exactly like a context source — the pool the station loops through.
        //   Range: the emitter starts/stops as the NEAREST detected speaker crosses this
        //   distance (game units) from the listener.
        // Read music from OTHER mods' Music folders as well as MusicPath. On by default:
        // a music pack shipped as its own mod is the whole point, and a pack that silently
        // does nothing until you find a setting is worse than no feature. Set false to
        // restrict CMM to the single folder MusicPath names.
        public static bool   AddonMusicScanEnabled = true;

        public static bool   WorldEmitterEnabled = true;
        public static string WorldEmitterSource  = "Radio";
        public static float  WorldEmitterRange   = 60f;
        //   Volume: the world emitters' OWN trim (0..1), multiplied onto the Ambient and Overall
        //   sliders to form the emitter master. DELIBERATELY SEPARATE from the Radio context's
        //   `volume` in contexts.xml, which now trims ONLY the personal radio (Alt+PgUp/PgDn) —
        //   the two share a station catalog but are different things: one is a speaker across the
        //   room, the other is in your head, and a level that suits one rarely suits the other.
        //   0.5 keeps the level the Radio context's trim used to give the emitters.
        // 1.0, up from 0.5. The old value was settled by ear while occlusion was LEAKING --
        // a route could pass diagonally between two blocks meeting at a corner and pay nothing,
        // so walls were under-charged anywhere there was geometry. Closing that made the same
        // speakers correctly quieter, which means 0.5 is no longer the level it was chosen to be.
        public static float  WorldEmitterVolume  = 0.5f;
        // Fade (seconds) when the station starts (a speaker comes in range) / stops (all leave range).
        public static float  WorldEmitterFade    = 1.0f;
        // Placement ("birth") ramp for a speaker that APPEARS while its station is already
        // playing (just placed, or streamed in with a chunk). < 0 ⇒ inherit the start fade
        // (the Radio context's fadeintime, else WorldEmitterFade). 0 = pop in instantly.
        public static float  WorldEmitterBirthFade = 0f;
        // Broadcast anchor window (seconds). Station schedules re-anchor at every multiple of this:
        // a join walks track lengths forward from the current window's anchor (~window/4min header
        // opens, pool-size independent). Longer = rarer re-anchor jumps, slightly costlier joins.
        // MP clients must share the value (it shapes the schedule, like the station folders do).
        public static float  RadioAnchorSeconds  = 3600f;
        // Personal-radio UI sounds (synthesized natively — no audio assets): a tuning static hash
        // while dialing / joining a station, a click on power-on, a clunk on power-off (also
        // played when the radio powers itself off because every station was deleted). Volume is
        // pre-master 0..1 (the sounds ride the music master exactly like the radio voice).
        public static bool   RadioSfxEnabled = true;
        public static float  RadioSfxVolume  = 0.5f;
        // Tuning-static character (all hot-reload — heard on the very next ping, even mid-hash,
        // so you can dial between stations while editing to taste):
        //   StaticVolume  = the static's OWN level; -1 = inherit RadioSfxVolume (clicks keep that).
        //   StaticLowHz   = low cut — lower = fuller/warmer body, higher = thinner.
        //   StaticHighHz  = top cap — higher = brighter hiss, lower = duller rumble.
        //   StaticCrackle = 0..1 sputter depth — 0 = smooth "shhh", 1 = full radio crackle.
        //
        // THE SOUND BETWEEN STATIONS IS A ROAR, NOT A HISS, AND THE LOW CUT WAS KILLING IT.
        // FM makes two different noises and they are not interchangeable. With a carrier present
        // (a weak station) the demodulator's noise rises with frequency, and de-emphasis flattens
        // it into the bright thin HISS everyone pictures. With NO carrier — which is what tuning
        // between stations is — the noise comes out FLAT, and de-emphasis then tilts it down above
        // its corner. That is a dark, full-bodied ROAR, and it is the louder of the two.
        //
        // The filter here is already the right shape for it: a one-pole highpass at LowHz into a
        // one-pole lowpass at HighHz, and -6 dB/oct above the corner is exactly what de-emphasis
        // does. HighHz 2000 was already within a whisker of the real 50 us corner (3183 Hz US-75us
        // is 2122). The error was LowHz 350, which threw away everything below it — so what came
        // out was a midrange "shhh" with no body: neither the roar nor the hiss, which is why it
        // read as synthetic. Drop the low cut and the same code produces the real thing.
        // ── Tuning between stations (spec: docs/plan-radio-tuning.md) ─────────────────────────
        // Changing station runs LEAVE -> SEARCH -> ARRIVE. One signal value, 0..1, drives the
        // station's gain, how much treble it keeps and how wide it is, and — inverted — the
        // static. Welded to one number on purpose: a station that merely gets quieter sounds like
        // a volume fade, and four curves tuned separately end up disagreeing.
        //
        // MinStaticMs is a FLOOR, not a duration. A slow file extends it; a file that was ready
        // instantly still pays it, because tuning has to sound like tuning even when the next
        // station happens to be loaded. Leave/Arrive are quoted at full scale and applied as a
        // RATE, so interrupting a half-finished tune-in fades from half and takes half as long —
        // a fixed duration drags on an early interrupt and snaps on a late one.
        //
        // 14 seconds end to end was the original, and it came from applying "double is fine" to a
        // listening demo and then shipping it as a gameplay timing without anyone checking it as
        // one. 5.5 s total is what settled: 1.2 out, 1.5 of static, 2.8 back in.
        public static float  RadioTuneMinStaticMs = 100f;
        // 700, not 1200. The knee puts the whole audible break-up in the LAST part of the
        // fade, so at 1200 ms the first 0.67 s of a dial press did nothing you could hear — the
        // key felt dead. Nobody caught it tuning by ear, because in a listening demo you never
        // press the key. Shortened here and the knee moved earlier; together, 0.25 s.
        public static float  RadioTuneLeaveMs     = 700f;
        public static float  RadioTuneArriveMs    = 1000f;
        public static float  RadioTuneDullness    = 0.85f;   // 0 = no treble loss on the way in/out
        public static float  RadioTuneMononess    = 1f;      // 0 = stays stereo the whole way
        // Backstop only, so a wedged open/seek cannot strand the player in static for ever. A
        // merely SLOW station is waited for; abandoning one that is still working is worse.
        // GAIN RISES FAST, TONE OPENS SLOWLY. gain = signal^this. A real receiver holds the
        // programme at full level across almost the whole quieting range and moves the NOISE
        // instead; a station tied linearly to signal just fades up, which is the single most
        // recognisable fake and is what the first version of this sounded like. At 0.30, a tenth
        // of the signal is already half the level, so the station is THERE and merely dull and
        // buried, and it is the muffling and the hiss that resolve. 1.0 = the old plain fade.
        public static float  RadioTuneGainPow     = 0.30f;
        public static float  RadioTuneGiveUpMs    = 10000f;

        // ── THE RECEIVER'S CHARACTER ───────────────────────────────────────────────────────
        // The hiss is generated INSIDE the station's own signal path now, summed with the music
        // before the shared low-pass and the shared stereo width — so they are one signal rather
        // than two sounds crossfading past each other. These shape what that one signal does.
        //
        // Knee/KneeWidth are the important pair. A linear ramp through a linear law is a fade,
        // and no endpoint tuning changes that. Real FM does its entire audible break-up inside
        // about EIGHT DECIBELS of carrier-to-noise, with flat plateaus either side, so the ramp
        // is bent into plateau → event → plateau. KneeWidth is how much of the ramp the event
        // gets: NARROW is correct, and widening it is the thing that will make it smooth again.
        // 0.90 = the station starts falling apart the INSTANT you press the key (0.075 s).
        //
        // This number was 0.45 for a long time and it was wrong for the wrong reason. The knee
        // exists so the break-up is an EVENT with flat plateaus either side rather than a smooth
        // fade — correct. But it models a station fading out ON ITS OWN, the way one does when
        // you drive out of range: quiet for a while, then break-up, then gone. That leading
        // stretch of "nothing has happened yet" is right for THAT story.
        //
        // Nobody drives out of range here. They press a button. There is nothing to wait for, and
        // the wait read as an unresponsive key — two thirds of a second of it at 0.45. Put the
        // knee at the top and the leading plateau disappears; the event and the trailing plateau,
        // which are the parts that make it sound like a receiver, are untouched.
        public static float  RadioRecvKnee        = 0.90f;   // where the break-up happens, 0..1 — HIGHER = sooner after the key
        public static float  RadioRecvKneeWidth   = 0.22f;   // how much of the ramp it occupies
        // How loud the hiss gets against the music. The old separate static topped out 5.9 dB
        // QUIETER than the music it was meant to bury, so it never buried anything.
        // 0.15, set by ear in game. The history is worth keeping: 0.55 was settled against RAW
        // files in the offline renderer, and the game plays audio normalised toward -18 LUFS —
        // about 5 dB below where those files sat — so 0.55 was always hot. It only sounded right
        // while the per-track loudness gain was being applied to the noise as well and happening
        // to cancel it. With that gone (a receiver's noise floor is fixed) the error showed, and
        // the honest level turned out to be lower again than the arithmetic suggested.
        public static float  RadioRecvNoise       = 0.15f;
        // Demodulator clicks: additive bipolar ticks that rise ABOVE the bed, isolated near the
        // threshold and merging into a rattle below it. The old "crackle" was a multiplier with a
        // ceiling of 1.0 — purely subtractive — so it could never produce one. 0 disables.
        public static float  RadioRecvClicks      = 0.18f;
        public static float  RadioRecvClickRate   = 180f;    // per second at the very bottom
        // Where the SHARED hi-cut lands with no signal, and the noise's own de-emphasis corner
        // (2122 = 75 µs US, 3183 = 50 µs EU). De-emphasis is the one stage that must NOT be
        // shared: the transmitter pre-emphasises, so for the music it cancels and only the noise
        // eats the curve. That is why between stations is a dark ROAR and a weak station is a
        // bright hiss — the off-station sound being the darker of the two.
        public static float  RadioRecvHiFloorHz   = 1800f;
        public static float  RadioRecvDeemphHz    = 2122f;

        // ── THE LEVEL LAW, and the one number that decided the whole thing ──────────────────
        // FloorDb is how far the station is allowed to sink. SMALL IS RIGHT, and this took a
        // long time to see: pulling it down 26 dB IS a fade, however much noise you layer over
        // it, and every version that did so was described as "the music fades out and the static
        // fades out, like they are two different things". A real receiver holds the programme at
        // full level and lets the NOISE do all the moving — the station is buried and then
        // revealed, never faded up. Five decibels is "barely moves", and that was the difference.
        //
        // MuteWidth is the SNR span it sinks over, wide enough that the station is dropping WHILE
        // the noise rises rather than holding still and then stopping.
        public static float  RadioRecvFloorDb     = 5f;
        public static float  RadioRecvMuteWidth   = 5f;
        // Damage, not just dulling. Clean music behind a blanket reads as a fader whatever you do
        // to the treble; a station that is clipping and squashed reads as a receiver. Unity
        // small-signal gain, so it dirties rather than boosting.
        public static float  RadioRecvDrive       = 0.15f;
        // The noise has to MOVE or it reads as a sound effect. Wander is a slow random drift (a
        // real AGC breathing as the signal fades); Flutter is faster movement on top, built from
        // BAND-LIMITED NOISE rather than an oscillator — two fixed sines gave the wobble a rate,
        // and a rate is a metronome you can hear counting.
        public static float  RadioRecvWander      = 0.55f;
        public static float  RadioRecvFlutter     = 0.30f;
        public static float  RadioRecvFlutterHz   = 14f;

        // ── THE ARRIVAL ────────────────────────────────────────────────────────────────────
        // Losing a station was right from early on. Coming back was wrong for four consecutive
        // rounds, and the reason turned out to be that NOTHING was happening to the music.
        // Measured, the deepest treatment an arriving station ever got was −3 dB at 3.75 kHz with
        // its bass, its dynamics and its stereo image completely intact — i.e. the source file,
        // fading up, with noise laid over it. Which is exactly what it sounded like: "I get no
        // sense that we're tuning into anything."
        //
        // Mangle is the fix and it is the load-bearing one. A weak station is not clean audio
        // under noise, it is WRECKED audio: squeezed into LowCut..HighCut by a narrow IF and a
        // small speaker, driven into distortion, and flattened by the station's own processing on
        // top of the receiver's AGC. All of it then lets go as the station locks. 0 = the old
        // behaviour (nothing happens to the music); 1 = full.
        //
        // LowCutHz is the one nothing had ever touched, and it matters twice over: bass is most
        // of what makes a source sound hi-fi, AND cutting it first is what turns Drive into
        // midrange grit instead of mud. MakeupDb puts back the level that band-limiting costs,
        // because a station that gets quieter as it arrives is the fade this all exists to avoid.
        public static float  RadioRecvMangle      = 1f;
        public static float  RadioRecvMakeupDb    = 9f;
        public static float  RadioRecvLowCutHz    = 400f;
        public static float  RadioRecvHighCutHz   = 2600f;
        // How far into the arrival the hiss lasts, and the residual bed that never fully goes.
        // Tied to the signal alone the hiss cleared while the station was still opening, which
        // left clean music fading in with nothing over it — and a fade with nothing over it is
        // just a fade. Absolute zero reads as "sound effect ended" rather than "receiver".
        public static float  RadioRecvNoiseHold   = 1f;
        public static float  RadioRecvNoiseFloor  = 0.06f;
        // The treble window, as positions on the 0..1 signal ramp, shared by both directions.
        // Keyed to ramp position rather than SNR because SNR saturates near its maximum long
        // before the station is merely audible, so anything read off it fires early and lands
        // inside the knee — which made the treble a step rather than a recovery.
        public static float  RadioRecvWindowA     = 0.28f;
        public static float  RadioRecvWindowB     = 0.912f;
        // Where the stereo decoder latches, and how fast the image opens once it does. A decoder
        // LATCHES; it does not glide, and it is the LAST thing to come good — after the treble.
        // Hysteresis of 0.14 is baked in: a lock is harder to acquire than to keep.
        public static float  RadioRecvLockAt      = 0.52f;
        public static float  RadioRecvLockMs      = 9f;

        public static float  RadioStaticVolume  = -1f;
        public static float  RadioStaticLowHz   = 50f;     // was 350 — the body of the roar lives here.
                                                           // 50 is the C's own floor (sfx_capture_static_tone
                                                           // clamps it), so anything lower reads as 50.
        public static float  RadioStaticHighHz  = 2100f;   // the de-emphasis corner, -6 dB/oct above
        // Sputter. Not decoration: below the FM threshold the noise instantaneously overtakes the
        // carrier and each overtake is a phase step the demodulator turns into a CLICK. They start
        // as isolated ticks and merge into a rattle as the signal goes. 0.8 is a lot of rattle —
        // right for the dead middle of the band, possibly too much as a constant bed.
        public static float  RadioStaticCrackle = 0.8f;
        // Power-click character, one set per click (all hot-reload — heard on the next dial):
        //   Hz       = tone cap — higher = bright tick, lower = dull thud.
        //   Snap     = 0..1 balance of the bright noise ping vs the low thump (1 = all snap).
        //   LengthMs = the thump's decay; the whole click lasts ~3.3× this.
        //   Body     = 0..1 mix of the shell-knock resonator — the "tok" of a real dial body.
        //   BodyHz   = the knock's ring pitch (plastic ~900-1400, wood/bakelite ~500-800).
        //   Double   = 0..1 level of a second micro-impact ~12ms after the first (the detent's
        //              spring release then the stop landing — the mechanical "cl-ick"; 0 = single).
        //   Volume   = that click's own level; -1 = inherit RadioSfxVolume.
        // THREE clicks, because they are three different objects. Power on/off is a TOGGLE: a
        // lever going over centre and then the mechanism hitting its stop — two impacts, a low
        // heavy shell. A station change is a DETENT: a ball dropping into a groove — one bright
        // tick, a small tight shell, over in a few milliseconds. Reusing the toggle for the dial
        // is why turning the knob did not sound like turning a knob.
        public static float  RadioClickOnHz        = 8722f;
        public static float  RadioClickOnSnap      = 0.72f;
        public static float  RadioClickOnLengthMs  = 60f;
        public static float  RadioClickOnBody      = 0.37f;
        public static float  RadioClickOnBodyHz    = 5100f;
        public static float  RadioClickOnDouble    = 0.8f;
        public static float  RadioClickOnVolume    = -1f;
        public static float  RadioClickOffHz       = 8722f;
        public static float  RadioClickOffSnap     = 0.7f;
        public static float  RadioClickOffLengthMs = 68f;
        public static float  RadioClickOffBody     = 0.4f;
        public static float  RadioClickOffBodyHz   = 4350f;
        public static float  RadioClickOffDouble   = 0.8f;
        public static float  RadioClickOffVolume   = -1f;
        public static float  RadioClickDialHz       = 8187f;
        public static float  RadioClickDialSnap     = 0.84f;
        public static float  RadioClickDialLengthMs = 44f;
        public static float  RadioClickDialBody     = 0.5f;
        public static float  RadioClickDialBodyHz   = 5949f;
        public static float  RadioClickDialDouble   = 0.52f;
        public static float  RadioClickDialVolume   = -1f;
        // How much each firing differs from the last, 0..1, shared by all three. No two detents
        // on a real mechanism are identical, and a fixed waveform repeated on every notch is the
        // thing that gives a UI sound away as a sound EFFECT. 0 = every click identical.
        public static float  RadioClickVary         = 0.5f;
        // Play the RECORDED clicks from Sounds/ instead of synthesizing them. The synth stays in
        // the build as the fallback: a missing or unreadable file makes the radio quieter, never
        // broken, and it is still what a user gets if they delete the folder.
        public static bool   RadioClickSamples      = true;
        // How far the playback rate wanders from one firing to the next, in Hz either side of the
        // file's own rate. One recording repeated verbatim is exactly what gives a UI sound away;
        // 2000 Hz on a 48 kHz file is about 4%, heard as "another one of those" rather than as a
        // different sound. 0 = every firing identical.
        public static float  RadioClickSampleJitterHz = 2000f;
        // ── Spatial curve (live-tunable; pushed to the native spatializer every tick) ──────────────
        // MinDistance = full-volume plateau radius (move freely inside it, level flat); past it the
        // level fades to silence at Range. Falloff = fade steepness (1 = straight amplitude ramp, >1
        // steeper just past the plateau / more responsive per metre, <1 gentler). Muffle = distance
        // low-pass strength 0..1 (0 = no dulling with distance, higher = duller far away).
        public static float  WorldEmitterMinDistance = 1f;
        public static float  WorldEmitterFalloff     = 2.5f;
        public static float  WorldEmitterMuffle      = 0.9f;
        // Stereo width 0..1: how hard a left/right speaker pans. 1 = full pan (far ear can go silent);
        // lower keeps the sound in BOTH ears (a source at 90° is still heard on the far side, quieter).
        public static float  WorldEmitterStereoWidth = 0.8f;
        // Occlusion: solid blocks between you and a speaker muffle it (volume only for now; a low-pass
        // comes with the radio-coloration phase). PerBlock = gain multiplier per solid block on the
        // line of sight (0.5 ≈ −6 dB/block); Floor = the minimum gain however many walls there are.
        public static bool   WorldEmitterOcclusion         = true;
        // Route occlusion through the NATIVE acoustic model (cmm_acoustics) instead of the
        // shipped ear-anchored flood. Off by default until it has been heard in game: the old
        // path stays reachable so the two can be A/B'd without a rebuild.
        // ── Field occlusion (the rebuild) ────────────────────────────────────────────────────
        // ONE occlusion field, centred on the listener, read by every source. Off by default until
        // it has been judged by ear; `cmm acoustics model field` switches to it live.
        // DetourDb is what a block of going-the-long-way costs, and it is the ONLY tuning constant
        // the model has beyond the shared material curve -- everything else comes from the blocks.
        // THE DEFAULT OCCLUSION MODEL (2026-08-20). One listener-anchored field, flooded a few
        // times a second and read by every source, replacing the per-source ear floods and the
        // field cache. Off until tonight because it had not been judged by ear; the two defects
        // that made that judgement impossible were not in the model at all -- one of its three
        // buffers was never flooded, and the emitter audio lanes had no ring cushion.
        // `cmm acoustics model native|legacy` still pins the old paths for an A/B.
        public static bool   WorldEmitterFieldOcclusion = true;
        // 1.5, up from 0.35. This is the model's stand-in for DIFFRACTION -- what a block of
        // going the long way round costs -- and it is the dominant term in a real building, where
        // rooms connect and the sound routes through a doorway two rooms over rather than through
        // a wall. At 0.35 a twenty-block detour was 7 dB; sound bending round a single edge loses
        // 10-15 dB in reality, so the model was charging almost nothing for the journey.
        //
        // It could not safely be raised before: the flood's cutoff budget included the
        // straight-line distance charge, so turning this up silently shrank the field (at 2 dB a
        // block it stopped after 30 blocks, at 12 after one step). That is fixed -- the budget now
        // scales with the window -- so this is free to be a real number.
        //
        // Open air is unaffected at any value: excess is material + (pathLength - straightLine) x
        // this, and the straight-line term cancels against the free-space reference.
        // BACK TO 1.5 (2026-08-26). 3 was agreed by ear on the 25th and the arithmetic does not
        // support it: Shape runs the total through 45*tanh(total/45), so ONE right-angle corner
        // (a 20+20 block L -- route 40, straight line 28.3, so 11.7 blocks of detour) costs
        //     1.5 -> 16.7 dB      2.0 -> 21.5 dB      3.0 -> 29.4 dB
        // against a real single-edge diffraction loss of 10-15 dB. At 3 the model charges about
        // two edges for one corner, and Chris's verdict after testing was the obvious consequence:
        // "sound doesn't travel far enough".
        //
        // The reasoning that raised it compared a TWENTY-BLOCK detour at 0.35 (7 dB) against a
        // SINGLE-EDGE loss. Those are not the same measurement. 0.35 really was far too low; the
        // size of the correction is what overshot.
        //
        // Detour can never silence a source -- the hard stop is material-only, deliberately -- so
        // this knob is pure attenuation and everything it does is audible rather than binary.
        // If sound is coming THROUGH A WALL rather than around a corner, this is the wrong knob:
        // that is WallDb/WallPow. The diagnostic splits them ("wall X, detour Y").
        public static float  WorldEmitterFieldDetourDb  = 1.5f;
        // HOW FAST OCCLUSION IS ALLOWED TO CHANGE, in dB per second, per speaker point.
        //
        // The field re-floods a few times a second and each flood is an independent answer, so
        // walking past a doorway swings the route from "through the opening" to "the long way
        // round" between one flood and the next. That is not noise -- both answers are right --
        // but published raw it is a lurch. Measured in game: excess jumping 2.7 -> 10.7 dB in
        // 0.25 s while walking, with the published extra distance swinging 6 to 51 blocks.
        //
        // Limited in dB rather than in the "extra virtual distance" the mixer wants, because dB is
        // what the ear hears and the two are not proportional: at 5 blocks from a speaker, extra
        // 10 -> 24 blocks is 8 dB, while at 50 blocks the same 14 blocks is a fraction of one. The
        // existing blocks-per-second limiter downstream could not tell those apart, which is why
        // it was set fast enough (50 blk/s = 12.5 blocks per publish) to pass nearly everything.
        // 40, not 12. This is a rate limit on a change of up to 60 dB, so 12 dB/s meant a door
        // shutting took FIVE SECONDS to arrive: 57 / 12 = 4.75, plus the re-read and the flood on
        // top. Reported, fairly, as "the delay is awful when the door is closed". The number was
        // picked when the model's answers were noise and needed damping; now that they are real
        // events it was throttling the events themselves.
        //
        // A door is exempt entirely (see ComputeOcclusion): it is a discrete, visible, deliberate
        // act, and the sound should land with the latch. The limiter is for the model changing its
        // mind, not for the world changing.
        public static float  WorldEmitterFieldSlewDb   = 40f;
        // TESTING ONLY. Mutes the GAME's own audio (Unity's AudioListener) while leaving CMM's
        // emitters and music running, because those go out through the native mixer and never
        // touch the listener. A door slam is far louder than a speaker heard through a wall, so
        // judging door timing by ear is otherwise judging the slam. Hot-reloadable: flip it in
        // settings.xml with the game running.
        public static bool   DebugMuteGameAudio       = false;
        // HOW MUCH OF A DOOR'S SWING THE SOUND WAITS FOR, 0..1. The occlusion tracks a door's
        // ANIMATION -- a leaf half open is half a door -- which is physically right and reads as
        // lag, because the seal only completes when the leaf stops moving, half a second to a
        // second after the click.
        //
        // Measured, latch to published field: 33-79 ms. The pipeline is not the delay; the ramp
        // is. So the swing is remapped rather than removed: the sound reaches full seal by the
        // time the leaf is this far through its travel, and holds. 0.35 means the door sounds shut
        // about a third of the way through closing, which is roughly when a real door's gap stops
        // mattering, and it lands close enough to the click to feel like the click caused it.
        //
        // 1 restores tracking the whole swing exactly; 0 snaps at the latch and ignores the
        // animation entirely.
        // DEFAULT 1 = INERT, and it should stay that way. This scales da.Openness, and Openness is
        // not a raw animation fraction: it is already the output of the OPEN-AREA model -- the
        // measured square metres of doorway currently unblocked, through a soft knee. Scaling that
        // squashes all of it with one blunt global number, which is what I did before noticing it
        // existed.
        //
        // If a door's shut lands too late, the knob is WorldEmitterDoorHalfArea -- the size of hole
        // that gets you halfway to hearing through. Lowering it seals earlier, while the leaf is
        // still visibly moving, and it is absolute, so it means the same thing on a closet door and
        // on a seven-metre roll-up.
        //
        // Kept only as a blunt override for someone who wants the animation ignored outright
        // (0 = snap at the latch). Measured latch-to-published-field is 33-79 ms, so nothing here
        // is latency; it is all the shape of the ramp.
        public static float  WorldEmitterDoorSwingFrac  = 1f;
        // HOW HARD A PARTLY OPEN FACE ATTENUATES: transmission = openFraction ^ this.
        //
        // The other two material knobs set what a SOLID block is worth, and a solid block is
        // already effectively sealed, so neither of them changes what a real building sounds
        // like. What decides that is how a face with a gap in it scores, and that was hard-coded
        // linear in area. Measured in game across 194 samples, the material term never once
        // exceeded 11.1 dB: every wall in a POI has some opening in it and the route always found
        // one cheap enough to walk through.
        //
        // Linear area is also too generous to be physical. A small aperture radiates much less
        // than its share of the wall, because the edges cost you; the fraction wants an exponent.
        //
        // 2.0 is the default for a second reason: it repays what the cell-shape fix cost. Faces
        // used to be 3x3 sub-blocks and are now 1x3, so a one-block hole went from a NINTH of the
        // face to a THIRD, and every partial wall got roughly twice as transparent overnight.
        // Squaring the fraction puts a one-block hole back at a ninth.
        //
        // 1.0 restores the linear behaviour exactly. A fully open face (1) and a sealed one (0)
        // are unaffected at any value, so open air stays at 0 dB regardless.
        // 1 = the plain open-area fraction. It was 2 to compensate for faces being averaged over
        // several sub-blocks, and there are none left: at one block per cell a face IS one block.
        // Leaving it at 2 would silently square every material's transmission and double its dB --
        // a 20.2 dB wood wall reading 39.2. Kept as a knob because it still works on the partial
        // transmission a single block can report (damaged blocks, terrain density, a door
        // mid-swing), where raising it makes part-open geometry heavier.
        public static float  WorldEmitterFieldAperturePow = 1f;
        // What one block of material is worth, as dB = WallDb * loss^WallPow.
        //
        // DERIVED FROM THE SHIPPED MODEL, not invented. The field first borrowed the native
        // model's curve, which was calibrated against a spec band saying a shut wood DOOR should
        // be 4-9 dB down -- so it charged 6 dB for a wood WALL where the shipped model charges 20,
        // and walls audibly barely existed ("occlusion on field seems almost broken"). Fitted
        // instead to what the shipped model actually does at range 60, eight blocks out:
        //
        //     one wood block      -20.2 dB        one concrete block   -50.5 dB
        //
        // which gives WallDb 59.5 and WallPow 1.56. Both hot-reload, so they can be turned by ear
        // against `cmm acoustics model auto` in the same spot.
        public static float  WorldEmitterFieldWallDb    = 59.5f;
        public static float  WorldEmitterFieldWallPow   = 1.56f;
        // The two bends ShapeOcclusion puts on the shipped model, ported to the field in dB.
        // See FieldOcclusion.Shape. Shaping=false is exactly the straight line the field used
        // before, so the whole change is one setting away from being undone.
        public static bool   WorldEmitterFieldShaping   = true;
        public static float  WorldEmitterFieldCeilingDb = 45f;
        public static float  WorldEmitterFieldHardStopDb = 60f;

        public static bool   WorldEmitterNativeAcoustics   = false;
        // Occlusion (dB) at which the emitter tone reaches full dullness, when the native acoustic
        // model is driving. 8 dB is about what the shipped model's virtual distance produced for a
        // wall at mid range, so a wall keeps sounding like a wall.
        public static float  WorldEmitterOcclusionFullDb   = 8f;
        // Occlusion is modelled as VIRTUAL DISTANCE: a wall makes the speaker sound farther (quieter +
        // duller on the same smooth curve) instead of a hard volume cut — so it fades gradually, not
        // all-or-nothing. WallDistance = blocks of virtual distance a FULL solid wall adds on the path.
        // Two curves shape it (see EmitterAcoustics.ComputeOcclusion):
        //  • MaxDistance is the SOFT-KNEE ceiling: the total detour is passed through maxDist·tanh(x/maxDist)
        //    so stacking walls gives DIMINISHING RETURNS and asymptotes to an audible floor — never silent
        //    by muffle/detour alone (a box you stand behind stays audible).
        //  • HardStop is a separate cut keyed ONLY to SOLID material actually transmitted through: once the
        //    through-solid virtual distance reaches HardStop the gain is pushed past range → true silence
        //    (a sealed thick vault goes dead), smoothstepped in from 55% so the dead-zone edge isn't a cliff.
        //    A long OPEN-AIR detour has zero solid cost, so it never triggers this. Denser materials reach
        //    HardStop in fewer blocks (metal ~2½, stone ~3, wood ~5 walls at the defaults).
        public static float  WorldEmitterOcclusionWallDistance = 35f;
        public static float  WorldEmitterOcclusionMaxDistance  = 30f;
        public static float  WorldEmitterOcclusionHardStop      = 68f;
        // Cap (virtual blocks/second) on how fast a speaker's occlusion may RISE from MOVEMENT — walking
        // out a doorway / behind a thick wall can flip the routed cost from near-clear to fully-blocked in
        // one 4 Hz tick, and the native glide is deliberately fast for big jumps (door tuning), so the dry
        // HARD-CUT instead of fading (log 2026-07-08 08:28:51: extra 6.8→98.4 in one tick leaving a store).
        // With the cap, movement-driven cutoffs fade out over ~a second. DOOR events bypass it (their
        // fast, precise response is tuned separately), and occlusion DROPS are never capped — sound
        // ARRIVING should track the event. 0 disables the cap (old behavior).
        public static float  WorldEmitterOcclusionFadeRate      = 50f;
        // THE ONE SMOOTHING TIME CONSTANT, milliseconds, at the very bottom of the chain. Every
        // occlusion change -- doors, blocks, walking behind a wall -- eases in over this, and so
        // does the muffle that rides on it. Constant by design: the door model leads the pipeline
        // by a fixed time to cancel it, and a fixed lead can only cancel a fixed lag. Raise it to
        // make ALL occlusion changes glide more; lower it to make them snap.
        public static float  WorldEmitterOcclusionSlewMs         = 50f;
        // How often the occlusion decision and the speaker point-set are refreshed, in Hz. The
        // listener basis is pushed every frame regardless -- this is only how fast WALLS catch up.
        public static float  WorldEmitterTickHz                  = 10f;
        // Path routing: when a speaker's direct line is blocked, FLOOD-FILL through open air (a portal
        // search) to find whether sound has a real way around the wall / up the stairwell, and how far
        // it has to travel — so it routes through openings instead of slamming to the floor gain. This
        // is deterministic (no ray noise). FloodRadius = how many blocks the air search reaches from
        // the ear (bigger = finds longer ways around, more CPU). Set PathRouting false for direct-only.
        // NOTE (2026-07-05): path routing was briefly slated for removal, then re-enabled (BoundaryBlend
        // smooths the search-box edge). But routing and REVERB fight: a big FloodRadius follows an open
        // hallway around a corner, keeping the DRY alive where it should fade — which masks the wet reverb
        // cue (the dry drop is what lets reverb bloom down a hallway). So the default is a MIDDLE value:
        // routing reaches nearby obstacles (kills the block-shadow) but a speaker far around a corner
        // falls out of the box → BoundaryBlend straight-lines the remainder through the wall → dry drops →
        // reverb blooms. Hot-reloadable: dial 1 (reverb-forward) ↔ 24 (routing-forward) live to taste.
        public static bool   WorldEmitterPathRouting = true;
        public static int    WorldEmitterFloodRadius = 32;
        // FIELD CACHE ("Phase 2", 2026-07-11): occlusion from ONE CACHED cost field PER SPEAKER
        // (seeded at the static speaker, box filled — no node budget) instead of re-flooding
        // around the moving ear each tick. The listener samples a stationary field ⇒ walking is
        // spatially continuous (offline judge: −83…97% adjacent-cell >12 dB jumps vs the ear-
        // anchored flood). Legacy path still serves door mid-swings (its tuned per-tick openness
        // sampling), the warm-up window while fields build, and stays the architecture for future
        // MOVING sources. FieldRadius = field half-extent in blocks (16–48; 48 ≈ 3.6 MB/speaker);
        // FieldRefresh = seconds between quiet rebuilds (catches block edits; door toggles force
        // an immediate rebuild). All hot-reloadable; toggle off ⇒ pure legacy behavior.
        public static bool   WorldEmitterFieldCache  = true;
        public static int    WorldEmitterFieldRadius = 48;
        public static float  WorldEmitterFieldRefresh = 60f;
        // Bounce-energy reverb carry: grade the next-room/around-corner reverb by the ENERGY
        // surviving the route's wall BOUNCES (route length ÷ room mean-free-path, ×reflectivity
        // per bounce) instead of raw meters — reflective caves ring around corners until the
        // energy is truly spent; absorptive rooms still die fast. Hot; false = distance grading.
        public static bool   WorldEmitterReverbBounce = true;
        // DIRECTIONAL occlusion: when the direct line to a speaker runs through solid rock, re-aim the
        // virtual source to the direction the sound actually ARRIVES from — the air route the flood found
        // (down the corridor), not the straight-through-the-wall bearing. Distance/attenuation are
        // unchanged (those already follow the air path); only the PAN is rotated, blended in by how much
        // rock the direct line crosses. Needs PathRouting on (uses the flood field). Hot-reloadable.
        public static bool   WorldEmitterOcclusionDirectional = true;
        // Reverb: a room probe (rays from the ear, hit distance + material) estimates how enclosed,
        // big and reflective the space is and drives a reverb on the station's reflections. The wet
        // (reflected) path is NOT attenuated by occlusion, so behind a wall the direct sound drops but
        // the reverb carries — the "it bounced / it's in another room" cue. ReverbWet = master wet
        // level 0..1 (0 = dry). Room size / decay / damping are estimated automatically per space.
        public static bool   WorldEmitterReverbEnabled = true;
        public static float  WorldEmitterReverbWet     = 0.6f;
        // ReverbDecay = tail-LENGTH lever 0..1 (scales the auto room-size → Freeverb feedback), separate
        // from Wet (LEVEL). Lower = shorter/tighter reverb, higher = a longer ring. Two independent knobs:
        // Wet says how loud the reflections are, Decay says how long they hang.
        public static float  WorldEmitterReverbDecay   = 0.6f;
        // ReverbRange = how far (blocks) the room probe's rays reach before a surface reads as "open sky".
        // This is what enclosure/size are measured against — a space BIGGER than this reads as outdoors and
        // the reverb fades out (why a large cave went dry). Raise it so big caverns register as enclosed;
        // the cost is longer ray marches per reverb tick (14 rays), which is cheap at the few-Hz reverb rate.
        public static float  WorldEmitterReverbRange   = 64f;
        // ReverbCarry 0..1: level of the CARRY reverb bus — the deliberate "muffled music from the next
        // room / down the tunnel" cue that survives occlusion of the direct sound. This is a TASTE knob,
        // not a physics patch: your own room's reverb (the LOCAL bus) is fed the arriving occluded signal,
        // so it follows walls/water/hard-stop automatically, and the carry is hard-blocked by real
        // material (solid seal, not adjustable — bleed through solids shouldn't exist at any setting;
        // open-air corners still carry because air has no solid cost). 0 = only your own space ever
        // reverberates. Replaces the old ReverbOcclusion compromise dial.
        public static float  WorldEmitterReverbCarry = 0.8f;
        // Mid-swing door acoustics are driven by the physical GAP IN METRES (swing fraction × the door's
        // largest dimension), so one pair of knobs is right for every door size: a 3 m garage door with a
        // metre still to go is a huge aperture (full sound), a vault leaf the same fraction open is a
        // hand-width crack. DoorGapFull = opening (m) at/above which sound is acoustically fully open;
        // DoorGapClosed = opening (m) at/below which it's fully shut (a sliver of crack is ignored —
        // heavy doors read as a slam). Both hot-reload.
        // How far AHEAD of the door's real pose to compute its sound, in milliseconds, to cancel
        // the pipeline's own delay (geometry read + flood + emitter tick + glide). 0 disables it.
        public static float  WorldEmitterDoorLeadMs      = 225f;   // MEASURED pipeline + the 50 ms slew
        // HOW BIG A HOLE GETS YOU HALFWAY TO HEARING THROUGH, in square metres. This is the one
        // number that decides when a door stops muffling, and it is ABSOLUTE, not a fraction of
        // the door: half a metre of daylight sounds like half a metre of daylight whether it is a
        // closet door or a seven-metre roll-up. Raise it if doors sound open before they look
        // open; lower it if you can see through a door and still hear nothing.
        public static float  WorldEmitterDoorHalfArea    = 0.35f;
        // ...and the same thing as a FRACTION of the door's own area. The knee is the LARGER of
        // the two, so small doors keep the absolute one and big doors get one in proportion to the
        // hole they are. Without this a 7 m roll-up is half-open after five centimetres of lift.
        public static float  WorldEmitterDoorHalfAreaFrac = 0.15f;
        // How abrupt the transition is. 1 = the plain soft knee. Above 1 the door holds its muffle
        // and then lets go all at once; below 1 smears the transition across more of the swing.
        public static float  WorldEmitterDoorKnee        = 1f;
        // CLOSING swings only. The animation eases, so clip time misdescribes the leaf's travel --
        // but only the CLOSE has its acoustically decisive stretch (the last small aperture) at the
        // end of the clip, where this exponent bites. On an OPEN the decisive stretch is at the
        // start, and easing it there simply made every long door open too slowly. 1 disables it.
        public static float  WorldEmitterDoorClipEase    = 1.5f;
        // Per-door-type: the FRACTION OF THE ANIMATION CLIP over which the doorway actually
        // finishes opening. Some rigs keep animating after the leaf has stopped -- the cellar door
        // overshoots and settles, the vault hatch runs a 3.1 s clip -- so the sound goes on opening
        // after the doorway is already open. "match=fraction;..." , match is a case-insensitive
        // SUBSTRING of the block name, first match wins, unlisted doors use the whole clip.
        public static string WorldEmitterDoorSwingSpans = "cellarDoor=0.4;vaultHatch=0.6;vaultDoor=0.6";
        // false = ignore the measured leaf's MOTION and drive every door's openness from its
        // animation clip. Default false, on evidence: of fourteen door types only the steel garage
        // 4x3 tracks its leaf honestly, and it is the one door whose occlusion reads jittery. The
        // aperture is still MEASURED -- its area is what scales the knee -- only the moment-to-
        // moment travel comes from the clock. True re-enables leaf tracking.
        public static bool   WorldEmitterDoorMeasureLeaf = false;

        // DoorSealTypes: comma-separated block-name PREFIXES of doors that are AIRTIGHT when fully shut
        // and undamaged — a closed vault door is a pressure seal, not a metal panel's worth of muffle.
        // Sealed doors read as gate-class solid (true silence via the hard-stop), and because the seal
        // density ramps in as the leaf closes, the silence lands while the door is still visibly closing
        // (the "slam") instead of only after — no gap/fraction knob could ever get there, since the dry
        // fade bottoms out at "muffled", never "dead". Damage breaks the seal: any visible damage state
        // drops the door back to ordinary material acoustics. Hot-reloads.
        public static string WorldEmitterDoorSealTypes = "vaultDoor,vaultHatch";

        // A DOOR IS NOT A WALL, AND A METAL DOOR IS NOT A METAL WALL. MaterialLoss reads a block's
        // SurfaceCategory, and Mmetal answers 1.0 -- the model's maximum, identical to a steel wall.
        // A shut sheet-metal roll-up garage door therefore blocked as hard as anything in the game:
        // measured 2026-08-22 on rollUpDoor3x3White, wall 59.5 dB, and after shaping 158.9 dB of
        // excess against a speaker 3.1 blocks away. That is silence, and it is a 3x3 hole in a wall.
        //
        // Real doors leak -- gaps at the jamb, at the threshold, around the panels. Project Acoustics
        // clamps its occlusion for the same reason and says so: "forming a perfect acoustical seal is
        // difficult and common doors have a degree of leakage". This is that clamp in this model's
        // units. Applies ONLY to things that open (Place=Door/DrawBridge), so metal WALLS are
        // untouched, and seal-class doors take AirtightLoss before the cap is consulted -- a vault
        // door is the one thing in the game that genuinely does seal.
        //
        // 0.60 is 26.8 dB at the shipped wall curve: clearly the heaviest door in the game against a
        // wood door's 20.2, and clearly not a wall's 52. Wood doors are 0.5 and so are unaffected.
        // 0 disables the cap.
        public static float  WorldEmitterDoorMaxLoss  = 0.6f;
        // Doors that are structurally WALLS, exempt from WorldEmitterDoorMaxLoss and priced as
        // their own material. Case-insensitive substring of the block name, comma separated.
        public static string WorldEmitterDoorFullLossTypes = "drawBridge";
        // ReverbRangeMult (≥1): the reverb's audible reach as a multiple of the dry Range. The reflections
        // carry FARTHER than the direct sound, so a brief step past the dry range in a connected cave doesn't
        // drop the tail (it fades continuously past the dry edge instead of cutting at it). Safe in the open:
        // with no enclosure the wet is ~0, so a bigger reach multiplies nothing. 1 = reverb stops at the dry
        // range (old behaviour); ~1.5–2 bridges out-of-range dips in a cave. Hot-reloads.
        public static float  WorldEmitterReverbRangeMult = 1.5f;
        // ReverbPan 0..1: how much the (otherwise diffuse) reverb is PANNED toward the source's traced air-route
        // bearing. 0 = fully diffuse (reflections surround you); 1 = fully located. A middle value keeps the
        // spacious reverb but leans it toward the source, so at distance — where the wet dominates the faded
        // dry — you can still tell which way the sound is coming from (in a cave, down the tunnel). Hot-reloads.
        public static float  WorldEmitterReverbPan = 0.5f;
        // ReverbDepth 0..1: how much the reverb EVOLVES with acoustic depth — the air-route distance from
        // you to the station (real distance + the open-air detour around bends; walls don't count, the
        // seal gate covers those). Deeper = the wet gets quieter, DARKER (low-passed) and more damped, the
        // way a reverberant field really decays down a tunnel: near the radio it's a bright splash, deep
        // in the cave a sinking dark rumble, with no cliff anywhere. 0 = flat wet at any depth (the old
        // behaviour); 1 = full grading. Hot-reloads.
        public static float  WorldEmitterReverbDepth = 0.7f;
        // Radio coloration — the "through a speaker" voice, applied to the station's signal before the
        // distance muffle / occlusion / reverb (so the reflections carry the colored sound too). Three
        // independent 0..1 levers, all hot-reloadable; all 0 = the clean, uncolored signal:
        //   ColorDrive  = soft-clip amount. Crushes peaks into speaker breakup/grit. Level-compensated so
        //                 it adds distortion, not volume.
        //   ColorStatic = constant static-hiss bed from the radio (spatialized + muffled like the music,
        //                 so it hisses FROM the speaker). Subtle even at moderate values.
        //   ColorVoice  = band-pass voicing blend: strips lows (<~250 Hz) and highs (>~4 kHz) — the
        //                 "small cheap speaker" body. 0 = full-range hi-fi, 1 = fully boxed-in.
        // Disabled (all 0) by default — parked until the acoustics work settles.
        public static float  WorldEmitterColorDrive  = 0f;
        // How loud a world speaker's TUNING noise is, against the personal radio's. Its own knob
        // because the receiver's tuning is otherwise shared: turning the shared trim down to
        // quieten a speaker across the room would quieten the radio in your pocket too.
        public static float  WorldEmitterStaticTrim = 0.5f;
        // How long a world speaker waits for a track's measured level before starting anyway.
        // Shorter than the personal radio's LoudnessStartHoldMs on purpose, and shortened again
        // once the receiver existed: a level jump UNDER the searching noise is most of the way to
        // inaudible, so the hold no longer has to be long enough to hide it on its own. It is the
        // dominant term in how long a retune feels -- the sweep is the small half. 0 starts the
        // station immediately and lets the level settle while it plays.
        public static int    WorldEmitterLevelHoldMs = 500;
        // The shortest a retune can take, in ms — how far the dial travels before the station is
        // allowed to come out from under the noise. A station that is already warm opens in
        // milliseconds, and without this a retune to one produced no sweep at all, which is not
        // how a set behaves: the dial passes through the same noise whatever you are tuning to.
        // The programme still STARTS on time underneath; this is only when it is revealed.
        public static int    WorldEmitterSweepMs = 250;
        public static float  WorldEmitterColorStatic = 0f;
        public static float  WorldEmitterColorVoice  = 0.5f;
        // The HAM RADIO's own coloration set (same three levers) — applied to radioHam points instead
        // of the speaker set above, so a ham always reads as a small communications speaker: voiced
        // narrow, driven slightly, a whisper of static. ON by default ("slightly distorted" is the
        // ham's character, not a tuning option); hot-reloads like the speaker set.
        public static float  WorldEmitterHamColorDrive  = 0.3f;
        public static float  WorldEmitterHamColorStatic = 0.04f;
        public static float  WorldEmitterHamColorVoice  = 0.9f;

        // ── Loudness normalization (EBU R128) ─────────────────────────────────
        // When enabled, each track's measured integrated loudness yields a per-track
        // gain (target − measured, in dB) folded into the volume multiplier. See
        // LoudnessStore / R128Analyzer. Target sits on the quiet side so most
        // corrections are attenuations (always achievable) and boosts stay modest
        // given the AudioSource.volume ≤ 1 ceiling.
        public static bool LoudnessNormalizationEnabled = true;
        public static float LoudnessTargetLufs = -18f;
        // Cap on the AUTO boost (a near-silent track can't ask for unbounded gain).
        // Attenuation is uncapped. userTrim is applied on top and clamped separately.
        public static float LoudnessMaxBoostDb = 12f;

        // Loudness scanning model: a track is measured on demand the first time it's
        // needed — the track about to play (block-before-play) plus a look-ahead of the
        // CURRENT context's exact next track, measured when the advance is pre-staged
        // (PreStageNextAdvance) — so it only measures music about to be heard. On the
        // menu, one track per context is pre-measured (the bounded prewarm in
        // MenuPrescanDrain) so every context has a ready, normalized track for its first
        // play. Each track is only ever measured once (cached in Cache/loudness.xml).
        // MeasureAhead: how many upcoming tracks to keep measured AHEAD of playback (per source —
        // the main context player and the world-emitter station each keep this many ready). Higher =
        // more tracks pre-normalized so nothing measures on-demand while playing (smoother on huge /
        // slow-storage libraries), at the cost of a little more background decoding. 0 = measure only
        // the track about to play. Hot-reloadable.
        public static int LoudnessMeasureAhead = 3;
        // How long a track may be HELD SILENT waiting for its loudness measurement before it is
        // started anyway. Nothing should be audible until it is normalised: a track that starts
        // at no correction and has its gain eased in a moment later is an audible jump, and on a
        // quiet file needing +12 dB it is a big one.
        //
        // Bounded, because a file that cannot be measured at all (decode error, unreadable) must
        // still play — silence forever is a far worse failure than one hot entrance. On expiry it
        // starts uncorrected and settles as it always did.
        //
        // Only applies when LoudnessNormalizationEnabled: with normalisation off there is nothing
        // to wait for and the hold is skipped entirely.
        public static int LoudnessStartHoldMs = 3000;
        // ...and what it plays at when the hold expires and the track is STILL unmeasured. It
        // used to be 1.0 -- no correction at all -- which is the worst possible guess. A modern
        // master sits 8 to 12 dB above the -18 LUFS target, so "no correction" means "up to 12 dB
        // hot", and on the personal radio that lands in the receiver's saturator: measured on a
        // brickwalled track, 6.7% THD with the peak pinned flat, before the volume slider, so
        // turning it down did not help. On a big library the 3 s hold expires often -- a whole
        // FLAC has to be decoded to measure it.
        //
        // -5 dB is the median correction this machine's library actually wants, so it is the
        // guess that is least wrong. It is a BIAS, not an estimate: too quiet for a moment is a
        // non-event, too loud is distortion. The real measurement still lands and eases in
        // (SettleLoudness, 0.2 s) exactly as before. 0 restores the old uncorrected start.
        public static float LoudnessUnmeasuredDb = -5f;

        // ── Progressive measurement (long files) ──────────────────────────────
        // Integrated loudness is a whole-file quantity, so nothing can be said about a track
        // until it has been decoded end to end. On a 37-minute podcast that is ~9 s, which is
        // three times StartHoldMs -- so those files always expired the hold and always started
        // at the UnmeasuredDb guess, which on them is about 12 dB from where they belong. And
        // since measurements no longer persist across launches, it happened on every rejoin.
        //
        // Measuring in SLICES fixes it without changing the measurement: the analyzer keeps
        // accumulating, and after each slice we ask it what it has so far. The first answer
        // arrives in about a second instead of nine, and each later one refines it.
        //
        // Slice length is a straight accuracy/latency trade and 5 minutes is where the evidence
        // put it: over 27 real files the worst first-answer error was 7.88 dB at a 60-second
        // slice and 0.82 dB at 300 s. Anything SHORTER than one slice is unaffected -- it
        // reaches the end of the file in the first slice, so it publishes once, exactly as
        // before. 0 disables slicing entirely (whole-file decode, the pre-1.0.5 behaviour).
        public static float LoudnessChunkSeconds = 300f;
        // Boost ceiling for a PARTIAL reading, until the whole file has been read. A quiet
        // opening measures quiet and asks for a boost the rest of the track does not want, and
        // that is the one direction that is audible as a fault (a hot entrance, into the radio
        // receiver's saturator). Attenuation from a partial is NOT capped by this -- the
        // relative gate makes the too-loud reading the reliable one. Applies only while a
        // measurement is in progress; the final reading uses LoudnessMaxBoostDb like any other.
        public static float LoudnessProvisionalMaxBoostDb = 6f;
        // How long the live voice takes to glide onto a REFINED gain (each slice after the
        // first). These corrections are small -- under 1 dB on real music -- so the ramp is long
        // enough that the level is never heard moving, only heard being right. The FIRST
        // correction on a track (from the unmeasured guess to a real reading) can be large, so
        // it scales with the size of the correction instead; see LoudnessSettleRampSeconds.
        public static float LoudnessRefineRampSeconds = 3f;

        public static bool CrossfadeEnabled = true;
        // When re-entering a context that is still audibly fading out on the
        // inactive channel, reverse that fade (ramp it back up from its current
        // volume) instead of reloading it and fading in from zero. Set false in
        // Config/settings.xml to fall back to the old reload-from-resume behavior.
        public static bool ReverseFadeReclaim = true;

        // Enemy-count thresholds exposed to the decision script as cfg.combatLowThreshold
        // / cfg.combatHighThreshold. The stock context.lua does its combat tiering via
        // weighted scoring instead, so these only matter to a custom script that reads them.
        public static int CombatLowEnemyThreshold = 1;
        public static int CombatHighEnemyThreshold = 4;

        // ── Context detection tuning (Config/settings.xml) ─────────────────────
        // Max distance (game units) an enemy is counted toward combat/tension state.
        public static float EnemyDetectRange = 30f;
        // Max distance (game units) another player counts as "with you" for the
        // multiplayer social signals: an enemy aggroed onto a party member / friend
        // within this range counts as your combat, and allies within it scale combat
        // intensity (see ContextResolver player scan). Independent of EnemyDetectRange
        // so "fighting alongside" can be tuned wider/narrower than "an enemy is near".
        public static float PlayerDetectRange = 30f;
        // Squad-shared combat: how close (game units) another player must be for an
        // enemy fighting THEM to count as your combat too. A fully independent setting from
        // PlayerDetectRange, with its own default — the "share a squadmate's fight" radius is
        // tuned separately from the ally/stranger proximity counts that scale combat intensity.
        public static float CombatShareRange = 30f;
        // Whether squad-shared combat requires the other player to be an ally (in your
        // party or an ACL friend). When false, ANY nearby living player's fight counts as
        // yours. Defaults to true (party/friend only), matching the original behavior.
        public static bool CombatShareRequireAlly = false;
        // Distance (game units) from a bedroll or land-claim that counts as "home".
        public static float HomeDetectRange = 70f;
        // Health fraction (0–1) below which combat escalates to Low_Health_Panic.
        public static float PlayerHealthDangerThresholdPercent = 0.30f;
        // CombatExitDelay: with no nearby aggro for this long (seconds), stop playing
        // combat music. CombatCooldown: how long the encounter's tier stays REMEMBERED
        // after that — re-aggro within this window resumes the previous (latched)
        // intensity instead of restarting at the bottom tier; past it the encounter is
        // forgotten and the next fight starts fresh. The tier latches upward only and
        // never steps back down mid-encounter.
        public static float CombatExitDelay = 2f;
        public static float CombatCooldown = 10f;

        // ── Playback keybinds (Config/settings.xml) ────────────────────────────
        // Live controls for whatever music is actually playing (menu or in-game).
        // Gated behind Alt by default so they don't collide with game/chat input.
        // Ctrl is avoided because it's the game's crouch toggle; Alt is unbound in
        // 7D2D. Set RequireCtrl/clear RequireAlt or both for other bindings.
        // Centre-screen banner (the combat/tension mute announcement): how long it holds at
        // full strength, and how long it then takes to fade out. Held time first, then the
        // fade, so total time on screen is the two added together.
        public static float BannerHoldSeconds = 1f;
        public static float BannerFadeSeconds = 1f;

        public static bool KeybindsEnabled = true;
        public static bool KeybindRequireCtrl = false;
        public static bool KeybindRequireAlt = true;
        public static KeyCode KeyNextTrack  = KeyCode.RightArrow;
        public static KeyCode KeyPrevTrack  = KeyCode.LeftArrow;
        public static KeyCode KeyVolumeUp   = KeyCode.UpArrow;
        public static KeyCode KeyVolumeDown = KeyCode.DownArrow;
        // Track flags + the combat-music toggle (same modifier gate as the keys
        // above). Ban/favourite persist per-track in Cache/loudness.xml alongside
        // the trim; the combat toggle is session-only. Defaults sit on the
        // Ins/Del/Home cluster — NEVER on letters: the game binds letters
        // (D = strafe, F = light) and Alt is the free-look hold, so Alt+letter
        // fires during normal play.
        public static KeyCode KeyBanTrack         = KeyCode.Delete;
        public static KeyCode KeyUndoBan          = KeyCode.End;   // unbans the last-banned track (25-deep, session)
        public static KeyCode KeyFavouriteTrack   = KeyCode.Insert;
        public static KeyCode KeyToggleCombatMusic = KeyCode.Home;
        // COMBAT-MUSIC MUTE, PERSISTED. Alt+Home flips this and writes it straight back to
        // Config/settings.xml, so "I don't want combat music" survives a restart instead of
        // having to be pressed again every session. It is a real setting as well as a keybind:
        // set it true here and the game starts muted with no keypress at all.
        //
        // Writing into the mod's OWN config file is deliberate — that is where a player looks
        // for it — and it carries one cost worth stating plainly: a mod UPDATE replaces
        // settings.xml and resets this to the shipped default. The alternative (a file under
        // Cache/, where the track flags and the radio dial live) survives updates but is
        // invisible, and this switch was asked for as configuration. If the file cannot be
        // written at all — a mod manager's read-only overlay — CMMConfigLoader.PersistSetting
        // says so and the toggle still works for the session.
        public static bool CombatMusicMuted = false;
        // TENSION-MUSIC MUTE, persisted the same way and driven by the SAME key. Tension is
        // combat music that has not started yet — the stalking cue the game plays when it
        // thinks a fight is coming — so silencing fights while it keeps running only moves the
        // noise rather than removing it. Alt+Home therefore cycles both: off together, then
        // combat alone, then back on.
        //
        // Kept as its own setting rather than folded into one three-valued one so each line
        // still means exactly one thing, an existing CombatMusicMuted keeps working untouched,
        // and hand-editing THIS alone silences only tension — a combination the key never
        // lands on but which is perfectly coherent to want.
        public static bool TensionMusicMuted = false;
        // Shows/hides the persistent Now Playing overlay (top-left track + context
        // readout). NowPlayingEnabled below picks the state at launch.
        public static KeyCode KeyNowPlaying       = KeyCode.Keypad7;
        // Numpad8 is the COVERAGE panel (off -> everything -> gaps only -> off): which
        // contexts have music and what happens when they do not. It is the one people
        // reach for, so it gets the key the context HUD used to have.
        public static KeyCode KeyCoverageHud     = KeyCode.Keypad8;
        public static KeyCode KeyContextHud      = KeyCode.Keypad9;   // was Keypad8
        public static KeyCode KeyContextHudReset = KeyCode.Keypad6;   // was Keypad9
        // Testing aid: dump the acoustic column from wherever you are standing, without opening
        // the console -- which would mean letting go of the movement keys.
        public static KeyCode KeyAcousticsDump    = KeyCode.Keypad5;
        // Personal radio dial (PersonalRadio): steps through Off → station → station → Off,
        // wrapping both ways — two keys give on/off/next/prev. On the same physical key
        // cluster as Ins/Del/Home/End, same modifier gate. In-game only.
        public static KeyCode KeyRadioNext        = KeyCode.PageUp;
        public static KeyCode KeyRadioPrev        = KeyCode.PageDown;
        public static bool NowPlayingEnabled = false;
        // Per-press change to the current track's manual volume trim (linear factor),
        // persisted per-track in Cache/loudness.xml. Folded into the loudness gain, so
        // it only takes audible effect when LoudnessNormalizationEnabled is true.
        public static float UserTrimStep = 0.1f;
        // How much more often a favourited track is picked in the random track orders
        // (1 = no preference). Sequential order ignores favourites.
        //
        // SCALED TO THE FOLDER, not a raw multiplier — see TrackRotation.ScaledWeight.
        // A plain multiplier meant this setting did something completely different
        // depending on library size (measured: weight 3 gave a favourite a turn every
        // ~45 minutes in a 40-track folder and every ~19 hours in a 1000-track one).
        // Calibrated so weight 3 is roughly once an hour of play at any size, and it
        // never scales DOWN, so small folders behave exactly as they always did.
        public static float FavouriteWeight = 3f;

        // How many recently played tracks a context refuses to repeat, under the
        // RandomNoRepeat order. -1 = the whole pool: every track plays once before any
        // track plays twice. 0 = the pre-1.0.5 behaviour, which despite the mode's name
        // only ever excluded the single track just played. N = remember the last N.
        // Per context, session-only. See TrackRotation.
        public static int TrackNoRepeatMemory = TrackRotation.WholePool;
        // Crossfade used by the MANUAL next/prev/ban skips (seconds). A keypress should
        // switch fast regardless of the context's fadeintime / TrackTransitionFadeDuration,
        // which still govern automatic advances and context changes.
        public static float ManualSkipFadeDuration = 0.25f;

        // Set to false in Config/settings.xml to silence verbose console output.
        // Log.Warning and Log.Error always emit regardless of this flag.
        public static bool DebugLogging = false;
        public static void D(string msg) { if (DebugLogging) Log.Out(msg); }

        // ── Perf instrumentation (see CMMPerf) ─────────────────────────────────
        // Off by default. When on, the context-change hot path is timed per-phase,
        // each transition logs a wall + GC line, and any synchronous span over
        // PerfSpikeMs logs a SPIKE line the instant it happens.
        public static bool  PerfTiming = false;
        public static float PerfSpikeMs = 1.5f;
        // Per-frame stutter watchdog (CMMPerf.FrameTick). The threshold is RELATIVE to
        // the device's own rolling baseline frame time, because on a 30 fps machine an
        // absolute ms cut-off just logs the baseline framerate. A frame is flagged when
        // it exceeds baseline × PerfFrameSpikeFactor AND clears PerfFrameSpikeFloorMs
        // (the floor stops fast machines flagging trivial wobble as a "2× spike").
        // Only consulted when PerfTiming is on.
        public static float PerfFrameSpikeFactor = 2.5f;
        public static float PerfFrameSpikeFloorMs = 50f;
    }
}
