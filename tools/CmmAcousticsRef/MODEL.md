# The acoustic model — what each term IS

**This file exists because its absence cost most of a working day.**

`README.md` records what the author wants it to *sound* like. That is acceptance criteria, and
it is necessary — but it is not a model. Without a model, a defect looks like "this route is
rough", and the reflex is to reach for a parameter or an operator. With a model, the same defect
reads as "term X is implemented wrong", and the fix is derivable rather than searched for.

Concretely: four different reduction operators were tried on the transmission estimate — max,
mean, energy sum, gated variants of each — without once asking what physical quantity that
estimate *is*. The author answered it in one sentence ("it should be pouring through the glass,
radiated outward") and the correct form followed immediately. The information was available; it
simply was not written down anywhere it could be consulted.

**Rule: no change to a formula without stating, here, what quantity it computes.** If the
quantity cannot be named, the change is tuning and not modelling, and it should be rejected on
that basis alone.

---

## The one governing principle

> **Whatever transmits, diffracts or reflects becomes a SOURCE at its own location, radiating
> into the space the listener is in. It is never a modifier applied along the listener's line of
> sight.**

Every term below is an instance of it. Every mistake worth recording was a violation of it: the
transmission disc placed at the listener instead of at the barrier; a doorway treated as a
modifier on a ray rather than as a radiator at the gap; occlusion expressed as a displacement
along the distance axis rather than as a loss in its own right.

The wrong question is *what is directly between me and the speaker*. The right one is *what is
radiating into the space I am in, and from where*.

---

## What the listener receives

Total received power is a **sum over distinct arrivals**. Never a minimum, never a maximum over
candidates.

    P_total = Σ over arrivals a of  P_a

Two arrivals are **distinct** if they are physically different paths. Two candidates describing
the same path must not both be counted — that is a double-count, and it has appeared in three
separate guises (air-vs-solid in open space, several cell faces of one opening, an aperture
sitting on the line of sight). The test for "same path" is geometric: if a candidate's route
length exceeds the straight-line distance by less than a wavelength or so, it *is* the direct
path. This is the Fresnel criterion, and it is why a wide-open doorway directly ahead adds
nothing on top of the direct sound.

Each arrival carries five properties, and all five matter:

| property | why it is not optional |
|---|---|
| **level** | the obvious one |
| **position** | where it radiates FROM — the aperture or barrier patch, not the source |
| **spectrum** | a wall is a filter, not a fader; this is the ear's primary distance/occlusion cue |
| **delay** | path length / speed of sound. Without it there is no echo, only a wash |
| **weight** | its share, so a path counted once is not counted twice |

Discarding any one of them has produced an audible defect. Delay is still not implemented, and
its absence is the most likely reason caves have never sounded right.

---

## Term 1 — distance

    L_distance = attenuation over the travelled path length

Must be **smooth and monotonic in position**, and the *unit of occlusion must not be distance*.
Expressing a wall as "extra virtual blocks" and feeding it through a curve that terminates at a
finite range makes the cost of that wall depend on where the listener stands — measured at
34 dB one block from a speaker and infinite twenty blocks away. A wall's transmission loss is a
fixed number of decibels. It has to be represented as one.

## Term 2 — transmission through a barrier

    P_transmitted = ∫ over the barrier surface of  (incident power × patch transmission)
                    each patch radiating from ITS OWN position

A barrier is a **surface of secondary sources**, not an attenuator on a ray. Glass beside a steel
column transmits and then radiates; standing behind the column you hear the glass, a little
further away and no more blocked. So the integral is evaluated **at the barrier**, over a disc
roughly the size of the first Fresnel zone, weighted by **area share** and summed in energy.

Not `max` (steps when the winning ray crosses a feature edge). Not `mean` (dividing by sample
count misreports a barrier whose patches differ). Not a plain sum over overlapping rays
(over-counts). An area integral has none of those failure modes, and adding samples refines it
rather than shifting it.

Per-voxel loss is **superlinear in material density** (mass law), applied per voxel and then
summed along the path as a **line integral** weighted by the ray's length inside each voxel.
Counting whole voxels made a one-voxel wall cost 6 or 12 dB depending on the ray's angle.

**Vertical is not horizontal.** A voxel used as a floor stands for a whole floor assembly —
deck, joists, ceiling below — and must cost more than the same material crossed horizontally.
Floors are also the exception to the thin-shape discount: a plate used as a floor is a complete
barrier vertically despite being thin.

## Term 3 — diffraction through apertures

    P_aperture = P_arriving_at_the_aperture × constriction × spreading from the aperture

An aperture is an **aperture radiator**: finite size, so the level is roughly constant across its
face near it rather than obeying an inverse law from a point. It radiates into the receiving
space from its own position, which is why a gap reads as a localised source *at* the gap and why
the image sweeps as you walk past a doorway.

Constriction is a **bottleneck**, not a toll: power through a chain of openings is limited by the
narrowest one, paid once. A corridor of constant width does not keep charging.

Openness is **continuous**, and aperture area is its integral. A door 80% open is 80% of an
opening; treating openness as a flag made the graph's topology binary in a quantity that is not,
and the air path snapped into existence at the end of every swing.

Energy is also lost **diffracting** round an edge — but only where there is an edge to diffract
round. Turning in open space is free, because a source radiates every way at once.

## Term 4 — the diffuse field  *(NOT IMPLEMENTED)*

    P_diffuse ≈ 4 × P_entering_the_space / R,     R = Sα/(1−α)

In an enclosed space, sound bounces off every surface and builds a field that is **essentially
uniform** — it does not vary with listener position. Beyond the critical distance it *dominates*
the direct component. For the test geometry that distance is about 2.6 m, and at the 5 m one
would walk past a doorway the diffuse field sits roughly 5 dB ABOVE the direct-from-doorway
component.

**Consequence, and it is a large one:** the position-dependent part is a small perturbation
riding on a much larger constant, so an indoor trajectory should be nearly flat. Rendering dry
arrivals with nothing underneath them exposes every dB of their variation. Harsh walking-past
behaviour is therefore *not* a smoothness defect in the dry path — it is a missing term.

Corollary for method: **measuring dry-path smoothness in isolation measures something the
listener never hears alone.** Effort spent flattening dry gradients may be flattening what
physics would have flattened for free.

## Term 5 — time structure  *(NOT IMPLEMENTED)*

    delay_a = path_length_a / c

A cave echo is the same sound arriving **repeatedly, separated in time**. One shared reverb whose
only time parameter is a decay cannot produce that at any setting — an exponential tail is a wash
by construction. At roughly a block per metre, a 60-block cave route lands ~175 ms behind the
direct sound, which is a plainly audible discrete echo.

Every arrival already carries a route length, so it already has an arrival time. This is an
omission, not a limitation.

---

## Structure: how the paths are found

Space is segmented **bottom-up** into regions of connected open air, merged outward while the air
stays wide and split where it pinches. Boundaries then land on the geometry rather than on a grid,
and **a boundary IS an aperture** — found, not detected by a heuristic.

Top-down subdivision keeps the grid, and a grid puts arbitrary boundaries through real features.
Nearly every stubborn defect of the cell-grid era traced to that: a wall inside a cell, a throat
measured relative to a cell, discontinuities at cell boundaries.

Requirements on the structure:

- **Static.** It must not depend on listener position, or it has to be rebuilt as the listener
  moves — which is the defect the shipped ear-anchored flood has.
- **Source-independent.** Moving speakers must be a lookup, not a rebuild.
- **A fixed candidate set.** A set that changes as the listener moves puts a step in the field.
  Prune by *contribution* (bounded error), never by topology or search radius.

---

## Method rules, each earned the hard way

1. **State the quantity before changing the formula.** Four operators were tried on the
   transmission estimate without asking what it computes.
2. **When two measurements disagree, resolve that before acting on either.** A p99 said the field
   was excellent while the render lurched audibly; both numbers sat unreconciled for hours.
3. **Never offer a cause not traced to a specific value changing at a specific input.** "That is
   the harness, not the model" was asserted three times; the fourth time it was a real bug.
4. **Validate an instrument against a known-bad case before trusting it.** Three dead ends were
   the instrument lying: a percentile that could not see the defect, a 4x oversampling inflation,
   and a renderer discarding arrival weights.
5. **Smoothness belongs to the field, not to a time constant.** Slewing makes the output
   path-dependent: stand still and you hold a value that depends on how you arrived. A fast walk
   past a door *should* change fast. Cliffs in the field are the defect.
