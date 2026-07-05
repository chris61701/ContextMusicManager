# What's New in Context Music Manager 1.0.4

1.0.3a gave you editable Lua context logic, loudness normalization, and playback
keybinds. 1.0.4 rebuilds the foundation underneath them: CMM stops borrowing the game's
audio system and plays through its own. On top of that come one download for every game
version, smarter multiplayer combat, and a round of reliability fixes. Here's what
actually changes for you.

---

## Its own audio engine

The headline change: **your custom music now plays through CMM's own audio engine**,
not the game's Unity `AudioSource` system. Tracks are decoded, mixed, and pushed to
your sound device on threads the mod owns.

Why you'll notice:

- **The music no longer stutters when the game is busy.** World generation, loading
  screens, or another program briefly hogging the CPU used to make audio hitch. Now
  the music keeps flowing smoothly through all of it — the output buffer even deepens
  automatically during loads so nothing can interrupt a track, and stays tight and
  responsive on the menu and in normal play.
- **Menu ↔ game transitions fade gracefully.** The menu → game handoff stays continuous
  even when a world loads unusually fast, and one-shot stingers now honor their
  configured fade-in time.
- Everything from 1.0.3a carries over unchanged — crossfades, mid-track resume,
  next/previous skipping, loudness normalization, and the per-track volume trim all work
  exactly as before. The engine shares the game's entry in the Windows volume mixer, so
  your system music slider still controls it.

Because CMM no longer routes through the game's own audio, the default mod music volume
was set to 0.5 to roughly match the in-game music level you're used to.

## One download for every game version

CMM now ships as a **single build that runs on Alpha 20.7, Alpha 21.2, 2.6, and 3.0**.
No more picking the right file for your game — one binary bridges the API differences
between versions at runtime, right down to the `cmm` console command. **Blood-moon
detection is also fixed on A20/A21**, so blood-moon combat music fires when it should.

## Smarter multiplayer combat

**A squad now shares one combat state.** When an enemy is fighting a nearby teammate,
you drop into combat music too — even if the zombies never touch you. Two `settings.xml`
knobs tune it: `CombatShareRange` (how close the ally must be for their fight to count as
yours) and `CombatShareRequireAlly` (limit sharing to party members/friends, or set it
`false` to share with any nearby player). No effect in single-player.

## Seamless POI clears

When the last sleeper in a point of interest dies, the cleared-POI theme now picks up
right where the infested one left off — no ambient music leaking through the gap while
the "cleared" fanfare plays. The fanfare also reliably wins over any lingering combat
music, ducking the underlying track so the one-shot stinger comes through cleanly.

---

## A few finer points

- **No more getting stranded in silence.** A burst of rapidly-changing situations could
  occasionally leave the music "playing" with nothing audible; a convergence check now
  catches that and re-starts the correct track.
- **No click when a new context interrupts a crossfade.** A half-finished crossfade that
  gets repurposed is now ramped to silence first, so the incoming track always fades in
  cleanly.
- **Moving tracks mid-session is reliable on any filesystem** — including exFAT/FAT/SD
  cards, network shares, and junctions that don't report folder changes the usual way.
  A genuinely deleted track no longer gets re-picked on a loop.
- **Vorbis streaming parity and race-free FLAC reads** mean `.ogg` and `.flac` tracks of
  any size decode and normalize correctly, even when several open at once.

Drop your tracks in, tune `settings.xml` to taste, and enjoy — happy surviving.
