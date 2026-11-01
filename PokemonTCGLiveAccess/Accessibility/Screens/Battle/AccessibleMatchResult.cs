using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using SharedLogicUtils.source.DataTypes;
using TPCI.Localization;
using UnityEngine;
using UnityEngine.UI;
using static Accessibility.GameReflect;

namespace Accessibility;

// End-of-match result screen — driver MatchResultsScreenOverlay. Online / vs-people matches end with
// this animated outro (win/loss splash → victor catchphrase → match stats → rewards → return to main
// menu). It's not a battle prompt and the battle screen has already been cleared, so without this the
// player heard nothing and had no way to advance past the stats page (which waits indefinitely for a
// "tap"). It's the base screen until the HUB replaces it, hence an AccessibleScreen.
//
// We hook Show(...) — it carries `won` + the localized game-end reason — to announce the outcome, and
// drive the screen's own advance: Enter taps through the stages (SetButtonTappedFlag, the same flag a
// click sets) and, once the Return-to-main-menu button is up, presses it (its onClick = Continue).
//
// The rewards page (the "MATCH REWARDS" stage near the end — XP / Battle Pass XP / rank points) has no
// readable label text (the values sit under icon-only hexes), so we also hook MatchRewardView.SetupAndShow
// — the game's own "rewards now shown" call — and read the three count LocTexts (whichever are visible
// for this mode) plus any Build-and-Battle item rewards. CONTINUE on that page is returnToMainMenuButton,
// already driven by Advance().
//
// The match STATS page (the screen with both players' attack-damage / coin-flips panels + CONTINUE /
// BATTLE LOG / ADD FRIEND buttons) is its own stage — MatchResultsStats.ShowStatsPage. We hook that to
// read the stats out loud and present its buttons as a real list (CONTINUE advances, BATTLE LOG opens the
// log, ADD FRIEND sends a request), instead of the blind Enter-tap that drove the other stages.
internal sealed class AccessibleMatchResult : AccessibleScreen
{
    private static readonly AccessibleMatchResult s_instance = new();

    private readonly KeyboardNavigator m_statsNav;
    private MatchResultsScreenOverlay? m_overlay;
    private string m_outcome = "";
    private bool m_onStatsPage;
    private string m_lastStatsButtonSig = "";
    private bool m_returnAnnounced;

    private AccessibleMatchResult()
    {
        m_statsNav = new KeyboardNavigator(this) { AllowTab = false };
    }

    #region Hooks

    [HarmonyPatch(typeof(MatchResultsScreenOverlay), "Show")]
    private static class ShowHook
    {
        private static void Postfix(MatchResultsScreenOverlay __instance, bool won, string gameEndReasonLocID)
        {
            try { s_instance.OnShow(__instance, won, gameEndReasonLocID); }
            catch (Exception ex) { Log.Error($"MatchResult.OnShow threw: {ex.Message}"); }
        }
    }

    // Rewards page: announce the breakdown when the game populates + shows it.
    [HarmonyPatch(typeof(MatchRewardView), "SetupAndShow")]
    private static class RewardsHook
    {
        private static void Postfix(MatchRewardView __instance)
        {
            if (s_instance.m_overlay == null) return;
            try { AccessibilityMgr.OutputNotification(RewardsText(__instance), interrupt: true); }
            catch (Exception ex) { Log.Error($"MatchResult.AnnounceRewards threw: {ex.Message}"); }
        }
    }

    // Stats page: read the stats + present its buttons; revert to Enter-tap when it hides.
    [HarmonyPatch(typeof(MatchResultsStats), "ShowStatsPage")]
    private static class StatsShowHook
    {
        private static void Postfix(MatchResultsStats __instance)
        {
            try { s_instance.OnStatsPage(__instance); }
            catch (Exception ex) { Log.Error($"MatchResult.OnStatsPage threw: {ex.Message}"); }
        }
    }

    [HarmonyPatch(typeof(MatchResultsStats), "HideStatsPage")]
    private static class StatsHideHook
    {
        private static void Postfix() => s_instance.m_onStatsPage = false;
    }

    private void OnShow(MatchResultsScreenOverlay overlay, bool won, string gameEndReasonLocID)
    {
        m_overlay = overlay;
        m_onStatsPage = false;
        m_returnAnnounced = false;
        var reason = LocalizationUtils.TryGetGame(gameEndReasonLocID);
        m_outcome = LocalizationUtils.Get(won ? LocalizationKey.BATTLE_RESULT_WON : LocalizationKey.BATTLE_RESULT_LOST);
        if (!string.IsNullOrWhiteSpace(reason)) m_outcome = LocalizationUtils.Format(LocalizationKey.BATTLE_SENTENCES, m_outcome, reason);
        if (AccessibilityMgr.CurrentScreen == this) AccessibilityMgr.Output(this, m_outcome);
        else AccessibilityMgr.SetScreen(this);
    }

    #endregion

    #region Stats page

    // The stats page appeared: read both players' stats + the outcome, then present its action buttons
    // (CONTINUE / BATTLE LOG / ADD FRIEND) as a navigable list instead of the blind Enter-tap.
    private void OnStatsPage(MatchResultsStats stats)
    {
        if (m_overlay == null) return;
        m_onStatsPage = true;
        var line = StatsAnnouncement(stats);
        if (!string.IsNullOrWhiteSpace(line)) AccessibilityMgr.Output(this, line);
        RebuildStatsButtons();
        m_lastStatsButtonSig = StatsButtonSig();
        if (m_statsNav.Count > 0) FocusStatsDefault(interruptPrior: string.IsNullOrWhiteSpace(line));
    }

    // Land on the SAFE button — the last one (the result screen's primary "move on": Continue / Close)
    // — and never on Add Friend. Focus first landing on Add Friend let an Enter fire a friend request
    // at the opponent by accident. Add Friend stays reachable by arrowing up.
    private void FocusStatsDefault(bool interruptPrior)
    {
        if (m_statsNav.Count == 0) return;
        var addFriend = AddFriendButton();
        int target = m_statsNav.Count - 1;
        while (target > 0 && ReferenceEquals(m_statsNav.Items[target], addFriend)) target--;
        m_statsNav.FocusIndex(target, announce: true, interrupt: interruptPrior);
    }

    // The "send friend request" button on the stats page (AddFriendFromMatchResults._sendRequestButton),
    // so the default focus can avoid it.
    private Button? AddFriendButton()
    {
        if (m_overlay == null) return null;
        try
        {
            var comp = m_overlay.GetComponentInChildren<AddFriendFromMatchResults>(includeInactive: true);
            return comp != null ? ReadField<Button>(comp, "_sendRequestButton") : null;
        }
        catch { return null; }
    }

    private static string StatsAnnouncement(MatchResultsStats stats)
    {
        var parts = new List<string>();
        var header = LocOf(ReadField<LocText>(stats, "headerText"));            // VICTORY / DEFEAT
        if (header != null) parts.Add(header);
        var reason = LocOf(ReadField<LocText>(stats, "gameEndReasonText"));     // "Knocked Out with no Benched Pokémon"
        if (reason != null) parts.Add(reason);
        var p = StatLine(ReadField<Component>(stats, "playerStats"));
        if (p != null) parts.Add(p);
        var o = StatLine(ReadField<Component>(stats, "opponentStats"));
        if (o != null) parts.Add(o);
        return string.Join(". ", parts);
    }

    // "<player name>: <stat label> <value>, <stat label> <value>" for one MatchResultsStatsPage.
    private static string? StatLine(Component? page)
    {
        if (page == null) return null;
        var name = LocOf(ReadField<LocText>(page, "playerNameDisplay"));
        var stat = string.Join(", ", new[]
        {
            StatEntry(ReadField<Component>(page, "totalDamageStat")),
            StatEntry(ReadField<Component>(page, "coinFlipStat")),
        }.Where(s => !string.IsNullOrWhiteSpace(s)));
        if (string.IsNullOrWhiteSpace(name)) return string.IsNullOrWhiteSpace(stat) ? null : stat;
        return string.IsNullOrWhiteSpace(stat) ? name : LocalizationUtils.Format(LocalizationKey.BATTLE_RESULT_PLAYER_STATS, name, stat);
    }

