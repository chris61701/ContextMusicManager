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

A Debug build deploys straight into `<GameInstallDir>\Mods\ContextMusicManager` and points
the deployed `settings.xml` at `DebugMusicPath`; set `-p:DebugMusicPath=<your music folder>`.

## Third-party code

Bundled and linked libraries and their licenses are listed in
[THIRD_PARTY_NOTICES.txt](THIRD_PARTY_NOTICES.txt).
