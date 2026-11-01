using System;
using System.Collections.Generic;
using System.Linq;
using _Rainier.Scripts.UI.DeckEditor.Collection;
using HarmonyLib;
using TMPro;
using TPCI.Rainier.UI.OverlayMenus;
using TPCI.UI.PlaySubGroups;
using TPCI.UI.Screens;
using TPCI.UI.News;
using TPCI.UI.Screens.ShopScreen;
using UnityEngine;
using UnityEngine.UI;

namespace Accessibility;

// Safety net for any OverlayScreenGroup no specific component takes. Without it an unknown overlay leaves focus on
// the screen beneath, in silence, with its buttons out of reach.
//
// No list of "known" overlays to keep in sync: when an overlay opens we remember who has focus, wait a moment, and
// only step in if nobody took over. Components that open through a hook, a poller or a deferred ShowUI therefore
// keep the overlay to themselves, including ones added later. The type list below is for overlays with a dedicated
// component, which can re-present one while keeping focus, and for overlays never meant to own the keyboard.
//
// ModalPopup / ModalDrawer need no such net: the game has no ModalPopup subclass and every container opens through
// ModalElementContainer.Open, which AccessibleModalContainer hooks. An overlay subclass overriding OnActivate without
// calling base is not seen here.
internal sealed class AccessibleGenericOverlay : AccessibleUI
{
    private const float ClaimDelaySeconds = 0.25f;

    private readonly struct Pending
    {
        public readonly OverlayScreenGroup Overlay;
        public readonly AccessibleComponent? Focused;
        public readonly AccessibleScreen? Screen;
        public readonly float Due;

        public Pending(OverlayScreenGroup overlay, AccessibleComponent? focused, AccessibleScreen? screen, float due)
        {
            Overlay = overlay; Focused = focused; Screen = screen; Due = due;
        }
    }

    private static readonly AccessibleGenericOverlay s_instance = new();
    private static readonly List<Pending> s_pending = new();

    private readonly List<OverlayScreenGroup> m_stack = new();
    private readonly KeyboardNavigator m_nav;
    private string m_lastSig = "";

    private AccessibleGenericOverlay()
    {
        m_nav = new KeyboardNavigator(this) { AllowTab = true, OnBack = Cancel };
    }

    private OverlayScreenGroup? Top => m_stack.Count > 0 ? m_stack[m_stack.Count - 1] : null;

    private static bool IsExcluded(OverlayScreenGroup o) =>
        o is ConfirmOverlay or ErrorOverlay or InfoPopup or ProfessorTooltip or GiftGrantingOverlay or NewsOverlay
            or ContentItemGrantingOverlay or AppRatingPrompt or LoadingPopupOverlay or LevelUpTransitionOverlay
            or BuildAndBattlePurchaseOverlay or BuildAndBattleGenericOverlay or BuildAndBattleBoxOpeningPopup
            or BuildAndBattleDeckInspectorController
            or ContentItemRewardOverlay or BigCardOverlayController or MatchmakingOverlay or BundlesOverlay or ShopProductOverlay
            or DeckStatsOverlayController or DeckValidityOverlay or CraftAllConfirmOverlay
            or AddFriendOverlayScreenGroup or FriendOptionsOverlayScreenGroup or SendJoinMatchOverlayScreenGroup
        || AccessibleReward.IsPresenting(o);

    public void HandleInput()
    {
        var top = Top;
        if (top == null || !PopupUtils.IsOverlayOpen(top))
        {
            if (top != null) m_stack.Remove(top);
            Refocus();
            return;
        }
        // Buttons can appear after an animation.
        var sig = Signature(top);
        if (sig != m_lastSig)
        {
            var hadNone = m_nav.Count == 0;
            Rebuild(top);
            if (hadNone && m_nav.Count > 0) m_nav.FocusFirst(announce: true, interruptPrior: false);
            return;
        }
        if (AccessibleKey.READ_CUR_LINE.IsPressed())
        {
            Speak(top);
            return;
        }
        m_nav.HandleInput();
    }

    public string GetHelp() => LocalizationUtils.Get(LocalizationKey.POPUP_GENERIC_HELP);

    public void OnGainedFocus()
    {
        var top = Top;
        if (top == null) return;
        Rebuild(top);
        Speak(top);
        if (m_nav.Count > 0) m_nav.FocusFirst(announce: true, interruptPrior: false);
    }

    private void Speak(OverlayScreenGroup overlay)
    {
        var text = BodyText(overlay);
        if (!string.IsNullOrWhiteSpace(text)) AccessibilityMgr.Output(this, text, interrupt: true);
    }

    private void Rebuild(OverlayScreenGroup overlay)
    {
        m_nav.SetItems(Controls(overlay).OrderBy(s => PopupUtils.ReadingOrder(s.transform)));
        m_lastSig = Signature(overlay);
    }

    // The stack changed: back to the overlay now on top, or give the keyboard back.
    private void Refocus()
    {
        if (m_stack.Count == 0) AccessibilityMgr.HideUI(this);
        else if (AccessibilityMgr.IsFocused(this)) OnGainedFocus();
    }

    // Escape: only an explicit close button. Never closes a waiting or a mandatory overlay behind the game's back.
    private void Cancel()
    {
        if (Top is not { } top) return;
        top.GetComponentsInChildren<Button>(includeInactive: false)
            .FirstOrDefault(b => b.isActiveAndEnabled && IsCloseName(b.gameObject.name))?.onClick.Invoke();
    }

    private static bool IsCloseName(string name)
    {
        var n = name.ToLowerInvariant();
        return n.Contains("close") || n.Contains("cancel") || n.Contains("back") || n.Contains("exit");
    }

    private static List<Selectable> Controls(OverlayScreenGroup overlay) =>
        overlay.GetComponentsInChildren<Selectable>(includeInactive: false)
            .Where(s => s.isActiveAndEnabled && s is not Scrollbar && !SocialUi.IsFullScreen(s) && !SocialUi.IsNested(s, overlay.transform))
            .ToList();

    private static string Signature(OverlayScreenGroup overlay) => string.Join(",", Controls(overlay).Select(s => s.GetInstanceID()));

    // Visible text that is not a control's label. A full-screen "tap outside" button is not a control: whole
    // overlays sit under one.
    private static string BodyText(OverlayScreenGroup overlay)
    {
        var parts = new List<string>();
        foreach (var t in overlay.GetComponentsInChildren<TMP_Text>(includeInactive: false).OrderBy(t => PopupUtils.ReadingOrder(t.transform)))
        {
            var parent = t.GetComponentInParent<Selectable>();
            if (parent != null && !SocialUi.IsFullScreen(parent)) continue;
            var s = t.text?.Trim() ?? "";
            if (s.Any(char.IsLetterOrDigit) && !parts.Contains(s)) parts.Add(s);
        }
        return string.Join(". ", parts);
    }

    private void Decide(Pending p)
    {
        var overlay = p.Overlay;
        if (overlay == null || !PopupUtils.IsOverlayOpen(overlay) || m_stack.Contains(overlay)) return;
        // A UI took focus, or the screen changed. Focus falling back to the screen is only the previous UI closing.
        var focused = AccessibilityMgr.Focused;
        if (AccessibilityMgr.CurrentScreen != p.Screen || (focused != p.Focused && focused is not AccessibleScreen)) return;

        var text = BodyText(overlay);
        if (string.IsNullOrWhiteSpace(text) && Controls(overlay).Count == 0) return;   // nothing to read or press (an animation)
        Log.Info($"GenericOverlay: taking {overlay.GetType().Name} ({overlay.gameObject.name})");
        // An overlay that lets clicks through never held the player: say it, leave the keys alone.
        if (!overlay.raycastTarget)
        {
            if (!string.IsNullOrWhiteSpace(text)) AccessibilityMgr.OutputNotification(text);
            return;
        }
        m_stack.Add(overlay);
        PopupUtils.ShowOrRefresh(this);
    }

    [AccessibilityPoller]
    private static void Poll()
    {
        if (s_pending.Count == 0) return;
        var now = Time.unscaledTime;
        for (var i = 0; i < s_pending.Count; i++)
        {
            var p = s_pending[i];
            if (now < p.Due) continue;
            s_pending.RemoveAt(i--);
            s_instance.Decide(p);
        }
    }

    // Prefix: the component hooks run later in this same call, so this records who had focus BEFORE them.
    [HarmonyPatch(typeof(OverlayScreenGroup), "OnActivate", new Type[0])]
    private static class ActivateHook
    {
        private static void Prefix(OverlayScreenGroup __instance)
        {
            if (!IsExcluded(__instance)) s_pending.Add(new Pending(__instance, AccessibilityMgr.Focused, AccessibilityMgr.CurrentScreen, Time.unscaledTime + ClaimDelaySeconds));
        }
    }

    [HarmonyPatch(typeof(OverlayScreenGroup), "OnDeActivate")]
    private static class DeactivateHook
    {
        private static void Postfix(OverlayScreenGroup __instance)
        {
            s_pending.RemoveAll(p => p.Overlay == __instance);
            if (s_instance.m_stack.Remove(__instance)) s_instance.Refocus();
        }
    }
}
