# How the radio system actually works, and why it sounded wrong

2026-08-23. Written after shipping two broken builds by patching without understanding the whole.
Four read-only investigations: the native mixer, the managed state machine, bench-vs-game
divergence, and an adversarial audit of the last four commits. This is the map plus the findings.

---

## THE HEADLINE: the game has never once run the approved numbers

`Config/radio.xml`, in **all four** deployed installs, lines 447-449:

| setting | approved by ear | **actually deployed** | error |
|---|---|---|---|
| `RadioRecvNoise` | 0.55 | **1.6** | **+9.3 dB** |
| `RadioRecvClicks` | 0.18 | **0.7** | **+11.8 dB** |
| `RadioRecvClickRate` | 180/s | **900/s** | **×5** |

These are hand-edited schema-8-era values. The migration in `CMMSettingsFile.cs:110` only rewrites
a key that still holds a *former default* — `1.6` was never a default, so it reads as a deliberate
user override and is skipped. **It will be skipped forever.** No schema bump reaches it.

What those numbers do:

- `nAmp` is hard-clamped at 1.6 (`cmm_mix.c:682`). At trim 0.55 the clamp essentially never fires.
  At 1.6 it fires for the whole of `u < 0.30` — **the entire deep search sits pinned at the
  clamp**, which flattens wander and flutter completely. The "AGC breathing" the design depends on
  is gone and the bottom of the search is a motionless level-pinned roar. That is precisely the
  "sound effect, not a receiver" failure the tuning exists to avoid.
- Simulated through the real LCG and de-emphasis pair at u=0: bench noise RMS **−11.44 dBFS**,
  peak 1.12. Deployed: **−6.17 dBFS**, peak **2.06** — i.e. it hard-clips for any
  `master × level > 0.487`. The approved roar *cannot* clip; the deployed one does.
- Clicks at 900/s have a mean spacing of **1.11 ms** against an 8.3 ms decay, and `g_recvClk` is
  overwritten rather than accumulated (`cmm_mix.c:711`). So the click generator degenerates into a
  **~900 Hz random-amplitude buzz**. At 180/s you hear discrete ticks. It is a different sound, not
  a louder one.

**Every listening judgement made in game has been against these numbers.** The README, `CMMConfig.cs`
and the bench all agree with each other; only the file that actually wins disagrees.

---

## The other four things that make the game unlike the bench

### 1. The leave is truncated to 10 ms

`PersonalRadio.cs:566` sets `_nextStartFade = 0.01f`. `cmm_mix.c:1438-1453` fades **every**
non-emitter voice to zero over that fade — including the station being left. The new voice lands
~100-200 ms after the press (one frame + open/seek + one frame). At press+150 ms the signal ramp is
still at `u ≈ 0.875`, where `nAmp = 0.0016` → the hiss is at **−66 dBFS, inaudible**.

So what actually happens on a dial press is: click → **the music hard-cuts in 10 ms** → *then* the
static rises over the next second.

That is "the music fades out and the static fades out, like they are two different things", and it
is structural — it is unreachable in the bench, where station A plays under the noise for 1.95 s.
It is also *intermittent*: an unmeasured track's loudness gate delays the add by up to 6 s, which
lets the whole leave play properly.

### 2. The search is 0.3 s, not 1.5 s

`PersonalRadio.cs:573` anchors `_searchUntil` at **the press**, at the same instant as the leave is
aimed. The leave takes 1.2 s of that 1.5 s floor. The dead-zero window is **0.30-0.55 s** after tick
quantisation, and the whole event is **4.3 s, not 5.5 s**. The comment at `CMMConfig.cs:194`
("1.2 out, 1.5 of static, 2.8 back in") describes the bench, not the code.

### 3. The loudness gain is applied to the programme twice — my fix, this session

`g_recvLevel` multiplies **the sum** (`cmm_mix.c:755`), and it now carries `_gainApplied`, which is
the same number already inside `v->trackGain` (`cmm_mix.c:781`, `:902`). So:

```
programme at bus = P · G · G       noise at bus = N · G
```

Over this machine's 68-track loudness cache the median gain is **−5.08 dB** (range −11.55 … +0.83).
So the radio's programme sits a median 5 dB below the same track played as context music, and the
noise-to-programme ratio — the thing that was tuned by ear — is a median **5 dB hot, up to 11.5**.

I wrote that fix to make the noise follow the track gain, which is the right intent. The code
applies it to programme+noise instead of to the noise. Same class of error as the two before it.

