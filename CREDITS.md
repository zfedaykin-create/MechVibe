# Credits

MechVibe is an independent, from-scratch port of the haptic-feedback idea behind
**MechShaker** (for *MechWarrior 5: Mercenaries*) to *MechWarrior 5: Clans*. None
of MechVibe's own code is copied from the original mod's distributed files —
Clans exposes a different telemetry surface than Mercs, so the event hooks, the
IPC pipe, the SimHub integration, and the left/right split are all new work.

Two pieces of the original project's own *source code* (not its shipped binaries)
were reused directly, which is why this project is licensed GPL-3.0 as a whole —
see [LICENSE](LICENSE). The MIT-licensed piece's original license text is
reproduced as-is in [THIRD-PARTY-LICENSES.md](THIRD-PARTY-LICENSES.md), per
that license's own terms.

## MechShaker (engine)

- Author: **sicsix** — https://github.com/sicsix/MechShaker
- License: GPL-3.0
- Distributed mod: https://www.nexusmods.com/mechwarrior5mercenaries/mods/1029
- What MechVibe reused: normalization constants, the landing/footstep intensity
  formulas, and envelope (attack/decay) defaults from `MechShakerEngine`'s effect
  classes. These are re-implemented as C# in `MechVibe`'s SimHub plugin, not
  copy-pasted files — but the values and formulas are sicsix's work.

## MW5-UEVR-Plugins (bridge)

- Author: **Tim Butler** (sicsix) — https://github.com/sicsix/MW5-UEVR-Plugins
- License: MIT
- What MechVibe reused: the shared-memory ring-buffer layout and
  `SetupMemoryMappedFile`/`WriteToSharedMemory` logic from
  `mechshaker_bridge/MechVibeBridge.cpp`, carried into `MechVibeBridge.exe`
  with the UEVR dependency stripped out and a named-pipe server added in front.

## What is NOT included

This package contains none of sicsix's original distributed files — no
`MechShaker.exe`, no compiled Blueprint assets, no `DefaultSettings.yaml`. If
you also play *Mercenaries*, MechShaker is its own separate mod: get it from
the NexusMods link above.

## Everything else

The UE4SS Lua hooks, the named-pipe protocol, the SimHub plugin's polling and
property model, the left/right hardpoint decoding, and the ShakeIt multi-channel
setup are original to this project.
