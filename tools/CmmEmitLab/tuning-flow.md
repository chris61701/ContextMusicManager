# Tuning flow — the personal radio, and how the emitter should differ

The personal radio's steps are written out below, one per block, in the order they happen.
Under each is a blank `EMITTER:` line. Fill those in — "same", "not this", or whatever it
should do instead. Anything you leave as `same` I'll take literally.

Timings in brackets are the current shipped values.

---

## 1. You press the dial

**RADIO:** plays a click immediately, so the input is confirmed before anything else happens.

**EMITTER:**

---

## 2. The station you're leaving

**RADIO:** keeps playing. It is not stopped and not faded — the receiver *buries* it as the
signal falls. It is retired later, by the arrival of the next station, not by the press.

**EMITTER:**

---

## 3. The static

**RADIO:** rises as the signal falls, over 700 ms [`RadioTuneLeaveMs`]. It is a rate, not a
duration — press again half way through and it reverses from where it is.

**EMITTER:**

---

## 4. Opening the new station's file

**RADIO:** starts **immediately**, on the next frame, while the old station is still playing
and the static is still rising. The disk work is hidden underneath the leave.

**EMITTER:**

---

## 5. The swap

**RADIO:** happens half way through the static floor [700 ms + 50 ms]. The new station is
already open by then, so the swap costs nothing. It is added with a 0.25 s crossfade
[`SwapFade`] spent entirely under the static, and that add is what retires the old station.

**EMITTER:**

---

## 6. The floor

**RADIO:** the static is guaranteed to run at least 100 ms [`RadioTuneMinStaticMs`] measured
*after* the leave finishes, not from the press.

**EMITTER:**

---

## 7. Arriving

**RADIO:** once the floor has passed **and** the new voice exists, the signal rises over
1000 ms [`RadioTuneArriveMs`]. The static is not switched off in front of the station — it
fades out from under it while the station rises through it.

**EMITTER:**

---

## 8. If the new station will not open

**RADIO:** keeps the static running for 10 s [`RadioTuneGiveUpMs`], then automatically moves
on to the next station in the direction you were dialling.

**EMITTER:**

---

## 9. If something goes wrong and nothing arrives

**RADIO:** an unconditional rescue — if a voice exists and nothing is bringing it up, bring it
up. Deliberately not chained behind any other condition.

**EMITTER:**

---

## 10. You press again mid-tune

**RADIO:** no special case at all. The leave restarts from wherever the signal currently is,
and the in-flight open is abandoned.

**EMITTER:**

---

## 11. What is never true

**RADIO:** there is never a moment with no programme playing. Something is always there, even
if buried.

**EMITTER:**

---

## Anything the emitter needs that the radio has no equivalent for

The radio is one sound in your head. The emitter is a box in a room, so some things simply do
not exist on the radio side. Write them here.

- Occlusion / walls / doors:
- Distance:
- Several speakers on one station:
- Turning a speaker off, and back on:
- A station with no playable tracks (the off-air tone):
- Anything else:
