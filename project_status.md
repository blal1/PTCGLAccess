# Project status

Game version targeted: 1.43.0 (Unity 6000.3.5f2). Previous mod (reference for behaviour):
`Downloads\pokemon-tcg-access-main`.

## Done

- Core (HSA model): AccessibilityMgr, components, menus, items, lists, keys, Prism speech, keyed localization
  (en + fr), Compat report, GameReflect, pollers, window fix.
- Tools: LinkGameInstallation, EnvironmentSetup, Decompiler (one branch per game version).
- Offline tests: localization coverage, no spoken literals, game members resolve in the installed game.
- Startup loading screen.

## Migration from the previous mod (parity first, then the rest of the game)

- Shared infrastructure: keyboard navigator, card/shop/rank readers, currency, player name, hub tab shortcuts.
- Startup and popups: language select, consent, simple message, modal container, confirm, error, info,
  professor tooltip, dialogue, news, gift, reward, season reset, item grant, FTUE, rank up.
- Hub: home, learning lab, build and battle, matchmaking, league tower, expansion challenge.
- Profile and social: profile, avatars, settings, overflow menus, friends, add friend, friend options, send join match.
- Collection: deck select, edit deck, add card, craft, deck name/stats/validity/options, card dex, shop, bundles.
- Battle: board, prompts, placement, mulligan, match result, battle settings, battle log, match intro/ready.
- Updater (separate mod for the game's updater).

## Beyond parity (100% of the game)

Surfaces the previous mod never covered must be found in game first: run a slice of
`unity-accessibility-recon` per surface (build fingerprint, static UI ledger, runtime probe), then implement.

## Needs in-game testing

Everything ported: nothing has been run in the game yet with this mod.
