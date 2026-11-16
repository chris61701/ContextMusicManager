Your own radio click sounds (optional)

The radio's clicks are synthesized, and no recordings ship with the mod. To replace them
with recordings of your own, put WAV files here with exactly these names:

  radio_dial.wav    one notch of the tuning dial
  radio_power.wav   the power switch (used for both on and off)

Mono or stereo, 8/16/24/32-bit PCM or float. They are read once when the game starts, so
restart after adding or changing them. RadioClickSamples in settings.xml turns them on and
off (on by default); a missing or unreadable file falls back to the synthesized click.

In game, `cmm sfx` shows what loaded and `cmm sfx test spin` fires eight dial clicks in a row.