    // "<stat label> <value>" for a StatPageEntry (label + count are both LocTexts).
    private static string? StatEntry(Component? entry)
    {
        if (entry == null) return null;
        var label = LocOf(ReadField<LocText>(entry, "statNameDisplay"));
        var value = LocOf(ReadField<LocText>(entry, "statCountDisplay"));
        if (label == null && value == null) return null;
        return $"{label} {value}".Trim();
    }

    // The stats page's action buttons (CONTINUE / BATTLE LOG / ADD FRIEND), in reading order.
    private void RebuildStatsButtons()
    {
        if (m_overlay == null) return;
        var buttons = ActiveButtons().OrderBy(b => ReadingOrder(b.transform)).Cast<Selectable>().ToList();
        m_statsNav.SetItems(buttons);
        m_statsNav.ForceFlatVertical();   // CONTINUE / BATTLE LOG / ADD FRIEND — a small vertical list
    }

    private IEnumerable<Button> ActiveButtons() => m_overlay == null
        ? Enumerable.Empty<Button>()
        : m_overlay.GetComponentsInChildren<Button>(includeInactive: false).Where(b => b.isActiveAndEnabled && b.IsInteractable());

    private string StatsButtonSig() => string.Join(",", ActiveButtons().Select(b => b.gameObject.name));

    private static string? LocOf(LocText? t)
    {
        var s = t != null ? t.CurrentText : null;
        return string.IsNullOrWhiteSpace(s) ? null : s!.Trim();
    }

    private static float ReadingOrder(Transform t) { var p = t.position; return -p.y * 10000 + p.x; }

    #endregion

    #region Rewards page

    // Read the rewards page. The three XP counts are private SerializeField LocTexts whose own icons carry
    // no words; we name each and read its formatted count ("+200"), including only the tiles actually
    // visible for this mode (rank points show only in Ranked, the Battle-Pass tile only when there's an
    // active challenge). Build-and-Battle item rewards (rare mode) are read by name from the spawned slots
    // so nothing is silently dropped.
    private static string RewardsText(MatchRewardView view)
    {
        var parts = new List<string>();
        AddCount(parts, view, "playerXPCount", LocalizationKey.BATTLE_RESULT_XP);
        AddCount(parts, view, "challengeXPCount", LocalizationKey.BATTLE_RESULT_PASS_XP);
        AddCount(parts, view, "rankXPCount", LocalizationKey.BATTLE_RESULT_RANK_POINTS);

        var bnb = ReadField<Transform>(view, "buildAndBattleRewardsHolder");
        if (bnb != null && bnb.gameObject.activeInHierarchy)
            foreach (var slot in bnb.GetComponentsInChildren<ProductItemSummarySlot>(includeInactive: false))
            {
                var item = ReadField<object>(slot, "currentItem") is ItemData d ? d : default;
                if (item.itemType == ItemData.ItemType.None) continue;
                int n = ReadField<object>(slot, "numberOfItems") is int i && i > 0 ? i : (item.amount > 0 ? item.amount : 1);
                var name = AccessibleReward.ItemName(item);
                if (!string.IsNullOrWhiteSpace(name)) parts.Add(n > 1 ? LocalizationUtils.Format(LocalizationKey.BATTLE_RESULT_ITEM_TIMES, name, n) : name);
            }

        var header = LocalizationUtils.GetGame("match_results_match_rewards", LocalizationKey.BATTLE_RESULT_REWARDS);
        Log.Info($"Match rewards: {string.Join(" | ", parts)}");
        return parts.Count > 0 ? LocalizationUtils.Format(LocalizationKey.BATTLE_SENTENCES, header, string.Join(". ", parts)) : header;
    }

