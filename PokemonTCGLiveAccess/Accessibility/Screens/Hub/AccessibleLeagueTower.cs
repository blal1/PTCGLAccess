using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RainierClientSDK.source.SeasonRank;
using SharedLogicUtils.source.DataTypes.RankProgression.DataObjects;
using TPCI.Localization;
using UnityEngine.UI;
using static Accessibility.GameReflect;

namespace Accessibility;

// League Tower — the full rank ladder, reached by activating the HOME rank hex (Btn_Rank →
// MainMenuController.OpenLeagueTower). Driver: HUBLeagueTowerController (a HUBScreenController, so the lifecycle
// hook is the usual OnActivate(HUBGroupController) / OnDeactivate, like Profile/Shop).
//
// Sections, visual top-to-bottom:
//   • Top    — the HUB tab strip (HOME / DECKS / …) + ⋮.
//   • Header — back, season info (ⓘ → info popup), LADDER END REWARDS. Entering the screen also reads a summary:
//              season name, time left, and the rank status.
//   • Ladder — the rank tiers, grouped into leagues. Navigated as DATA computed from the season config
//              (leagueData → rankData), not the instantiated tower elements: each reads
//              "<league>, <rank>, <N> points, achieved/current/locked". Enter opens that league's reward overlay via
//              the controller's own OpenLeagueRewardOverlay.
//
// Escape leaves to HOME (MainMenuController.OpenPlayScreen — what the back arrow does). The ladder is a horizontal row
// in landscape, so Left/Right move between tiers; default focus lands on the current rank.
internal sealed class AccessibleLeagueTower : AccessibleScreen
{
    private enum Section { Top, Header, Ladder }

    private sealed class Tier
    {
        public int LeagueIndex;
        public string League = "";
        public string Rank = "";
        public uint Threshold;
        public LocalizationKey Status = LocalizationKey.HUB_LOCKED;
    }

    private static readonly AccessibleLeagueTower s_instance = new();

    private HUBLeagueTowerController? m_controller;
    private Section m_section = Section.Ladder;
    private readonly KeyboardNavigator m_topNav;
    private readonly KeyboardNavigator m_headerNav;
    private readonly List<Tier> m_tiers = new();
    private int m_tierIndex = -1;
    private bool m_fresh;

    private AccessibleLeagueTower()
    {
        m_topNav = new KeyboardNavigator(this);
        m_headerNav = new KeyboardNavigator(this);
    }

    [HarmonyPatch(typeof(HUBLeagueTowerController), "OnActivate")]
    private static class ActivateHook
    {
        private static void Postfix(HUBLeagueTowerController __instance)
        {
            s_instance.m_controller = __instance;
            s_instance.m_fresh = true;
            AccessibilityMgr.SetScreen(s_instance);
        }
    }

    [HarmonyPatch(typeof(HUBLeagueTowerController), "OnDeactivate")]
    private static class DeactivateHook
    {
        private static void Postfix(HUBLeagueTowerController __instance)
        {
            if (s_instance.m_controller != __instance) return;
            s_instance.m_controller = null;
            AccessibilityMgr.ClearScreen(s_instance);
        }
    }

    public string GetHelp() =>
        LocalizationUtils.Format(LocalizationKey.HUB_HELP_WITH_GLOBAL_FORMAT, LocalizationUtils.Get(LocalizationKey.HUB_LEAGUE_TOWER_HELP), LocalizationUtils.Get(LocalizationKey.HUB_GLOBAL_KEYS_HELP));

    // First entry: summary, then the current rank. Afterwards (popup closed, window back): the focused item, positions kept.
    public void OnGainedFocus()
    {
        var topIndex = m_topNav.Index;
        var headerIndex = m_headerNav.Index;
        Rebuild();
        if (m_fresh)
        {
            m_fresh = false;
            m_tierIndex = CurrentTier();
            AccessibilityMgr.Output(this, Summary(), interrupt: true);
            EnterSection(m_tiers.Count > 0 ? Section.Ladder : Section.Header, interruptPrior: false);
            return;
        }
        if (topIndex >= 0 && topIndex < m_topNav.Count) m_topNav.FocusIndex(topIndex, announce: false);
        if (headerIndex >= 0 && headerIndex < m_headerNav.Count) m_headerNav.FocusIndex(headerIndex, announce: false);
        AccessibilityMgr.Output(this, LocalizationUtils.Get(LocalizationKey.HUB_LEAGUE_TOWER), interrupt: true);
        switch (m_section)
        {
            case Section.Top: m_topNav.RefocusCurrent(announce: true, interrupt: false); break;
            case Section.Header: m_headerNav.RefocusCurrent(announce: true, interrupt: false); break;
            default: AnnounceTier(interrupt: false); break;
        }
    }

