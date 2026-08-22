# Radio tuning: the spec

Settled with Chris 2026-08-22. This is what gets built. Background on why the shaping looks like
this is in `plan-fm-dial-consult.md`; the continuous FM dial described there is **not** being built.

## What this is not

Not a dial. There is no band, no frequencies, no capture, no two stations decoding at once. Stations
stay a discrete list and the keys stay a discrete seek. None of the ten structural items the
continuous dial would have needed apply.

## Input

- **Press only.** Next / previous. No hold, no acceleration, no repeat.
- **No power button.** Off is a position in the ring: roll far enough and you reach it, keep going
  and you come out the other side into stations again. Circular. This is what the code already does.

## The sequence

Every station-to-station change runs **leave → search → arrive**.

| phase | signal | what happens |
|---|---|---|
| **leave** | 1 → 0 | the old station fades, loses stereo and goes dull as it goes |
| **search** | 0 | static, for `max(minimum delay, actual load time)` |
| **arrive** | 0 → 1 | the new station comes in dull and mono under the static, then opens up |

**One `signal` value, 0..1, drives everything**: station gain, stereo width, dullness (a one-pole
low-pass), and — inverted — the static's level. They can never disagree with each other, and there
is one curve to tune rather than four.

## The minimum delay

- Configurable: `RadioTuneMinStaticMs`.
- A **floor, not a duration.** A slow load extends it; an instant load still pays it.
- The point: tuning always *sounds* like tuning, even when the next station happens to be ready.

## Off

- **Onto Off: silent immediately.** No leave ramp, no static. Any load in flight is dropped.
  (Implementation note: "immediately" is the shortest fade that does not click, not a hard cut.)
- **Off Off, onto a station: static, then arrive.** There is nothing to leave, so no leave phase.

## Pressing again mid-sequence

- Allowed at any point, in any phase.
- **The new leave starts from wherever `signal` is right now.** Half tuned in means it fades from
  half — never a jump back to full and then a fade, which would be an audible swell in the wrong
  direction.
- **Constant rate, not fixed duration.** Leaving from half takes half as long. A fixed duration
  makes an early interrupt sound sluggish and a late one abrupt.
- **The static bed is never re-triggered.** It is already rolling; it keeps rolling. Re-arming it
  would put an attack transient on every press.
- **The minimum delay restarts on each press.** You land one delay after your *last* press, so
  hammering the key gives continuous static until you stop. That is the wanted behaviour.
- The in-flight load is abandoned.

## When a station fails

- Keep seeking **in the direction you were already going** until one works.
- The static rolls continuously across the whole search — not restarted per station.
- The minimum delay does **not** restart on these auto-advances, only on the player's presses.
  Otherwise three dead stations in a row would cost three delays.
- If every station fails, land on **Off**.
- **"Fails" means an error** — missing folder, unreadable file, empty station. Those are fast and
  definite, so move on at once. A merely *slow* load is not a failure and is waited for. A long
  backstop (`RadioTuneGiveUpMs`, ~10 s) exists only so a wedged load cannot strand you in static.

## Switching back to the one you just left

No special case. The broadcast clock has moved on, so it is a fresh seek to wherever that station is
*now* — the same cost as any other station.

## The detent click

Unchanged. One per press. A real dial spun fast gives a rapid run of clicks, and each fire already
draws its own variation, so a run is related-but-different clunks rather than one sample repeated.
Considered rate-limiting it and decided against.

## Settings

| name | default | what it does |
|---|---|---|
| `RadioTuneMinStaticMs` | 900 | the floor on how long the static rolls |
| `RadioTuneLeaveMs` | 260 | time to go from full signal to none, at full scale |
| `RadioTuneArriveMs` | 420 | time to come from none to full, at full scale |
| `RadioTuneDullness` | 0.85 | how far the treble drops as signal goes (0 = no dulling) |
| `RadioTuneMononess` | 1.0 | how completely stereo collapses as signal goes |
| `RadioTuneGiveUpMs` | 10000 | backstop before a wedged load is abandoned |

Leave and arrive are quoted at full scale and applied as a **rate**, so a partial move takes
proportionally less time.

## Open, and deliberately deferred

The tuning static's own character is a separate question and is being settled by ear against
rendered examples. Measured 2026-08-22: the shipped `RadioStaticCrackle 0.8` is not crackle, it is a
**15 dB continuous amplitude tremolo** with 3–30 ms holds (33–333 Hz), which is fast enough to read
as graininess rather than as discrete events. Real demodulator clicks are sparse bipolar impulses
that get denser as the signal degrades. Whether to replace the mechanism is Chris's call once he has
heard both.
