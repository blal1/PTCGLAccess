using System.Collections.Generic;
using HarmonyLib;
using TPCI.Localization;
using UnityEngine;
using UnityEngine.UI;
using static Accessibility.GameReflect;

namespace Accessibility;

// The season reset / "WELCOME TO <season> LADDER" overlay — the big popup with the new league hex, the
// league reward cards and COLLECT REWARDS. It appears right after the tutorial finishes, and on every
// season rollover.
//
// It is NOT a RewardOverlaysController overlay, which is why the reward overlay component never sees it and
// the popup sat on screen unannounced with HOME still holding the keyboard behind it. Its driver is
// SeasonEndRewardsSetter, which calls OverlayManager.OpenOverlay(screenOverlays.seasonResetOverlay)
// directly from its private OpenSeasonResetOverlay(). Three flows reach it (SeasonEndRewardsSetter):
//   • season ended with rewards  → CONTINUE grants them (→ item-grant reveal)
//   • season ended, no rewards   → CONTINUE starts the new season
//   • new season / first login   → CheckGrantLadderRewards fills in the ladder reward and COLLECT REWARDS
// All three show the same overlay with one live button (`continueButton`), whose label the game sets
// from `buttonLoc`. So: announce what the overlay says, present that button, done.
//
// Close: the button's own handlers call OverlayManager.CloseOverlay(). There's no close hook to patch,
// so we let go when OverlayManager.currentOverlay is no longer ours (activeInHierarchy is unreliable for
// OverlayManager-closed overlays).
internal sealed class AccessibleSeasonReset : AccessibleUI
{
    private static readonly AccessibleSeasonReset s_instance = new();

    private readonly KeyboardNavigator m_nav;
    private SeasonEndRewardsSetter? m_setter;

    private AccessibleSeasonReset()
    {
        m_nav = new KeyboardNavigator(this) { AllowTab = false };
    }

    public void HandleInput()
    {
        if (m_setter == null || !PopupUtils.IsCurrentOverlay(m_setter.screenOverlays?.seasonResetOverlay))
        {
            m_setter = null;
            AccessibilityMgr.HideUI(this);
            return;
        }
        m_nav.HandleInput();
    }

    public string GetHelp() => LocalizationUtils.Get(LocalizationKey.POPUP_BUTTONS_HELP);

    public void OnGainedFocus()
    {
        if (m_setter == null) return;
        // The overlay's one live control. The game re-wires continueButton.onClick per flow (grant
        // rewards / start the new season), so Enter on it is exactly the sighted player's click.
        var button = ReadField<Button>(m_setter, "continueButton");
        var items = new List<Selectable>();
        if (button != null && button.gameObject.activeInHierarchy) items.Add(button);
        m_nav.SetItems(items);

        var title = OverlayTitle();
        if (title != null) AccessibilityMgr.Output(this, title, interrupt: true);
        if (items.Count > 0) m_nav.FocusFirst(announce: true, interruptPrior: title == null);
        else AccessibilityMgr.Output(this, LocalizationUtils.Get(LocalizationKey.POPUP_SEASON_REWARDS), interrupt: title == null);
    }

    // Everything the popup shows, in reading order: "WELCOME TO", the season name, your new trophy
    // count, the league reward heading, and the reward cards themselves. All read live from the game's
    // own LocTexts — the season name and league names are localized ids we must never bake in.
    private string? OverlayTitle()
    {
        var parts = new List<string>();
        void Add(string? s) { if (!string.IsNullOrWhiteSpace(s)) parts.Add(s!.Trim()); }

        Add(LocLine(ReadField<LocText>(m_setter, "otherLoc")));                   // "WELCOME TO"
        Add(LocLine(ReadField<LocText>(m_setter, "titleLoc")));                   // "JUNE 2026 LADDER"
        Add(PopupUtils.Text(ReadField<Component>(m_setter, "leagueNamePlate")));  // the new league's name
        Add(LocLine(ReadField<LocText>(m_setter, "seasonRankNewTrophyCount")));   // trophies carried into the new season
        Add(LocLine(ReadField<LocText>(m_setter, "ladderLoc")));                  // "<league> LEAGUE REWARD"
        Add(AccessibleReward.SummarizeRewards(ReadField<Transform>(m_setter, "rewardHolder")));
        return parts.Count > 0 ? string.Join(", ", parts) : null;
    }

    // LocText.CurrentText is the already-formatted display string (the season name is substituted in).
    // Skip it when the game has the containing group hidden — the same overlay serves the season-end
    // variant, where different groups are active.
    private static string? LocLine(LocText? loc) => loc != null && loc.gameObject.activeInHierarchy ? loc.CurrentText : null;

    [HarmonyPatch(typeof(SeasonEndRewardsSetter), "OpenSeasonResetOverlay")]
    private static class OpenHook
    {
        private static void Postfix(SeasonEndRewardsSetter __instance)
        {
            s_instance.m_setter = __instance;
            PopupUtils.ShowOrRefresh(s_instance);
        }
    }
}
