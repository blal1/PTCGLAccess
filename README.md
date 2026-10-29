# Pokémon TCG Live Access

Screen-reader accessibility mod for **Pokémon TCG Live** (Unity 6, Mono), built on the architecture of
Hearthstone Access and its DevTools: one focused component owns the keyboard and the voice, every game hook is
isolated and reported when a game update breaks it, and the decompiled game is kept per version so updates are
a `git diff` away.

Unofficial fan project, not affiliated with The Pokémon Company International. This repository contains only
the mod's own code: no game files, assets or decompiled game code. **Never publish your `Decompiled` folder.**

## Repository layout

- `PokemonTCGLiveAccess/` — the MelonLoader mod.
  - `Accessibility/AccessibilityMgr.cs` — current screen + stack of UIs; focus, speech, F1 help, F8 on/off.
  - `Accessibility/Accessible*.cs` — components (`AccessibleScreen`, `AccessibleUI`) and building blocks
    (`AccessibleMenu`, `AccessibleItem`, `AccessibleListOfItems`), keys (`AccessibleKey`).
  - `Accessibility/Screens/` — one component per game screen, with its `[HarmonyPatch]` hooks nested inside.
  - `Accessibility/Localization/` + `Localization/<language>/<Feature>.txt` — keyed, tab-separated text.
  - `Accessibility/Compat.cs`, `GameReflect.cs` — game-update breakage report.
- `CommonUtils/`, `LinkGameInstallation/`, `EnvironmentSetup/`, `Decompiler/` — developer tools.
- `Tests/` — offline checks, no game launch.

## Setup (developers)

Requirements: .NET SDK 8+, git, the game, MelonLoader 0.7+ installed in it, `prism.dll` next to the game exe.

1. Link your installation (creates a `Game` junction, no admin needed; MelonLoader needs an ASCII path):
   `dotnet run --project LinkGameInstallation` (or pass the game folder as an argument).
2. Create the private decompiled repository: `dotnet run --project EnvironmentSetup`.
3. Build: `dotnet build PTCGLAccess.sln` — the mod is copied into `Game\Mods` (skipped, with a warning, while
   the previous mod `PokemonTCGLiveAccessibility.dll` is still there: both would speak).
4. Check: `dotnet test Tests`.

## After a game update

1. `dotnet run --project Decompiler` — decompiles the new version into a new branch of `Decompiled/`.
2. `cd Decompiled && git diff <old version> <new version> -- <files of the classes you hook>`.
3. `dotnet test Tests` — lists every hooked or reflected game member the update renamed, with file and line.
4. In game, the mod also speaks at startup how many parts failed to hook; details are in
   `Mods\PokemonTCGLiveAccess.log` (`COMPAT:` lines).

## Speech

[Prism](https://github.com/ethindp/prism) (NVDA, JAWS, SAPI, ...). Build it with CMake
(`cmake -B build -A x64 && cmake --build build --config Release`) and put `prism.dll` next to the game exe.

## Keys

F1 anywhere: the keys of the current screen. F8: accessibility off/on.

## License

MIT — see [LICENSE](LICENSE). Pokémon and Pokémon TCG Live are trademarks of their respective owners.
Most of this code was written with AI assistance (Anthropic's Claude) under the maintainer's direction.
