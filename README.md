# MechVibe (source)

Source code for [MechVibe](https://www.nexusmods.com/mechwarrior5clans/mods/), a bass-shaker
haptics mod for *MechWarrior 5: Clans*. If you just want to install and play, get the packaged
release from that NexusMods page instead — this repo is the GPL-3.0 Corresponding Source, useful
if you want to build it yourself, read how it works, or send a patch.

```
Game (UE4SS Lua mod)  --pipe-->  MechVibeBridge.exe  --shared memory-->  SimHub plugin  -->  ShakeIt Bass Shakers
```

## Layout

```
lua/main.lua          UE4SS Lua mod - hooks the game's telemetry, packs it into 32-byte
                       events, sends them over a named pipe
bridge/main.cpp        Standalone relay: reads the pipe, republishes into a shared-memory
                       ring buffer. No UE4SS/Unreal dependency - builds with plain cl.exe
bridge/pipetest.cpp    Connectivity check tool (--write / --read), not required to run the mod
simhub/MechVibe.cs     SimHub plugin: polls the shared memory, computes intensity curves,
                       exposes ~70 properties for ShakeIt Bass Shakers to consume
simhub/Settings.cs     The plugin's tunable settings (envelopes, weighting) and its
                       code-generated (no XAML) settings page
```

## Building

Each component builds standalone with nothing but the platform toolchain - no CMake, no
UE4SS core, no .NET SDK:

```powershell
bridge\build.ps1    # needs the MSVC C++ toolchain (Visual Studio Build Tools is enough)
simhub\build.ps1    # needs SimHub installed locally (references its DLLs) and either
                     # csc.exe from VS Build Tools or the in-box .NET Framework compiler
simhub\install.ps1  # copies the built plugin into your SimHub folder (SimHub must be closed)
```

`lua/main.lua` doesn't need building - drop it into `Mods\MechVibe\Scripts\` in the game's
`Binaries\Win64` folder (with the built `MechVibeBridge.exe` alongside it under `Bridge\` -
see the packaged release's README for the exact layout and `mods.txt` line).

## License

GPL-3.0 — see [LICENSE](LICENSE). [CREDITS.md](CREDITS.md) and
[THIRD-PARTY-LICENSES.md](THIRD-PARTY-LICENSES.md) cover what this project reused from the
original *MechShaker* (a *Mercenaries* mod by sicsix) and under what terms.
