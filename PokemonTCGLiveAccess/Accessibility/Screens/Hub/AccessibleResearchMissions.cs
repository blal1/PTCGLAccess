using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using _Rainier.Features.CardDex.ResearchMissions;
using HarmonyLib;
using TPCI.Features.CardDex.ResearchMissions;
using TPCI.Localization;
using TPCI.Rainier.CardDex;
using UnityEngine;
using UnityEngine.UI;
using static Accessibility.GameReflect;

namespace Accessibility;

// RESEARCH MISSIONS — the Card Dex's second tab (HUBResearchMissionsScreenController, a subpage of
// HUBCardDexScreenController). AccessibleCardDex only knows the collection subpage, so this takes over the screen for as
// long as the subpage is active and gives it back afterwards.
//
// The dex's OnActivate activates its subpage BEFORE AccessibleCardDex's postfix, so hooking Activate alone would be
// overwritten: the poller takes over from the dex the frame after, whichever came first. When the subpage deactivates
// because of the tab, the dex stays up with no screen; because of the whole dex closing, the next screen sets itself.
//
// Sections: top strip, tabs, series (Enter opens the in-place list), sets, actions, missions. The mission list reads the
// display's data model rather than its virtualized cells, so all missions are reachable.
internal sealed class AccessibleResearchMissions : AccessibleScreen
{
    private static readonly AccessibleResearchMissions s_instance = new();
    private static readonly FieldInfo? s_seriesOpen = AccessTools.Field(typeof(CardDexSeriesSelector), "isOpen");
    private static readonly FieldInfo? s_carouselData = AccessTools.Field(typeof(CollectionSetCarousel), "_carouselObjectsData");

    private readonly SectionStack m_stack = new();
    private readonly KeyboardNavigator m_top, m_tabs, m_series, m_actions;
    private readonly DataListSection m_sets, m_missions;

    private HUBResearchMissionsScreenController? m_controller;
    private HUBCardDexScreenController? m_dex;
    private AccessibleScreen? m_dexScreen;
    private bool m_returnToDex;
    private string m_lastSig = "";
    private string? m_lastSet;

    private bool m_picking;
    private List<CardDexSeriesObjectsData> m_seriesData = new();
    private int m_seriesPick;

    private AccessibleResearchMissions()
    {
        m_top = new KeyboardNavigator(this) { AllowTab = false };
        m_tabs = new KeyboardNavigator(this) { AllowTab = false, RadioToggleMode = true };
        m_series = new KeyboardNavigator(this) { AllowTab = false };
        m_actions = new KeyboardNavigator(this) { AllowTab = false };
        m_sets = new DataListSection(this, LocalizationKey.COLLECTION_SETS, () => SetData().Count, SetLabel, null, horizontal: true)
        {
            LiveIndex = () => Math.Max(0, SetData().FindIndex(d => d != null && d.setId == HUBCardDexScreenController.LoadedSetId)),
            // ponytail: one step per key, Home and End included; ArrowScroll only documents ±1 (the < > arrows).
            MoveTo = i => ReadField<CollectionSetCarousel>(m_dex, "collectionSetCarousel")?.ArrowScroll(Math.Sign(i - m_sets!.Index)),
        };
        m_missions = new DataListSection(this, LocalizationKey.HUB_RM_MISSIONS, () => Missions().Count, MissionLabel, OpenMission);
        m_stack.OnBack = HubUi.OpenOverflowMenu;
    }

    [HarmonyPatch(typeof(HUBResearchMissionsScreenController), "Activate")]
    private static class ActivateHook
    {
        private static void Postfix(HUBResearchMissionsScreenController __instance)
        {
            s_instance.m_controller = __instance;
            s_instance.m_dex = ReadField<HUBCardDexScreenController>(__instance, "cardDexScreenController");
            s_instance.m_returnToDex = false;
        }
    }

