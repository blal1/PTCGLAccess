using System.Collections.Generic;
using HarmonyLib;
using TPCI.Localization;
using static Accessibility.GameReflect;

namespace Accessibility;

// Deck Stats overlay (new in game 1.5.0) — driver: DeckStatsOverlayController (an OverlayScreenGroup) opened from
// the deck editor's Stats button (HUBEditDeckController.OpenStatsMenu -> SetDeckInfo -> Activate) or the deck
// options' DECK STATS. It shows the deck's win/loss record (DeckInfoHelper.GetWinLossInfo) plus a Reset Stats
// button whose confirm is a ModalElementContainer (its own handler). Silent without us: we announce the record on
// open and navigate its buttons.
//
// DeckStatsOverlayController DOES override OnActivate, so we patch that override directly. It doesn't override
// OnDeActivate, so the close hook is the shared base OverlayScreenGroup.OnDeActivate with a base-typed instance
// and an identity check (typing it as the subclass would bad-cast for every other overlay).
internal sealed class AccessibleDeckStats : AccessibleUI
{
    private static readonly AccessibleDeckStats s_instance = new();

    private readonly KeyboardNavigator m_nav;
    private DeckStatsOverlayController? m_overlay;

    private AccessibleDeckStats()
    {
        m_nav = new KeyboardNavigator(this) { AllowTab = false, OnBack = Close };
    }

    public void HandleInput()
    {
        if (m_overlay == null || !m_overlay.gameObject.activeInHierarchy) { Hide(); return; }
        m_nav.HandleInput();
    }

    public string GetHelp() => LocalizationUtils.Get(LocalizationKey.COLLECTION_POPUP_HELP);

    public void OnGainedFocus()
    {
        if (m_overlay == null) return;
        m_nav.SetItems(CollectionUi.Buttons(m_overlay));
        m_nav.AllowHorizontal = true;
        AccessibilityMgr.Output(this, Stats(), interrupt: true);
        m_nav.FocusFirst(announce: true, interruptPrior: false);
    }

    // Escape = close: the overlay's own teardown.
    private void Close() => m_overlay?.Deactivate();

    private void Hide()
    {
        m_overlay = null;
        AccessibilityMgr.HideUI(this);
    }

    // "Deck stats. 3 wins, 2 losses, win rate 60%" — the numbers are the overlay's own labels.
    private string Stats()
    {
        var title = LocalizationUtils.Get(LocalizationKey.COLLECTION_DECK_STATS);
        if (m_overlay == null) return title;
        var parts = new List<string>(3);
        if (CollectionUi.Text(ReadField<LocText>(m_overlay, "winsLabel")) is { } wins) parts.Add(LocalizationUtils.Format(LocalizationKey.COLLECTION_STATS_WINS, wins));
        if (CollectionUi.Text(ReadField<LocText>(m_overlay, "lossesLabel")) is { } losses) parts.Add(LocalizationUtils.Format(LocalizationKey.COLLECTION_STATS_LOSSES, losses));
        if (CollectionUi.Text(ReadField<LocText>(m_overlay, "winLossRatioLabel")) is { } ratio) parts.Add(LocalizationUtils.Format(LocalizationKey.COLLECTION_STATS_RATIO, ratio));
        return parts.Count == 0 ? title : LocalizationUtils.Format(LocalizationKey.COLLECTION_TITLE_BODY, title, string.Join(", ", parts));
    }

    private void Open(DeckStatsOverlayController overlay)
    {
        m_overlay = overlay;
        if (AccessibilityMgr.IsFocused(this)) OnGainedFocus();
        else AccessibilityMgr.ShowUI(this);
    }

    [HarmonyPatch(typeof(DeckStatsOverlayController), "OnActivate", new System.Type[] { })]
    private static class OnActivateHook
    {
        private static void Postfix(DeckStatsOverlayController __instance) => s_instance.Open(__instance);
    }

    [HarmonyPatch(typeof(OverlayScreenGroup), "OnDeActivate")]
    private static class OnDeActivateHook
    {
        private static void Postfix(OverlayScreenGroup __instance)
        {
            if (s_instance.m_overlay != null && __instance == s_instance.m_overlay) s_instance.Hide();
        }
    }
}
