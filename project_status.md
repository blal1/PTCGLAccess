# Project status

Game version targeted: 1.43.0 (Unity 6000.3.5f2). Previous mod (reference for behaviour):
`Downloads\pokemon-tcg-access-main`.

## Done (builds, offline tests green, NOT yet run in game)

- Core (HSA model): AccessibilityMgr, components, menus, items, lists, keys, Prism speech, keyed localization
  (en + fr), Compat report, GameReflect, pollers (stopped while F8 is off), window fix.
- Tools: LinkGameInstallation, EnvironmentSetup, Decompiler (one branch per game version).
- Shared: KeyboardNavigator, readers (cards, card dex, UI labels, shop, rank), global P / N / digit tab shortcuts.
- Screens/Popups: language select, consent, simple message, modal container, confirm, error, info, professor
  tooltip, dialogue, news, gift, reward, season reset, item grant, FTUE battle intro and focus fallback, rank up,
  FtueTracker (the single owner of every tutorial hook).
- Screens/Hub: home (Learning Lab, Build and Battle sections), B&B lobby and popups, matchmaking, League Tower,
  Battle Pass; tutorial coordination via FtueTracker.
- Screens/Social: profile, avatars, settings, overflow menu and sub-pages, friends panel, add friend, friend
  options, send join match.
- Screens/Collection + Shop: deck select, deck editor, card detail, craft, craft all, deck name/stats/validity/
  options, card dex, shop, product overlay, bundles.
- Screens/Battle: board (zone keys), placement by keyboard, prompts, battle settings, match result, battle log,
  match intro, mulligan reveal, match ready.
- Updater mod (`PokemonTCGLiveUpdaterAccess`), deployed only if MelonLoader is installed in the updater.

## Known gaps

- Deck editor: jumps to the tutorial's card but does not lock navigation to it.
- Shop "All bundles": rows past the first screen not reachable (same as the previous mod).
- Battle settings: announced only when opened with Escape, not by mouse click on the gear.
- A battle prompt that opens under a tutorial dialogue waits in a poller; an `AccessibilityMgr.ShowUIBelow`
  would remove it.
- Text fields rely on KeyboardNavigator's own field handling; a per-component text mode that still passes
  Escape/Tab would be cleaner.

## Needs in-game testing

Everything. Move the previous `PokemonTCGLiveAccessibility.dll` out of `Game\Mods`, then `dotnet build`.
Priority checks: first launch (language, consent), hub digits, deck editor pick/place, battle prompts and damage
placement in the tutorial, return to hub after a match, no double announcements on overlays.

## Beyond parity (100% of the game)

Surfaces the previous mod never covered must be found in game first: run a slice of the recon kit
(`Accessibility-Mod-Template\recon`) per surface, then implement.
