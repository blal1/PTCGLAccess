# Pokémon TCG Live Access — working rules

User: blind, screen reader user, French speaker. Answers in French, lists not tables.

## Architecture (Hearthstone Access model on MelonLoader)

- `PokemonTCGLiveAccess/Accessibility/AccessibilityMgr.cs` — one current `AccessibleScreen` + a stack of `AccessibleUI`.
  Only the focused one (top UI, else screen) gets `HandleInput()` each frame and may speak. Every call into a
  component is exception-guarded.
- Components implement `HandleInput()`, `GetHelp()` (F1), `OnGainedFocus()` (announce where the player is).
- Building blocks speak through their parent (`AccessibleElement`): `AccessibleMenu` (vertical options),
  `AccessibleListOfItems<T>` (horizontal row), `AccessibleItem` (line-by-line reading),
  `KeyboardNavigator` (drives the game's uGUI Selectables).
- Hooks: `[HarmonyPatch(typeof(GameType), "Method")]` nested classes, discovered and applied one by one by
  `Compat.PatchAll` (a broken target is reported, never fatal). Hooks call only `SetScreen` / `ClearScreen` /
  `ShowUI` / `HideUI` / `OutputNotification` or the component's own methods.
- Surfaces with nothing to hook: a static `[AccessibilityPoller]` method (runs every frame; keep it cheap).
- Never edit `Main.cs` for a feature.

## Rules for every feature

- One file per screen/UI in `Accessibility/Screens/<Area>/Accessible<Name>.cs`, `internal sealed`, singleton
  `s_instance`, hooks nested inside it.
- Text: `LocalizationUtils.Get(LocalizationKey.X)` / `Format(...)`; the game's own words first with
  `LocalizationUtils.GetGame("resource_id", LocalizationKey.FALLBACK)`. Keys in
  `Accessibility/Localization/LocalizationKey.<Area>.cs` (partial class, `new(nameof(KEY))`, prefix = area), lines in
  `Localization/en/<Area>.txt` AND `Localization/fr/<Area>.txt` (`KEY<TAB>TEXT<TAB>COMMENT`). Official French
  Pokémon TCG terms. Never speak a string literal (a test fails).
- Keys: `AccessibleKey` partial file per area (`AccessibleKey.<Area>.cs`) with exact modifiers. Reuse the common
  keys (CONFIRM, BACK, ESCAPE, arrows, Home/End, Tab, numbers). Update the component's `GetHelp()`.
- Game members by name: `ReadField<T>(obj, "name")` (`using static Accessibility.GameReflect;`) or
  `AccessTools` with a string literal, so `Tests/GameCompatTests` can check them. Never guess a name: verify in
  `Decompiled/` (run `dotnet run --project EnvironmentSetup` once) or `C:\PokemonTCGLive\Decompiled_Game_DLLs`.
- Act through the game's own paths (`Button.onClick.Invoke()`, `Selectable.Select()`, the game's operations), never
  by faking state. Read live data; cache object references, never values.
- Speech: `interrupt: true` for answers to a key press, `false` for queued announcements.
- Keep the hard-won comments from the previous mod (why a hook exists, which game bug it works around).

## Build and check

- `dotnet build PTCGLAccess.sln` (deploys to `Game\Mods` unless the previous mod is still installed).
- `dotnet test Tests` — localization coverage + every game member name resolves in the installed game.
- After a game update: `dotnet run --project Decompiler`, then `git diff <old> <new>` in `Decompiled/` on the
  classes the hooks target, then `dotnet test Tests`.
- Never commit or share `Decompiled/` (proprietary code).
