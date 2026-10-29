using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using TMPro;
using TPCI.UI.PlaySubGroups;
using UnityEngine;
using UnityEngine.UI;
using static Accessibility.GameReflect;

namespace Accessibility;

// Friendly-battle request overlay — driver: SendJoinMatchOverlayScreenGroup (an OverlayScreenGroup).
// Opened both ways: challenging a friend (SEND) and accepting a friend's challenge (JOIN). It shows
// the deck you'll bring (deckNameText + a validity icon), a turn-timer toggle (when sending), a confirm
// button (SendMatchRequest), a change-deck button (OpenDeckManager), and a close. Confirming kicks off
// matchmaking (MatchmakingOverlay -> NetworkMatchController).
//
// Open: its own OnActivate(OverlayManager) override (it runs UpdateSelectedDeck first, so the deck line is
// current). Close: it doesn't override OnDeActivate, so we hook the shared base OverlayScreenGroup.OnDeActivate
// with an identity guard (typed as the base: no bad cast for the other overlays).
internal sealed class AccessibleSendJoinMatchOverlay : AccessibleUI, ISharesPlayerName
{
    private static readonly AccessibleSendJoinMatchOverlay s_instance = new();

    private readonly KeyboardNavigator m_nav;
    private SendJoinMatchOverlayScreenGroup? m_overlay;

    private AccessibleSendJoinMatchOverlay()
    {
        m_nav = new KeyboardNavigator(this) { AllowTab = false, OnBack = Close };
    }

    public void HandleInput() => m_nav.HandleInput();

    public string GetHelp() => LocalizationUtils.Get(LocalizationKey.SOCIAL_OVERLAY_HELP);

    public void OnGainedFocus() => Rebuild();

    private void Close() => m_overlay?.Close();

    private void Rebuild()
    {
        if (m_overlay == null || !m_overlay.gameObject.activeInHierarchy) return;

        // Buttons (confirm / change deck / close) + the turn-timer toggle, in reading order.
        var items = m_overlay.GetComponentsInChildren<Selectable>(includeInactive: false)
            .Where(s => (s is Button || s is Toggle) && s.isActiveAndEnabled)
            .OrderBy(s => SocialUi.ReadingOrder(s.transform))
            .ToList();
        m_nav.SetItems(items, ItemLabel);
        m_nav.AllowHorizontal = true;

        // Title, the deck you'll bring, then (when ACCEPTING) the game's "no turn timer" banner —
        // sighted players see that banner before they commit, so it is part of the first announcement.
        var parts = new List<string> { LocalizationUtils.Get(LocalizationKey.SOCIAL_BATTLE_REQUEST) };
        var deck = DeckLine();
        if (!string.IsNullOrWhiteSpace(deck)) parts.Add(deck);
        if (NoTimerBanner() is { } banner) parts.Add(banner);
        AccessibilityMgr.Output(this, string.Join(". ", parts), interrupt: true);
        if (items.Count > 0) m_nav.FocusFirst(announce: true, interruptPrior: false);
    }

    // "Deck <name>[, invalid]" — the deck you'll bring + whether it fails validation.
    private string DeckLine()
    {
        var name = ReadField<TMP_Text>(m_overlay, "deckNameText")?.text;
        if (string.IsNullOrWhiteSpace(name)) return "";
        var invalid = ReadField<GameObject>(m_overlay, "invalidIcon");
        return LocalizationUtils.Format(invalid != null && invalid.activeInHierarchy ? LocalizationKey.SOCIAL_DECK_INVALID_FORMAT : LocalizationKey.SOCIAL_DECK_FORMAT, name);
    }

    // Shown by SetupForReceivingRequest when the challenger turned timers off. Prefer the banner's
    // own (game-localized) text; the fallback only covers a banner with no readable text.
    private string? NoTimerBanner()
    {
        var banner = ReadField<GameObject>(m_overlay, "turnTimerBanner");
        if (banner == null || !banner.activeInHierarchy) return null;
        var text = string.Join(" ", banner.GetComponentsInChildren<TMP_Text>(includeInactive: false)
            .Select(t => t.text?.Trim()).Where(t => !string.IsNullOrEmpty(t)));
        return string.IsNullOrWhiteSpace(text) ? LocalizationUtils.Get(LocalizationKey.SOCIAL_NO_TURN_TIMER) : text;
    }

    // The turn-timer toggle is inverted — ON turns the timers OFF (FriendsHUBScreenController
    // .HandleTurnTimerToggleChange: useMatchTimer = !value). "Checked" alone doesn't say which way
    // that cuts, so state the consequence.
    private string ItemLabel(Selectable s)
    {
        var timer = ReadField<Toggle>(m_overlay, "turnTimerToggle");
        if (timer == null || s != timer) return KeyboardNavigator.DefaultLabel(s);
        return LocalizationUtils.Format(timer.isOn ? LocalizationKey.SOCIAL_TIMER_OFF_FORMAT : LocalizationKey.SOCIAL_TIMER_ON_FORMAT,
            KeyboardNavigator.ResolveBaseLabel(s));
    }

    [HarmonyPatch(typeof(SendJoinMatchOverlayScreenGroup), "OnActivate", new[] { typeof(OverlayManager) })]
    private static class OnActivateHook
    {
        private static void Postfix(SendJoinMatchOverlayScreenGroup __instance)
        {
            s_instance.m_overlay = __instance;
            if (AccessibilityMgr.IsFocused(s_instance)) s_instance.Rebuild();
            else AccessibilityMgr.ShowUI(s_instance);
        }
    }

    [HarmonyPatch(typeof(OverlayScreenGroup), "OnDeActivate")]
    private static class OnDeActivateHook
    {
        private static void Postfix(OverlayScreenGroup __instance)
        {
            if (s_instance.m_overlay == null || !ReferenceEquals(s_instance.m_overlay, __instance)) return;
            AccessibilityMgr.HideUI(s_instance);
        }
    }
}