    [HarmonyPatch(typeof(HUBResearchMissionsScreenController), "Deactivate")]
    private static class DeactivateHook
    {
        private static void Postfix(HUBResearchMissionsScreenController __instance)
        {
            if (s_instance.m_controller != __instance) return;
            s_instance.m_controller = null;
            s_instance.m_returnToDex = AccessibilityMgr.CurrentScreen == s_instance;
            AccessibilityMgr.ClearScreen(s_instance);
        }
    }

    [AccessibilityPoller]
    private static void FollowSubpage()
    {
        var self = s_instance;
        if (self.m_controller != null)
        {
            if (AccessibilityMgr.CurrentScreen is AccessibleCardDex dexScreen)
            {
                self.m_dexScreen = dexScreen;
                AccessibilityMgr.SetScreen(self);
            }
        }
        else if (self.m_returnToDex)
        {
            self.m_returnToDex = false;
            if (AccessibilityMgr.CurrentScreen == null && self.m_dexScreen != null && self.m_dex != null && self.m_dex.gameObject.activeInHierarchy)
                AccessibilityMgr.SetScreen(self.m_dexScreen);
        }
    }

    public string GetHelp() => LocalizationUtils.Get(LocalizationKey.HUB_RM_HELP);

    public void OnGainedFocus()
    {
        var fresh = m_stack.Sections.Count == 0;
        Rebuild();
        var parts = new List<string> { LocalizationUtils.Get(LocalizationKey.HUB_RM_TITLE) };
        if (SetName(HUBCardDexScreenController.LoadedSetId) is { } set) parts.Add(set);
        AccessibilityMgr.Output(this, string.Join(", ", parts), interrupt: true);
        if (fresh) m_stack.Focus(m_missions, announce: true, interrupt: false);
        else m_stack.Current?.RefocusCurrent(announce: true, interrupt: false);
    }

    public void HandleInput()
    {
        if (m_controller == null) return;
        if (SeriesOpen()) { SeriesPicker(); return; }
        if (m_picking)
        {
            m_picking = false;
            m_stack.Current?.RefocusCurrent(announce: true, interrupt: true);
        }
        var sig = ContentSig();
        if (sig != m_lastSig)
        {
            Rebuild();
            return;
        }
        if (m_stack.Current == m_series && (AccessibleKey.CONFIRM.IsPressed() || AccessibleKey.SPACE.IsPressed()))
        {
            ReadField<CardDexSeriesSelector>(m_dex, "cardDexSeriesSelector")?.ToggleSelectionMenu();
            return;
        }
        m_stack.HandleInput();
    }

    // ── Sections ──

    private void Rebuild()
    {
        m_lastSig = ContentSig();
        if (m_lastSet != HUBCardDexScreenController.LoadedSetId)
        {
            m_lastSet = HUBCardDexScreenController.LoadedSetId;
            m_missions.SetIndex(0);
        }
        var keep = m_stack.Current;
        var keepIndex = keep is KeyboardNavigator kn ? kn.Index : -1;
        var sections = new List<INavSection>();

        m_top.SetItems(HubUi.TopStrip(), UiLabels.TopNavLabel);
        if (m_top.Count > 0) sections.Add(m_top);

        var tabs = new[] { "cardCollectionTab", "researchMissionsTab" }
            .Select(f => ReadField<PageDragTab>(m_dex, f)).Where(t => t != null && t.toggle != null && t.toggle.isActiveAndEnabled)
            .Select(t => (Selectable)t!.toggle).ToList();
        m_tabs.SetItems(tabs, TabLabel);
        if (m_tabs.Count > 0) sections.Add(m_tabs);

        if (ReadField<Button>(m_dex, "dexSeriesSelectBtn") is { } seriesBtn && seriesBtn.isActiveAndEnabled)
        {
            m_series.SetItems(new List<Selectable> { seriesBtn }, SeriesLabel);
            sections.Add(m_series);
        }
        if (SetData().Count > 0) sections.Add(m_sets);

        var actions = new[]
        {
            ReadField<Button>(m_controller, "collectAllButton"),
            ReadField<Button>(m_controller, "gotoNextCollectableMissionButton"),
            ReadField<Button>(m_dex, "collectorStatsButton"),
        }.Where(b => b != null && b.isActiveAndEnabled).Cast<Selectable>().ToList();
        m_actions.SetItems(actions, ActionLabel);
        if (m_actions.Count > 0) sections.Add(m_actions);

        sections.Add(m_missions);
        m_stack.Set(sections, keep);
        if (keep is KeyboardNavigator nav && nav.Count > 0 && keepIndex >= 0) nav.FocusIndex(Math.Min(keepIndex, nav.Count - 1), announce: false);
    }