This is a **v48 regression**: at ABI 47 the noise lived inside the voice and `(programme + noise)`
was multiplied by `g` once, so the ratio was preserved.

### 4. A blip of clean, full-volume music inside the static

`cmm_mix_add` runs on the worker thread; `SetVoiceRadio(id, true)` runs on the main thread one frame
later (`PersonalRadio.cs:969`). Between them `recvOn == 0`, so the voice routes straight to `g_bus`
(`cmm_mix.c:905`) — **full bandwidth, no level law, no mangle, nothing over it**. The producer
renders ahead ~200 ms in 2048-frame blocks, so up to ~200 ms of the incoming song can appear at full
volume in the middle of the search and then vanish again.

---

## What I got wrong about my own fix

I moved the receiver arm to the top of `Tick()` and wrote a comment saying the old placement "sat
below seven early returns, so during ordinary playback it effectively never ran."

**The second half is false.** Every one of those seven returns is an abnormal state (`!_on`, no
player, dead, blocking stinger, no stations, off-air, backstop). During ordinary playback —
radio on, alive, station present, voice live — execution reached the old site and armed at 4 Hz
exactly as intended.

What the old placement actually failed to cover was the *abnormal* states. And what disarmed the
receiver during normal play was **`POI_Cleared` falling through with `active = false`** — a ducking
stinger that deliberately keeps the voice alive.

Moving it up is still right, but it did not fix what I claimed, and it **introduced** a new bug: the
arm now asserts `active = true` above the `player == null` return, so quitting to the menu mid-dial
leaves full-scale hiss playing over the menu music.

---

## The map

### Voice level

One struct serves music, radio programme and world emitters, discriminated by `isEmitter` and
`recvOn`. Output level is **two independent ramps multiplied** — `cmm_mix.c:781`:

```c
float g = v->gain * v->trackGain;      // crossfade/duck  ×  loudness
```

There is no third multiplier. The receiver's `signal` does **not** attenuate a voice — that was the
finding behind the v48 refactor, and it is why two stations at signal 0 both emitted full-scale
noise. Radio level comes from `progG` (the ≤5 dB soft mute) and `g_recvLevel`, not from `g`.

`gainStep == 0` is ambiguous: it means both "arrived" and "never started". Three call sites
special-case `seconds <= 0` into a snap; `cmm_mix_stop` and `cmm_mix_reclaim` do not, so
`cmm_mix_stop(0, …)` freezes every voice at its current gain instead of stopping it.

### Routing

```
decoder → g_voiceStereo ─┬─ emit ────────────→ g_emitterMono[slot] ─→ emitter master ─→ rings
                         ├─ recvOn ──────────→ g_radioBus ─┐
                         └─ else ────────────→ g_bus ←─────┤
                                                            │
                              render_receiver_add ──────────┘
                                ├ unarmed: g_bus += g_radioBus        (raw, no level)
                                └ armed:   + noise, hi-cut, width,
                                           g_bus += l × g_recvLevel
                          render_sfx_add → g_bus
                          → master slew (25 ms) → hard clip → device
```

**Unconditional routes:** voice→`g_voiceStereo`; the three-way output branch (exhaustive);
`g_radioBus`→`g_bus` (both branches, since the fix); `g_bus`→device.

**Gated:** the noise/hi-cut/width/level on `g_recvActive`; the level law and mangle on `v->recvOn`
— note those two gates are *different*, so an unarmed radio voice is still mangled and mute-lawed
but gets no level. That is a `1/g_recvLevel` step (median 5 dB) on every arm/disarm edge.

### `Tick()` reachability — where an instruction may be placed

| region | runs when |
|---|---|
| above `:609` | every frame, radio on or off. Only `SpinTick` and `CommitPendingStart`. |
| `:610-626` | **4 Hz, radio on or off.** The arm correctly lives here. |
| `:631-664` | 4 Hz, on, before the player/dead/stinger filters |
| `:670-781` | 4 Hz, on, in-game, alive, station present. **The only region where both "voice exists" and "no voice" are reachable.** ARRIVE / RESCUE / backstop belong here. |
| `:783-817` | **only when `_voiceId != 0`** |
| `:822` | **only when `_voiceId == 0`** |

The return at `:817` is the one that has bitten twice. Anything after it is unreachable during
normal playback.

### The receiver, after v48

