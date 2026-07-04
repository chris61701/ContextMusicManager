# CMM Playlist — In-Game Live-Reaction Checklist

Validates the live ConfigWatch/FileWatch coroutine reaction that the offline harness
(`CmmPlaylistTest`, 39/39) can't cover: hot-reloading playlists, folder+playlist union,
`subfolders` toggling, and changing a context's `source` on the fly.

The **Menu** context is the probe — it plays on the main menu, so **no gameplay is needed**.
Everything happens on the main-menu screen.

## Already set up for you
- Current build **deployed** to `C:\_Games\Steam\steamapps\common\7 Days To Die\Mods\ContextMusicManager` (new DLL + `source=` config, `MusicPath=J:\7D2D Custom Music`).
- Test artifacts under **`J:\7D2D Custom Music\_pltest\`** (additive; nothing existing was touched):
  - `direct.m3u` — absolute entries: **T1**, **T3**
  - `loose\LooseA.mp3` (=**T2**) + `loose\infolder.m3u` (relative entry → **T4**)
  - `loose\deep\LooseSub.mp3` (=**T1**)
  - `folderonly\FolderX.mp3` (=**T3**)
- Deployed **Menu** `source` pre-set to `_pltest/direct.m3u`.
- Backup of the deployed config: `…\Config\contexts.xml.pltest-backup`.

## Track legend (all existing, safe menu pieces — audio content only)
| Label | File |
|---|---|
| T1 | `menu\Gravel_Passage_1…` |
| T2 | `menu\Gravel_Passage_2…` (copied as `loose\LooseA.mp3`) |
| T3 | `menu\Gravel_in_the_Heart…` (copied as `folderonly\FolderX.mp3`) |
| T4 | `menu\Gravel_on_the_Heart…` (referenced by `loose\infolder.m3u`) |

## How to observe
- **Log = source of truth.** `DebugLogging` is on. Open the in-game console (**F1**) or tail the newest log in `%APPDATA%\7DaysToDie\logs\`, filtering for **`[CMM]`**. Reloads print lines like `File change in Menu: N -> M files` and config-reload messages.
- **Force a fresh pick** anytime with the **Right Arrow** key (CMM next-track). After each edit, wait ~2 s then press Right Arrow to hear the new pool without waiting for the current track to end.
- **Two files to edit live:** the deployed `…\Config\contexts.xml` (Menu line) and the `_pltest` playlists. Keep both open in an editor.

The clearest *audible* checks are the **source-change to invalid** (music stops → vanilla) and **subfolders toggle** (pool changes) steps; the log confirms the finer reloads.

---

## Steps

### 0. Baseline — playlist source plays
- **Do:** Launch the game to the main menu.
- **Expect:** Only **T1 / T3** play (the old 4-track menu rotation is gone). Right Arrow cycles between T1 and T3. Log shows Menu resolving to 2 files.

### 1. Direct `.m3u` hot-reload
- **Edit:** `J:\7D2D Custom Music\_pltest\direct.m3u` → delete both lines, add just
  `J:\7D2D Custom Music\menu\Gravel_Passage_2_by_Stormwrought.mp3` (**T2**). Save.
- **Do:** wait ~2 s, press Right Arrow.
- **Expect:** now only **T2** plays (T1/T3 gone). Log: `File change in Menu: 2 -> 1 files`.

### 2. Folder + in-folder `.m3u` (union) + reload
- **Edit:** deployed `contexts.xml` Menu line → `source="_pltest/loose"`. Save.
- **Do:** press Right Arrow a few times.
- **Expect:** pool = **T2** (loose `LooseA.mp3`) **and T4** (from `infolder.m3u`) — union of loose music + the in-folder playlist. Log shows Menu with 2 files.
- **Edit:** `_pltest\loose\infolder.m3u` → add a second line
  `..\..\menu\Gravel_Passage_1_by_Stormwrought.mp3` (**T1**). Save, Right Arrow a few times.
- **Expect:** **T1** now joins the rotation (T2 + T4 + T1). Log: `File change in Menu: 2 -> 3 files` — **this proves editing an in-folder `.m3u` in place hot-reloads.**

### 2c. Multiple playlists in one folder (union across `.m3u`'s)
- **Setup (done):** `_pltest\multiplaylist\` holds `list1.m3u`..`list5.m3u`, each 5 random tracks from `h:\music` (25 distinct total, no loose music).
- **Edit:** deployed `contexts.xml` Menu line → `source="_pltest/multiplaylist"`. Save → Right Arrow several times.
- **Expect:** the pool is the **union of all five playlists = 25 tracks**. Log: Menu with `25` files. Right Arrow keeps landing on different tracks.
- **Optional reload:** edit any `listN.m3u` (add or remove a line) → pool count changes on the next tick (proves multi-`.m3u`-in-one-folder edits reload).
- **Playlist file add/remove (exFAT):** J: is exFAT, so its folder mtime doesn't move on add/remove — CMM detects it by set-diffing the top-level `.m3u` listing (only on sloppy filesystems; NTFS stays a pure mtime stat).
  - **Delete** `list5.m3u` → within a tick the pool drops **25 → 20** (`File change in Menu` log).
  - **Re-add** `list5.m3u` (or drop in a new `list6.m3u`) → within a tick the pool climbs back **20 → 25** (or up to 30). This is the case a plain folder-mtime watch misses.

### 3. `subfolders` exception
- **Edit:** deployed `contexts.xml` Menu line → set `subfolders="true"` (keep `source="_pltest/loose"`). Save, Right Arrow.
- **Expect:** the `infolder.m3u` entries (**T4**, **T1**) **drop out**; pool = **T2** (`LooseA.mp3`) + **T1** (`deep\LooseSub.mp3`, now included via recursion). i.e. **`.m3u` ignored, subfolder music included.**
- **Edit:** set `subfolders="false"` again. Save.

### 4. Change `source` on the fly
- **4a absolute folder:** Menu `source="J:\7D2D Custom Music\_pltest\folderonly"` → only **T3** (`FolderX.mp3`).
- **4b relative folder:** Menu `source="menu"` → the original **4 Gravel** menu tracks return.
- **4c invalid playlist:** Menu `source="_pltest/does_not_exist.m3u"` → **music stops / falls back to vanilla** (no CMM track); **no crash**, log notes empty/vanilla. *(Clearest audible check.)*
- **4d recover:** Menu `source="_pltest/direct.m3u"` → music returns (T1/T3).

### 5. Revert
- **Edit:** Menu line back to `source="menu"` and `subfolders="false"` (or restore the backup: copy `contexts.xml.pltest-backup` over `contexts.xml`, or just run a Debug build which re-mirrors the repo config).
- Optionally delete `J:\7D2D Custom Music\_pltest\`.

---

### Pass criteria
Each step's **[CMM] log** matches the expected pool size/reload **and** the audible pool changes as described. Steps 1–2 prove live `.m3u` edits reload (direct + in-folder); step 3 proves the subfolders exception; step 4 proves live `source` changes across playlist/folder/absolute/invalid.