    public void HandleInput()
    {
        if (m_controller == null || !m_controller.gameObject.activeInHierarchy)
        {
            AccessibilityMgr.ClearScreen(this);
            return;
        }
        // Escape / Backspace = back to HOME (the back arrow's own MainMenuController.OpenPlayScreen).
        if (AccessibleKey.ESCAPE.IsPressed() || AccessibleKey.BACK.IsPressed())
        {
            ManagerSingleton<RainierManager>.instance._mainMenuController.OpenPlayScreen();
            return;
        }
        if (AccessibleKey.NEXT_SECTION.IsPressed()) CycleSection(+1);
        else if (AccessibleKey.PREV_SECTION.IsPressed()) CycleSection(-1);
        else if (m_section == Section.Top) StripInput(m_topNav, null, Section.Header);
        else if (m_section == Section.Header) StripInput(m_headerNav, Section.Top, Section.Ladder);
        else LadderInput();
    }

    #region Discovery

    private void Rebuild()
    {
        if (m_controller == null) return;

        var top = HubUi.TopStrip();
        m_topNav.SetItems(top, UiLabels.TopNavLabel);
        m_topNav.ForceFlatHorizontal();

        // Header chrome buttons: every active Button under the controller that ISN'T a HUB-top-strip button and ISN'T
        // a per-tier reward button (those live inside a LeagueRankGroupParts and belong to the ladder). Leaves the
        // season-info ⓘ, LADDER END REWARDS, snap-to-current, and the back arrow. Back also works via Escape, so a
        // missed/odd label here is never a dead end.
        var topSet = new HashSet<Selectable>(top);
        var header = m_controller.GetComponentsInChildren<Button>(includeInactive: false)
            .Where(b => b.isActiveAndEnabled && !topSet.Contains(b) && b.GetComponentInParent<LeagueRankGroupParts>() == null)
            .Cast<Selectable>()
            .OrderBy(s => HubUi.ReadingOrder(s.transform))
            .ToList();
        m_headerNav.GroupName = LocalizationUtils.Get(LocalizationKey.HUB_LEAGUE_OPTIONS);
        m_headerNav.SetItems(header, s => HubUi.IconButtonLabel(s, LocalizationKey.HUB_SEASON_INFO));
        // The header is a left-to-right strip whose buttons sit at slightly different heights, so force a flat
        // horizontal row — otherwise it auto-detects as a 1-column list and Left/Right can't reach past the first item.
        m_headerNav.ForceFlatHorizontal();

        RebuildLadder();
    }

    // The ladder tiers, computed from the season config (every league's every rank), in the order the game lays them
    // out left-to-right. Status comes from the player's current rank: the exact RankData instance GetRankByEXP returns
    // is the "current rank" (reference match), ranks at/below it are "achieved", the rest "locked" — mirroring
    // PopulateLeagueTower's rankAchieved test.
    private void RebuildLadder()
    {
        m_tiers.Clear();
        var cfg = HUBLeagueTowerController.seasonConfigContent?.leagueConfigContent;
        var leagues = cfg?.leagueData;
        if (cfg == null || leagues == null) return;

        RankData? current = null;
        try { current = cfg.GetRankByEXP(SeasonRank.exp); }
        catch { /* season data not loaded yet: every tier reads locked */ }

        for (var li = 0; li < leagues.Count; li++)
        {
            var league = leagues[li];
            if (league?.rankData == null) continue;
            var leagueName = LocalizationUtils.TryGetGame(league.leagueName)?.Trim() ?? "";
            foreach (var rank in league.rankData)
            {
                if (rank == null) continue;
                m_tiers.Add(new Tier
                {
                    LeagueIndex = li,
                    League = leagueName,
                    Rank = LocalizationUtils.TryGetGame(rank.rankName)?.Trim() ?? "",
                    Threshold = rank.expLimit,
                    Status = current == null ? LocalizationKey.HUB_LOCKED
                        : ReferenceEquals(rank, current) ? LocalizationKey.HUB_CURRENT_RANK
                        : rank.expLimit <= current.expLimit ? LocalizationKey.HUB_ACHIEVED
                        : LocalizationKey.HUB_LOCKED,
                });
            }
        }
        if (m_tierIndex >= m_tiers.Count) m_tierIndex = m_tiers.Count - 1;
    }

    private int CurrentTier() => Math.Max(0, m_tiers.FindIndex(t => t.Status == LocalizationKey.HUB_CURRENT_RANK));

    #endregion

    #region Sections

    private bool SectionHasContent(Section s) => s switch
    {
        Section.Top => m_topNav.Count > 0,
        Section.Header => m_headerNav.Count > 0,
        _ => m_tiers.Count > 0,
    };

    private void CycleSection(int dir)
    {
        for (var step = 1; step <= 3; step++)
        {
            var cand = (Section)((((int)m_section + dir * step) % 3 + 3) % 3);
            if (SectionHasContent(cand))
            {
                EnterSection(cand, interruptPrior: true);
                return;
            }
        }
    }

    private void EnterSection(Section s, bool interruptPrior)
    {
        if (!SectionHasContent(s)) s = SectionHasContent(Section.Ladder) ? Section.Ladder : Section.Header;
        m_section = s;
        switch (s)
        {
            case Section.Top: m_topNav.FocusFirst(announce: true, interruptPrior); break;
            case Section.Header: m_headerNav.FocusFirst(announce: true, interruptPrior); break;
            default: AnnounceTier(interruptPrior); break;
        }
    }

    // Horizontal strip (top nav / header): Left/Right within; Up/Down escape to the adjacent section.
    private void StripInput(KeyboardNavigator nav, Section? up, Section? down)
    {
        if (AccessibleKey.READ_PREV_MENU_OPTION.IsPressed())
        {
            if (up.HasValue) EnterSection(up.Value, interruptPrior: true);
        }
        else if (AccessibleKey.READ_NEXT_MENU_OPTION.IsPressed())
        {
            if (down.HasValue) EnterSection(down.Value, interruptPrior: true);
        }
        else nav.HandleInput();
    }

    #endregion

    #region Ladder (data nav)

    private void LadderInput()
    {
        if (AccessibleKey.READ_NEXT_ITEM.IsPressed()) MoveTier(+1);
        else if (AccessibleKey.READ_PREV_ITEM.IsPressed()) MoveTier(-1);
        else if (AccessibleKey.READ_FIRST_ITEM.IsPressed()) MoveTier(-m_tierIndex);
        else if (AccessibleKey.READ_LAST_ITEM.IsPressed()) MoveTier(m_tiers.Count - 1 - m_tierIndex);
        else if (AccessibleKey.READ_PREV_MENU_OPTION.IsPressed())
        {
            if (SectionHasContent(Section.Header)) EnterSection(Section.Header, interruptPrior: true);
            else if (SectionHasContent(Section.Top)) EnterSection(Section.Top, interruptPrior: true);
        }
        // Open the focused tier's league reward overlay via the controller's own method. The game only wires this
        // button on each league's first rank, but the call is per-league, so it's meaningful from any tier.
        else if (AccessibleKey.CONFIRM.IsPressed() && m_controller != null && m_tierIndex >= 0 && m_tierIndex < m_tiers.Count)
            m_controller.OpenLeagueRewardOverlay(m_tiers[m_tierIndex].LeagueIndex);
    }

    private void MoveTier(int delta)
    {
        var next = m_tierIndex + delta;
        if (next < 0 || next >= m_tiers.Count) return;   // no wrap
        m_tierIndex = next;
        AnnounceTier(interrupt: true);
    }

    private void AnnounceTier(bool interrupt)
    {
        if (m_tierIndex < 0 || m_tierIndex >= m_tiers.Count) return;
        var t = m_tiers[m_tierIndex];
        var label = new List<string>(4);
        if (t.League.Length > 0) label.Add(t.League);
        if (t.Rank.Length > 0) label.Add(t.Rank);
        label.Add(t.Threshold == 1 ? LocalizationUtils.Get(LocalizationKey.RANK_ONE_POINT) : LocalizationUtils.Format(LocalizationKey.RANK_POINTS, t.Threshold));
        label.Add(LocalizationUtils.Get(t.Status));
        AccessibilityMgr.Output(this, Announce.Item(string.Join(", ", label), null, m_tierIndex + 1, m_tiers.Count), interrupt);
    }

    #endregion

    // Screen name + season name + time left + the rank status (the same as the HOME rank hex).
    private string Summary()
    {
        var parts = new List<string> { LocalizationUtils.Get(LocalizationKey.HUB_LEAGUE_TOWER) };
        if (m_controller != null)
        {
            if (HubUi.Text(m_controller.seasonTitleText) is { } title) parts.Add(title);
            var timer = m_controller.seasonTimer;
            if (timer != null && HubUi.Text(ReadField<LocText>(timer, "timeLeftText")) is { } timeLeft) parts.Add(timeLeft);
        }
        if (RankReader.Status() is { Length: > 0 } rank) parts.Add(rank);
        return string.Join(". ", parts);
    }
}