Per-voice: `recvOn`, the mangle's `recvMlp[4]`/`recvMhp[4]`, and `recvCEnv`. Everything else —
signal ramp, RNG, de-emphasis poles, wander, flutter, clicks, hi-cut state, stereo latch — is
mixer-global. `receiver_advance()` advances the one ramp per block and publishes `g_recvU[f]`, so
every voice and the bus stage read the same value for the same frame.

---

## TESTED IN GAME 2026-08-23 — three of these were NOT REAL

Chris reproduced the list below rather than taking it on trust. Three did not happen, and the
reason they were all predicted is one wrong assumption shared between them.

**The pause menu keeps a player.** The whole theory rested on `Tick`'s `player == null` return
firing when the game is paused, stranding `_staticWanted` / `_searchGiveUp` in a state nothing
below that return can clear. It does not fire — the tick keeps running, the station lands, the
timers are cleared normally.

| predicted | reality |
|---|---|
| the radio dials itself after a menu trip | **does not happen.** *"A radio never fails to load, even if I go to the pause menu."* The give-up timer never gets the chance. |
| static over the menu | **does not happen.** *"the station always resolves, even while paused."* |
| a stinger mid-tune destroys the transition | **does not happen.** *"Triggering death during a station change keeps the static playing until your spawn and then it resolves the station."* The arm-edge signal snap is real in the code but nothing reaches it — the transition completes correctly. |
| POI_Cleared kills the radio instead of ducking | **happens, and is preferred.** *"it's how I'd prefer it."* The `TEMP` marker is now a recorded decision. |
| the ABI guard is stale and dead | **confirmed by inspection.** 46 against a native 49, and `Available` forwarded by seven wrappers and consumed by none. Fixed. |

The lesson is about the method, not the code. Four agents reading the same file all reasoned
from `player == null` firing on pause, because that is what the code *looks* like it does. None
of them could run the game. **A code-path argument is a hypothesis, and the cheap ones should be
tested before they are ranked as defects** — three of these were written up with severities and
repro steps for something that never occurs.

## Everything still broken, ranked

| # | what | where |
|---|---|---|
| 1 | **`radio.xml` overrides three settled values** and no migration can reach them | `radio.xml:447-449` |
| 2 | **Loudness gain applied twice** — level must scale `nz`, not `l` | `cmm_mix.c:755` |
| 3 | **The leave hard-cuts in 10 ms** while the static is still inaudible | `PersonalRadio.cs:566` + `cmm_mix.c:1438` |
| 4 | **Search floor anchored at the press**, so it is 0.3 s not 1.5 s | `PersonalRadio.cs:573` |
| 5 | **ABI guard stale (46 vs 48) AND dead** — nothing reads `NativeOpus.Available` | `NativeAudio.cs:25` |
| 6 | **Arming snaps the signal to 1.0**, so a stinger tick mid-tune destroys the transition | `cmm_mix.c:2346` |
| 7 | **Menu/off-air/death leave the receiver armed** — full-scale roar over menu music | `PersonalRadio.cs:626` |
| 8 | `g_recvLevel` is not slewed — a click at every track change | `cmm_mix.c:2352` |
| 9 | Turning on from Off plays a phantom 1.2 s leave | `cmm_mix.c:2363` |
| 10 | ~200 ms of clean full-volume music inside the static | `PersonalRadio.cs:969` |
| 11 | `rising` is a whole-block value → ~35 dB step in the hiss at lock | `cmm_mix.c:642` |
| 12 | `_gainApplied` never reset — the search rides a dead track's gain | `PersonalRadio.cs:997` |
| 13 | `_searchGiveUp` outlives menu/death → **the radio dials itself to another station** | `PersonalRadio.cs:574` |
| 14 | Dead settings still documented as live: `RadioTuneGainPow`, `RadioStatic*` | `CMMSettingsSchema.cs` |
| 15 | `cmm_mix_add` has no bounds check on `g_voices` | `cmm_mix.c:1459` |
| 16 | `POI_Cleared` passes `0f`, which is a permanent kill, not a duck | `AudioPlayer.cs:525` |

Items 1-4 are almost certainly the whole of what was heard. 5 is why none of this failed loudly.

---

## The rule this cost us

**A checksum proves two renders differ. It does not prove the thing you varied is what differed,
and it says nothing at all about what the game is running.** Three listening rounds were spent on
byte-identical files; every in-game round was spent on config the bench never used. Before trusting
any in-game listening judgement again, dump the *live* values out of the running mixer and diff them
against the bench's argument line.
