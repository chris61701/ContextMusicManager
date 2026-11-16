# Context Music Manager

A 7 Days to Die mod that plays your own music based on what is happening in the game.
It picks music for combat, blood moons, traders, home and dozens of other contexts,
normalizes loudness across your whole library, and can pipe music through in-world radios
and speakers.

Supports **Alpha 20, Alpha 21, 2.6 and 3.x**. One DLL serves every version.

**Download and documentation:** [Nexus Mods](https://www.nexusmods.com/7daystodie/mods/10649)

## Repository layout

| Path | What it is |
| --- | --- |
| `Harmony/` | All of the mod's C# source (not only the Harmony patches) |
| `Config/` | The default `settings.xml`, `contexts.xml` and the `context.lua` decision script |
| `CMM_Decoder/` | The native audio library (`CMM_Decoder.dll`): decoding, mixing, output, acoustics. C, built with mingw. See its own [README](CMM_Decoder/README.md) |
| `Sounds/` | Radio click recordings that ship with the mod |
| `tools/` | Offline test harnesses, simulators and benchmarks used during development |
| `docs/` | Mod page sources, design notes and the development history |

## Building from source

Requirements:

- Visual Studio / MSBuild with the .NET Framework 4.8.1 targeting pack
- An installed copy of 7 Days to Die. The project compiles against the game's own
  assemblies, which are not in this repository.
- To rebuild the native DLL only: MSYS2 / mingw-w64. A prebuilt
  `CMM_Decoder/dist/CMM_Decoder.dll` is committed, so this is optional.

Clone with submodules (the native libraries come from their upstream repositories):

```sh
git clone --recursive https://github.com/chris61701/ContextMusicManager.git
```

The project file defaults to the author's install paths. Point it at your own game install
with `GameInstallDir`, which overrides the default on the command line:

```sh
# Compile without deploying anywhere
MSBuild ContextMusicManager.csproj -p:Configuration=Debug -p:CMMDeployAll=false ^
  -p:OutputPath=tempverify\ "-p:GameInstallDir=D:\Steam\steamapps\common\7 Days To Die"

# Release zip into dist\
MSBuild ContextMusicManager.csproj -p:Configuration=Release ^
  "-p:GameInstallDir=D:\Steam\steamapps\common\7 Days To Die"
```

A Debug build deploys straight into `<GameInstallDir>\Mods\ContextMusicManager`, along with
a `settings.dev.xml` that turns on debug logging and overrides `MusicPath` with
`DebugMusicPath`; set `-p:DebugMusicPath=<your music folder>`. That file is read before
`settings.xml`, so in a Debug install the music folder is changed there, not in `settings.xml`.

### Release builds on GitHub

Official release zips are built by GitHub Actions (`.github/workflows/release.yml`), not on a
local machine. Pushing a tag such as `v1.0.5b-rc2` builds `CMM_Decoder.dll` from source (signed
once code signing is set up), compiles the mod, and opens a draft GitHub release with the zip
attached. The game's assemblies come from the free 7 Days to Die dedicated server, downloaded
anonymously from Steam during the build; they are never stored in the repository.

## License

Context Music Manager is released under the [MIT License](LICENSE). Bundled and linked
libraries keep their own licenses, all of them listed with full texts in
[THIRD_PARTY_NOTICES.txt](THIRD_PARTY_NOTICES.txt). FFmpeg, linked statically into
`CMM_Decoder.dll`, is LGPL 2.1 or later; this repository and `CMM_Decoder/build_*.sh` are what
let anyone rebuild that DLL against a modified FFmpeg.

## Code signing policy

Free code signing provided by [SignPath.io](https://about.signpath.io), certificate by
[SignPath Foundation](https://signpath.org).

- **What is signed:** `CMM_Decoder.dll`, the mod's native audio library, and nothing else.
  The managed DLLs are loaded by the game's Mono runtime, which Windows does not check.
- **How:** the DLL is built from this repository by the
  [Build CMM_Decoder](.github/workflows/build-decoder.yml) workflow on GitHub-hosted runners,
  from `main` or a `release/*` branch only, and every signing request is approved by hand
  before SignPath signs it. A binary built anywhere else is never signed.
- **Team:**
  - Committers and reviewers: [chris61701](https://github.com/chris61701)
  - Approvers: [chris61701](https://github.com/chris61701)

  Every change from outside the team is reviewed before it is merged.

## Privacy

This program will not transfer any information to other networked systems unless
specifically requested by the user or the person installing or operating it. In multiplayer
it exchanges music-state messages with the game server through the game's own networking,
which is part of playing on that server.

## Uninstalling

Delete the `ContextMusicManager` folder from the game's `Mods` folder. That folder holds
everything the mod installs and everything it writes (settings, caches), and the mod changes
nothing outside it.

**If your music is in the mod's own `Music` folder** (the default location), move it somewhere
else first: deleting the mod folder deletes it too. A library kept elsewhere is only ever read.