    private string ContentSig()
    {
        var actions = string.Concat(new[] { "collectAllButton", "gotoNextCollectableMissionButton" }
            .Select(f => ReadField<Button>(m_controller, f) is { } b && b.isActiveAndEnabled ? "1" : "0"));
        return HUBCardDexScreenController.LoadedSeriesId + "|" + HUBCardDexScreenController.LoadedSetId + "|" + actions + "|"
            + string.Concat(Missions().Select(m => (int)m.State));
    }

    private List<ResearchMissionsListObject.ResearchMissionUIModel> Missions() =>
        ReadField<List<ResearchMissionsListObject.ResearchMissionUIModel>>(ReadField<ResearchMissionsListDisplay>(m_controller, "researchMissionsListDisplay"), "_missionListData")
        ?? new List<ResearchMissionsListObject.ResearchMissionUIModel>();

    private void OpenMission(int i)
    {
        var missions = Missions();
        if (i < missions.Count) m_controller?.ShowMissionDetailsPopup(missions[i].MissionId);
    }

    // ── Labels ──

    public static string StateWord(MissionState state) => LocalizationUtils.Get(state switch
    {
        MissionState.Completed => LocalizationKey.HUB_READY_TO_COLLECT,
        MissionState.RewardsRedeemed => LocalizationKey.HUB_CLAIMED,
        _ => LocalizationKey.HUB_RM_IN_PROGRESS,
    });

    private static string? DifficultyWord(MissionDifficulty d) => d switch
    {
        MissionDifficulty.Easy => LocalizationUtils.Get(LocalizationKey.HUB_RM_EASY),
        MissionDifficulty.Medium => LocalizationUtils.Get(LocalizationKey.HUB_RM_MEDIUM),
        MissionDifficulty.Hard => LocalizationUtils.Get(LocalizationKey.HUB_RM_HARD),
        MissionDifficulty.ExtraHard => LocalizationUtils.Get(LocalizationKey.HUB_RM_EXTRA_HARD),
        _ => null,
    };

