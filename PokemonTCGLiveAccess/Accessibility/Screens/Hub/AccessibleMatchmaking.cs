using System.Collections.Generic;
using HarmonyLib;
using TPCI.Localization;
using UnityEngine;
using UnityEngine.UI;
using static Accessibility.GameReflect;

namespace Accessibility;

// Matchmaking / "searching for opponent" overlay — driver: MatchmakingOverlay (an exclusive OverlayScreenGroup).
// Shown for ranked/quick search AND the friend-battle flow: sending a challenge (FriendsHUBScreenController.OnJoinMatch
// -> OpenMatchmakingOverlay) and accepting one both land here, with a CANCEL button. Without a component it's silent
// and — being exclusive — leaves the screen underneath navigable, which was exactly the "cancel a friend battle
// request popup isn't speaking" report. When an opponent is found the cancel button hides and the game moves into
// the match.
//
// We hook MatchmakingOverlay.OnActivate(OverlayManager) — its OWN override — rather than the shared base
// OnActivate(): the searching text / header are set in the back half of that override, AFTER the base call, so a
// base-method postfix would read them stale. Hooking the override fires once it's all populated, and only for this
// overlay. Closed by its own OnDeActivate override.
internal sealed class AccessibleMatchmaking : AccessibleUI
{
    private static readonly AccessibleMatchmaking s_instance = new();

    private readonly KeyboardNavigator m_nav;
    private MatchmakingOverlay? m_overlay;

    private AccessibleMatchmaking()
    {
        m_nav = new KeyboardNavigator(this) { OnBack = Cancel };
    }

    [HarmonyPatch(typeof(MatchmakingOverlay), "OnActivate", typeof(OverlayManager))]
    private static class ActivateHook
    {
        private static void Postfix(MatchmakingOverlay __instance)
        {
            s_instance.m_overlay = __instance;
            if (AccessibilityMgr.IsFocused(s_instance)) s_instance.OnGainedFocus();
            else AccessibilityMgr.ShowUI(s_instance);
        }
    }

    [HarmonyPatch(typeof(MatchmakingOverlay), "OnDeActivate")]
    private static class DeactivateHook
    {
        private static void Postfix(MatchmakingOverlay __instance)
        {
            if (s_instance.m_overlay != __instance) return;
            s_instance.m_overlay = null;
            AccessibilityMgr.HideUI(s_instance);
        }
    }

    // "Opponent found": the game signals this only with an audio stinger + the cancel button hiding, then it loads
    // the versus scene — no on-screen text — so a blind player otherwise can't tell the search succeeded.
    [HarmonyPatch(typeof(MatchmakingOverlay), "MatchCreated")]
    private static class MatchCreatedHook
    {
        private static void Postfix(MatchmakingOverlay __instance)
        {
            if (s_instance.m_overlay != __instance || !__instance.gameObject.activeInHierarchy) return;
            AccessibilityMgr.OutputNotification(LocalizationUtils.Get(LocalizationKey.HUB_OPPONENT_FOUND), interrupt: true);
        }
    }

    public string GetHelp() => LocalizationUtils.Get(LocalizationKey.HUB_MATCHMAKING_HELP);

    public void OnGainedFocus()
    {
        if (m_overlay == null) return;
        var items = new List<Selectable>();
        if (CancelButton() is { } cancel && cancel.isActiveAndEnabled) items.Add(cancel);
        m_nav.SetItems(items);
        var status = Status();
        AccessibilityMgr.Output(this, string.IsNullOrWhiteSpace(status) ? LocalizationUtils.Get(LocalizationKey.HUB_SEARCHING) : status, interrupt: true);
        m_nav.FocusFirst(announce: true, interruptPrior: false);
    }

    public void HandleInput()
    {
        if (m_overlay == null || !m_overlay.gameObject.activeInHierarchy)
        {
            m_overlay = null;
            AccessibilityMgr.HideUI(this);
            return;
        }
        m_nav.HandleInput();
    }

    // Escape cancels the search — but only while the cancel button is still up (it hides the moment an opponent is
    // found and we're committing to the match).
    private void Cancel()
    {
        if (m_overlay != null && CancelButton() is { } b && b.gameObject.activeInHierarchy) m_overlay.CancelMatchmaking();
    }

    // The game-mode/friend header + the "searching… / waiting for <friend>…" line.
    private string Status()
    {
        var parts = new List<string>(2);
        if (HubUi.Text(ReadField<LocText>(m_overlay, "gameplayTypeHeader")) is { } header) parts.Add(header);
        if (HubUi.Text(ReadField<LocText>(m_overlay, "searchingText")) is { } searching) parts.Add(searching);
        return string.Join(". ", parts);
    }

    private Button? CancelButton()
    {
        var go = ReadField<GameObject>(m_overlay, "cancelButton");
        return go != null ? go.GetComponent<Button>() : null;
    }
}
