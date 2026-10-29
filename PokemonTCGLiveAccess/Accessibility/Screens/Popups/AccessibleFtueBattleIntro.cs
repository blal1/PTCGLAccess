using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine.UI;

namespace Accessibility;

// FTUE tutorial battle intro — the "BATTLE X OUT OF Y" overlay shown before each of the
// six tutorial battles (and on resume of a partially-completed tutorial).
//
// Driver: FTUEManager. Everything hangs off one piece of game state: `FTUEManagerParts.battleCanvas`, the
// GameObject the panel lives on. Three methods toggle it, and each is hooked here so we open and close
// exactly when the game does:
//   - `ChangeBattleText()` — battleCanvas.SetActive(true) + OverlayManager.OpenOverlay, then fills
//     in the battle count header and description. But only while battles remain: once the last one
//     is done it falls through to CloseBattleCanvas() instead. So "ChangeBattleText ran" does NOT
//     mean "the panel is up" — ask the canvas.
//   - `CloseBattleCanvas()` — battleCanvas.SetActive(false) + OverlayManager.CloseOverlay. Reached
//     from that else-branch and from the SkipThis / SkipAll completion callbacks.
//   - `PlayBattle()` — battleCanvas.SetActive(false) directly, WITHOUT calling CloseBattleCanvas,
//     then triggers the animator into the match.
//
// That last one is why the previous mod used to strand the player. Hooking only CloseBattleCanvas
// meant pressing PLAY never closed us: we sat under Battle for the whole tutorial match, and after the
// final battle ChangeBattleText's else-branch closed the panel while the postfix promptly re-opened us
// over a now-hidden canvas — an empty popup that swallowed every keypress.
//
// So there is no open flag and no cached manager here: `FTUEManager.instance` and
// `battleCanvas.activeInHierarchy` already are the game's answer. It's a plain GameObject
// SetActive, not an OverlayManager canvas-disable, so activeInHierarchy is trustworthy.
//
// Text and buttons live on `FTUEManagerParts`, reached through the private `FTUEManager.CurrentParts`
// property, which picks between the portrait and landscape variants and memoises the result. All its
// fields are public (battleCountText / battleText LocText.CurrentText gives the formatted display string,
// e.g. "BATTLE 1 OUT OF 6", and the three Buttons).
internal sealed class AccessibleFtueBattleIntro : AccessibleUI
{
    private static readonly AccessibleFtueBattleIntro s_instance = new();

    // Handle on the game's own lazily-resolved parts property — not a copy of its value.
    private static readonly MethodInfo? s_currentParts = AccessTools.PropertyGetter(typeof(FTUEManager), "CurrentParts");

    private readonly KeyboardNavigator m_nav;

    private AccessibleFtueBattleIntro()
    {
        m_nav = new KeyboardNavigator(this);
    }

    public void HandleInput()
    {
        // Backstop for hide paths we don't hook — FTUEManager also blanks both parts' canvases
        // directly when it tears the tutorial down. Never keep a hidden panel: it would
        // own the keyboard and swallow every keypress.
        if (!IsOpen)
        {
            AccessibilityMgr.HideUI(this);
            return;
        }
        m_nav.HandleInput();
    }

    public string GetHelp() => LocalizationUtils.Get(LocalizationKey.POPUP_BUTTONS_HELP);

    public void OnGainedFocus()
    {
        var parts = CurrentParts();
        if (parts == null) return;
        var items = new List<Selectable>();
        foreach (var b in new[] { parts.playButton, parts.skipThisButton, parts.skipAllButton })
            if (b != null && b.gameObject.activeInHierarchy) items.Add(b);
        m_nav.SetItems(items);
        // Visual layout in the prefab is PLAY on top (large, full width) with SKIP THIS and
        // SKIP ALL side by side below it. That block is far wider than it is tall, so the
        // bounding-box auto-detect reads it as a horizontal strip; the natural reading flow is
        // vertical (primary action → secondary → most-destructive), so force a flat list.
        m_nav.ForceFlatVertical();
        m_nav.AllowTab = false;

        var preamble = PopupUtils.Preamble(PopupUtils.Loc(parts.battleCountText), PopupUtils.Loc(parts.battleText), null);
        if (!string.IsNullOrWhiteSpace(preamble)) AccessibilityMgr.Output(this, preamble!, interrupt: true);
        // Queued so the description plays in full before the button label.
        m_nav.FocusFirst(announce: true, interruptPrior: false);
    }

    // The game's own "is the intro panel showing" flag.
    private static bool IsOpen => CurrentParts()?.battleCanvas is { } canvas && canvas.activeInHierarchy;

    private static FTUEManagerParts? CurrentParts()
    {
        var manager = FTUEManager.instance;
        if (manager == null || s_currentParts == null) return null;
        return s_currentParts.Invoke(manager, null) as FTUEManagerParts;
    }

    // ChangeBattleText both opens the panel and refreshes it for the next battle in the sequence
    // (SkipThisBattle advances a checkpoint without ever hiding the canvas): re-announce on a refresh so
    // the player hears the new battle's header and description.
    [HarmonyPatch(typeof(FTUEManager), "ChangeBattleText")]
    private static class ChangeBattleTextHook
    {
        private static void Postfix()
        {
            if (IsOpen) PopupUtils.ShowOrRefresh(s_instance);
            else AccessibilityMgr.HideUI(s_instance);   // the else-branch: last battle done
        }
    }

    [HarmonyPatch(typeof(FTUEManager), "CloseBattleCanvas")]
    private static class CloseBattleCanvasHook
    {
        private static void Postfix() => AccessibilityMgr.HideUI(s_instance);
    }

    // PlayBattle bypasses CloseBattleCanvas, so it needs its own hook or pressing PLAY leaves this popup
    // parked over the whole match.
    [HarmonyPatch(typeof(FTUEManager), "PlayBattle")]
    private static class PlayBattleHook
    {
        private static void Postfix() => AccessibilityMgr.HideUI(s_instance);
    }
}