    private string MissionLabel(int i)
    {
        var missions = Missions();
        if (i >= missions.Count) return LocalizationUtils.Get(LocalizationKey.HUB_EMPTY);
        var m = missions[i];
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(m.TitleText)) parts.Add(Clean(m.TitleText));
        if (DifficultyWord(m.Difficulty) is { } difficulty) parts.Add(difficulty);
        if (!string.IsNullOrWhiteSpace(m.DescriptionText)) parts.Add(Clean(m.DescriptionText));
        var total = m.MissionObjectiveCards?.Count ?? 0;
        var mgr = ManagerSingleton<ResearchMissionsDataManager>.instance;
        if (total > 0 && mgr != null) parts.Add(LocalizationUtils.Format(LocalizationKey.COLLECTION_COLLECTED, mgr.GetCompletedObjectiveCountForMission(m.MissionId), total));
        parts.Add(StateWord(m.State));
        return string.Join(", ", parts);
    }

    private static string Clean(string s) => ShopLabels.StripTags(CardText.SpeakIcons(s)).Trim();

    private List<CollectionCarouselObjectsData> SetData() =>
        s_carouselData?.GetValue(ReadField<CollectionSetCarousel>(m_dex, "collectionSetCarousel")) as List<CollectionCarouselObjectsData> ?? new List<CollectionCarouselObjectsData>();

    private static string? SetName(string? id) => CardDexText.Loc("tcg_" + (id ?? "").ToLowerInvariant());

    private string SetLabel(int i)
    {
        var data = SetData();
        if (i >= data.Count) return LocalizationUtils.Get(LocalizationKey.COLLECTION_SETS);
        var name = SetName(data[i].setId) ?? data[i].setId ?? LocalizationUtils.Get(LocalizationKey.COLLECTION_SETS);
        var mgr = ManagerSingleton<ResearchMissionsDataManager>.instance;
        var ready = mgr != null ? mgr.GetCompletedMissionsPerSet(data[i].setId).Count() : 0;
        return ready > 0 ? name + ", " + LocalizationUtils.Format(LocalizationKey.HUB_RM_REWARDS_READY_FORMAT, ready) : name;
    }

    private static string TabLabel(Selectable s)
    {
        var tab = s.GetComponent<PageDragTab>() ?? s.GetComponentInParent<PageDragTab>();
        return LocalizationUtils.TryGetGame(tab != null ? tab.GetLocTextID() : null) ?? KeyboardNavigator.DefaultLabel(s);
    }

    private static string SeriesLabel(Selectable s) =>
        LocalizationUtils.Get(LocalizationKey.COLLECTION_SERIES) + ": " + (SetName(HUBCardDexScreenController.LoadedSeriesId) ?? KeyboardNavigator.DefaultLabel(s));

    private string ActionLabel(Selectable s) =>
        s == ReadField<Button>(m_dex, "collectorStatsButton") && !HubUi.HasLetterText(s)
            ? LocalizationUtils.Get(LocalizationKey.HUB_RM_COLLECTOR_STATS)
            : HubUi.IconButtonLabel(s, LocalizationKey.HUB_INFORMATION);

    // ── Series list (in-place panel, as in AccessibleCardDex) ──

    private bool SeriesOpen() => s_seriesOpen?.GetValue(ReadField<CardDexSeriesSelector>(m_dex, "cardDexSeriesSelector")) is true;

    private void SeriesPicker()
    {
        var selector = ReadField<CardDexSeriesSelector>(m_dex, "cardDexSeriesSelector");
        if (!m_picking)
        {
            m_picking = true;
            m_seriesData = ReadField<Dictionary<string, CardDexSeriesObjectsData>>(selector, "_cardDexSeriesObjectsData")?.Values.Where(v => v != null).ToList() ?? new();
            m_seriesPick = Math.Max(0, m_seriesData.FindIndex(e => e.seriesId == HUBCardDexScreenController.LoadedSeriesId));
            AccessibilityMgr.Output(this, LocalizationUtils.Get(LocalizationKey.COLLECTION_SERIES), interrupt: true);
            SpeakSeriesPick(interrupt: false);
        }
        if (AccessibleKey.ESCAPE.IsPressed() || AccessibleKey.BACK.IsPressed()) selector?.CloseSeriesOverlay();
        else if (AccessibleKey.READ_NEXT_MENU_OPTION.IsPressed()) { if (m_seriesPick < m_seriesData.Count - 1) { m_seriesPick++; SpeakSeriesPick(true); } }
        else if (AccessibleKey.READ_PREV_MENU_OPTION.IsPressed()) { if (m_seriesPick > 0) { m_seriesPick--; SpeakSeriesPick(true); } }
        else if (AccessibleKey.CONFIRM.IsPressed() && m_seriesPick < m_seriesData.Count)
        {
            var e = m_seriesData[m_seriesPick];
            e.onSelectSeries?.Invoke(e.seriesProgress, e.seriesId);
        }
    }

    private void SpeakSeriesPick(bool interrupt)
    {
        if (m_seriesPick >= m_seriesData.Count) return;
        var e = m_seriesData[m_seriesPick];
        AccessibilityMgr.Output(this, Announce.Item(SetName(e.seriesId) ?? e.seriesId, null, m_seriesPick + 1, m_seriesData.Count), interrupt);
    }
}
