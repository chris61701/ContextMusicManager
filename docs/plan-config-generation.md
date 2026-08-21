# Config files are generated, not shipped

**Decision (2026-08-21).** Future versions ship **no** `Config/*.xml`. CMM writes them at first
run with default values. On upgrade, settings we have added since are written into the existing
file. Chosen upgrade policy for **changed** defaults: **option B — version-stamped migration.**

**Status.** Plan only, no code.

---

## 1. Why

- A mod update currently overwrites `Config/settings.xml`, so every user setting resets
  silently. The Debug build is worse: it does `RemoveDir $(OutputPath)Config` on **every**
  build, which destroys anything `PersistSetting` wrote (the Alt+Home combat-music toggle).
- It removes the only objection to a default-on feature flag being user-disableable — see
  `plan-addon-music-roots.md` §5 Q1, now resolved by this.
- The XML stops being a hand-maintained artifact that can drift from the code. Today a key can
  sit in `settings.xml` with **no reader at all** and silently do nothing.

## 2. Scope

| File | Treatment |
|---|---|
| `Config/settings.xml` | generated + backfilled + migrated |
| `Config/radio.xml` | same |
| `Config/contexts.xml` | **second step.** Feasible — `InitContexts()` already holds all 70 contexts in code and contexts.xml is a written-out copy. Doing it also fixes the `CmmSession` harness drift, where the harness's hand-ported context table falls behind. Not in the first pass. |
| `Config/context.lua` | unchanged — already embedded in the DLL with a disk copy as override |

## 3. The settings table

The blocker for *writing* the file is that a setting lives in three disconnected places: the
field in `CMMConfig.cs`, the `ReadBool/ReadFloat/...` line in `CMMConfigLoader.cs`, and the
element + comment in the XML. A writer needs all of it in one place.

New `Harmony/CMMSettingsSchema.cs` — one descriptor per setting:

```csharp
internal sealed class SettingDef
{
    public string Name;
    public SettingFile File;        // Settings | Radio
    public string Doc;              // the XML comment, verbatim
    public object Default;          // current default
    public int    AddedIn;          // schema number this setting first appeared in
    public (int Schema, object Was)[] DefaultHistory;   // optional, oldest→newest
    public Func<string> Read;       // current in-memory value, for writing out
    public Action<string> Write;    // parse + assign to the CMMConfig field
}
```

~113 settings (49 in settings.xml, 64 in radio.xml). Mechanical. The doc comments move out of
the XML and into this table, where they stop being a second copy of the ones already in
`CMMConfig.cs`.

## 4. The upgrade algorithm

One new function, `CMMConfigLoader.EnsureSettingsFiles(modPath)`, called **immediately before**
`LoadSettings` in `Init` (`ContextMusicManager.cs:117`). `LoadSettings` itself is untouched and
keeps its current first-file-wins chain semantics — all the new risk is in one function that
runs once.

```
fileSchema = root.@schema  (absent ⇒ 0, i.e. a file from a version that still shipped one)

for each setting S in the schema:
    if the file has no element for S:
        append <S.Name>S.Default</S.Name> with S.Doc            # backfill
    else if S changed its default after fileSchema:
        wasDefault = the default in force at fileSchema         # walk DefaultHistory
        if parse(fileValue) == wasDefault:  set to S.Default    # never customised → take it
        else:                               leave alone         # customised → theirs wins

root.@schema = CURRENT_SCHEMA
```

**Why B and not "always backfill everything".** Once a user has a file, a changed default can
never reach them again — their value is pinned forever. That is not hypothetical: the POI stale
60→120 change would have reached nobody. B lets a default change through **only** for users who
never touched that setting. The information needed to tell those two groups apart is destroyed
the moment we start backfilling, so the stamp has to exist from the first release, not later.

Details that matter:

- **Compare parsed values, not raw text.** `5` vs `5.0`, `true` vs `True`, trailing whitespace.
  A text compare would classify an untouched setting as customised and strand it.
- **A user who deliberately set the old default gets migrated.** Unavoidable — "equals the old
  default" is the only customisation signal available. Benign.
- **`schema` absent = 0** covers everyone updating from a version that shipped the file: all
  keys present, no stamp, every recorded default change applies. Correct behaviour.
- **New keys append at the bottom under a `<!-- New in <version> -->` banner.** Visible, and
  nothing existing moves.
- **Keys we have removed stay in the file.** Inert and harmless; pruning risks eating something.
- **Migration runs at startup only, never on hot-reload.** Rewriting the file under someone who
  is actively editing it is hostile. Hot-reload keeps reading and nothing else.

### 4.1 Guardrail: an unrecorded default change is silent

If someone edits a default in the schema and forgets to bump `CURRENT_SCHEMA` and add a
`DefaultHistory` entry, the migration silently does not happen and the bug is invisible until a
user reports it. Guard: a harness test that hashes every `(Name, Default)` pair and compares
against a committed expected hash per schema number. Changing a default without bumping the
schema fails the test.

## 5. Things that break if not handled

1. **The Debug deploy's four dev pokes.** `RemoveDir $(OutputPath)Config` + copy + four
   `XmlPoke`s (`MusicPath=J:\7D2D Custom Music`, `DebugLogging`, `PerfTiming`,
   `KeybindRequireCtrl`) at `.csproj:388-418`. The wipe must go — it would delete the generated
   file every build. Replacement: a Debug-only **`Config/settings.dev.xml`** inserted at the
   **front** of the existing read chain (`CMMConfigLoader.cs:44-53`, first file with a tag
   wins). Never written to, never generated, never shipped in Release. Drops straight into the
   architecture that is already there.
2. **`Content Include="Config\**\*"`** (`.csproj:131`) is what puts Config into the zip. Must
   stop including the two settings files.
3. **`ScaffoldMusicFolders`** (`.csproj:368`) reads `Config\contexts.xml` at build time to
   create the music folder tree. That reads the **repo** copy, which still exists — fine now,
   but it is the reason contexts.xml generation is a separate step.
4. **Writing can fail** — read-only install (Program Files), AV lock, mod manager overlay.
   Must degrade to in-memory defaults, log **once**, and never retry-spam. Write atomically
   (`.tmp` + `File.Move`), the way `LoudnessStore` already does.
5. **Our own write trips the config watcher.** `NoteConfigWritten()` exists for this but stamps
   **only** `settings.xml` (`ConfigWatch.cs:276-279`) — it needs `radio.xml` too. Startup
   generation happens before `ConfigWatchInit()` snapshots timestamps, so first-run generation
   is free; the stamp matters for `PersistSetting` and any later write.
6. **Dedicated servers never reach `Init`** (`HarmonyPatches.cs:25`), so no files are generated
   there. Intended, but worth knowing before someone reports "no settings.xml on my server".

## 6. Order of work

1. The schema table + move the docs into it; loader drives off the table. **No behaviour
   change** — the shipped XML still wins, nothing is written yet.
2. `EnsureSettingsFiles`: create-if-absent, backfill, stamp. Still shipping the XML, so this is
   a no-op on a fresh install and provably safe.
3. Default migration + the hash guardrail test.
4. Stop shipping the two XML files; `settings.dev.xml` for the Debug pokes; drop the
   `RemoveDir`.
5. `contexts.xml`, later, as its own piece of work.