    // Append "<label>, <count>" if that count tile is visible and non-empty. The game formats the count
    // text itself ("+200" / "0"); we read it verbatim so the spoken value matches the screen.
    private static void AddCount(List<string> parts, MatchRewardView view, string field, LocalizationKey label)
    {
        var loc = ReadField<LocText>(view, field);
        if (loc == null || !loc.gameObject.activeInHierarchy) return;
        var text = loc.CurrentText;
        if (string.IsNullOrWhiteSpace(text)) return;
        parts.Add(LocalizationUtils.Format(LocalizationKey.COMMON_STATE_FORMAT, LocalizationUtils.Get(label), text.Trim()));
    }

    #endregion

    #region AccessibleComponent

    public string GetHelp() => LocalizationUtils.Get(LocalizationKey.BATTLE_RESULT_HELP);

    public void OnGainedFocus()
    {
        AccessibilityMgr.Output(this, m_outcome);
        if (m_onStatsPage && m_statsNav.Count > 0) m_statsNav.RefocusCurrent(announce: true, interrupt: false);
    }

    public void HandleInput()
    {
        // The overlay lives on the Match scene; when it unloads (return to menu) the reference dies.
        if (m_overlay == null)
        {
            AccessibilityMgr.ClearScreen(this);
            return;
        }

        // On the stats page, navigate its real buttons (CONTINUE / BATTLE LOG / ADD FRIEND) — they can
        // appear a frame after ShowStatsPage, so re-present + announce when the set first fills in.
        if (m_onStatsPage)
        {
            var sig = StatsButtonSig();
            if (sig != m_lastStatsButtonSig)
            {
                bool hadNone = m_statsNav.Count == 0;
                m_lastStatsButtonSig = sig;
                RebuildStatsButtons();
                if (hadNone && m_statsNav.Count > 0) FocusStatsDefault(interruptPrior: true);
                return;
            }
            if (AccessibleKey.BATTLE_LOG.IsPressed()) { AccessibleBattleLogHistory.Open(ReadField<IBattleLogStateProvider>(m_overlay, "_battleLog")); return; }
            m_statsNav.HandleInput();
            return;
        }

        // When the return-to-game button appears (rewards / final page), announce it once by its real
        // label so the player hears the result is over and what advances — instead of pressing blind and
        // only getting feedback after. Enter still advances through the game's own flow (Advance).
        var rb = ReturnButton();
        bool rbAvail = rb != null && rb.isActiveAndEnabled && rb.IsInteractable();
        if (rbAvail && !m_returnAnnounced) { m_returnAnnounced = true; AccessibilityMgr.Output(this, KeyboardNavigator.DefaultLabel(rb!), interrupt: false); }
        else if (!rbAvail) m_returnAnnounced = false;

        // Other stages (win/loss splash, victor catchphrase, rewards) advance on the game's own tap flag.
        if (AccessibleKey.CONFIRM.IsPressed() || AccessibleKey.SPACE.IsPressed()) Advance();
    }

    #endregion

    // Move the result screen forward: if the Return-to-main-menu button is up, press it (its onClick
    // runs Continue → back to the HUB); otherwise tap through the current stage (the stats page waits
    // on exactly this flag). Both are the game's own advance paths — no parallel transition.
    private void Advance()
    {
        if (m_overlay == null) return;
        try
        {
            var btn = ReturnButton();
            if (btn != null && btn.isActiveAndEnabled && btn.IsInteractable())
            {
                // No speech here: the button was already announced when it appeared, and pressing it
                // transitions to the HUB (which announces itself). Speaking only on press was the bug.
                btn.onClick.Invoke();
                return;
            }
            m_overlay.SetButtonTappedFlag();
        }
        catch (Exception ex) { Log.Error($"MatchResult.Advance threw: {ex.Message}"); }
    }

    private Button? ReturnButton() => m_overlay == null ? null : ReadField<Button>(m_overlay, "returnToMainMenuButton");
}
