using System;
using System.Collections.Generic;
using System.Linq;
using _Rainier.Features.BuildAndBattle;
using HarmonyLib;
using SharedLogicUtils.source.DataTypes;
using TMPro;
using TPCI.Rainier.UI.DisplayModules.TimeRemaining;
using TPCI.RainierSDK.BuildAndBattleFeature.DataTypes;
using UnityEngine;
using UnityEngine.UI;
using static Accessibility.GameReflect;

namespace Accessibility;

// The Build & Battle RUN LOBBY — the hex-path event map you land on after buying in (driver HUBBnbLobbyRunController,
// loaded as its own root scene by BuildAndBattleLobbySpawner, replacing the HUB). Two sections:
//   • the buttons: PLAY (StartNextMatch), EDIT DECK, START NEW RUN / COLLECT REWARDS (when the run is done), plus the top
//     bar's home / info / tips / settings;
//   • the map: one row per match plus the prize pot. Nodes aren't Selectables (BnBMapNodeAnimatorDisplay is a pointer
//     handler), so these rows call what a click calls: ShowRewardsInfoUiForNode and HandlePrizePotClick.
// Entry reads the header + match progress + the "ends in" timer + why PLAY is unavailable.
//
// The previous mod polled for the controller every frame (no OnActivate: it's a plain MonoBehaviour). Its Start
// coroutine sets every text, then calls ShowPlayButtonState synchronously, so that call is the "lobby is up" hook;
// OnDestroy (the scene unloading) is the close.
internal sealed class AccessibleBnbLobby : AccessibleScreen
{
    private static readonly AccessibleBnbLobby s_instance = new();

    private readonly KeyboardNavigator m_nav;
    private readonly DataListSection m_map;
    private readonly SectionStack m_stack = new();
    private HUBBnbLobbyRunController? m_controller;
    private BnBLobbySceneViewBindings? m_bindings;
    private string m_lastButtonSig = "";
    private bool m_lastDeckInvalid;
    private string? m_pendingResult;

    private AccessibleBnbLobby()
    {
        m_nav = new KeyboardNavigator(this);
        m_map = new DataListSection(this, LocalizationKey.HUB_BNB_MAP, MapRowCount, MapRowLabel, ActivateMapRow);
    }

    [HarmonyPatch(typeof(HUBBnbLobbyRunController), "ShowPlayButtonState")]
    private static class ShowHook
    {
        private static void Postfix(HUBBnbLobbyRunController __instance)
        {
            s_instance.m_controller = __instance;
            s_instance.m_bindings = UnityEngine.Object.FindAnyObjectByType<BnBLobbySceneViewBindings>();
            AccessibilityMgr.SetScreen(s_instance);
        }
    }

    [HarmonyPatch(typeof(HUBBnbLobbyRunController), "OnDestroy")]
    private static class DestroyHook
    {
        private static void Postfix(HUBBnbLobbyRunController __instance)
        {
            if (s_instance.m_controller != __instance) return;
            s_instance.m_controller = null;
            AccessibilityMgr.ClearScreen(s_instance);
        }
    }

    // The lobby is its own scene, so nothing else ever restores it: a component opened inside it that clears the screen
    // on close (the deck editor's AccessibleEditDeck) used to leave no screen at all, with a dead keyboard until the next
    // scene. This covers that and any future case of the same kind.
    [AccessibilityPoller]
    private static void RestoreWhenOrphaned()
    {
        var c = s_instance.m_controller;
        if (c != null && AccessibilityMgr.CurrentScreen == null && c.gameObject.activeInHierarchy) AccessibilityMgr.SetScreen(s_instance);
    }

    // The match just played: the game only animates this on the map. Spoken once the lobby has focus.
    [HarmonyPatch(typeof(BnBLobbySceneViewBindings), "AnimatePostMatchResults")]
    private static class PostMatchHook
    {
        private static void Postfix(int node, bool wonMatch)
        {
            var result = LocalizationUtils.Format(wonMatch ? LocalizationKey.HUB_BNB_RESULT_WON_FORMAT : LocalizationKey.HUB_BNB_RESULT_LOST_FORMAT, node + 1);
            try
            {
                var items = ManagerSingleton<BnbDataManager>.instance.GetEarnedRewardsForNode(node);
                if (RewardNames(items) is { Length: > 0 } rewards) result += " " + LocalizationUtils.Format(LocalizationKey.HUB_REWARD_FORMAT, rewards);
            }
            catch (Exception e) { Log.Warn($"BnB lobby: earned rewards unreadable ({e.Message})"); }
            s_instance.m_pendingResult = result;
        }
    }

    public static string RewardNames(IEnumerable<ItemData> items) =>
        string.Join(", ", items.Select(i => i.amount > 1 ? LocalizationUtils.Format(LocalizationKey.HUB_TIMES_FORMAT, AccessibleReward.ItemName(i), i.amount) : AccessibleReward.ItemName(i)));

    public string GetHelp() => LocalizationUtils.Get(LocalizationKey.HUB_BNB_LOBBY_HELP);

    public void OnGainedFocus()
    {
        Rebuild();
        m_lastButtonSig = ButtonSig();
        m_lastDeckInvalid = DeckInvalid();
        if (m_controller == null) return;
        var parts = new List<string> { LocalizationUtils.Get(LocalizationKey.HUB_BNB_LOBBY) };
        if (HubUi.Text(ReadField<TextMeshProUGUI>(m_controller, "lobbyHeaderText")) is { } header) parts.Add(header);
        if (ProgressText() is { } progress) parts.Add(progress);
        if (TimerText() is { } timer) parts.Add(timer);
        if (HubUi.Text(ReadField<TextMeshProUGUI>(m_controller, "completedText")) is { } completed) parts.Add(completed);
        if (DeckInvalid()) parts.Add(LocalizationUtils.Get(LocalizationKey.HUB_BNB_DECK_INVALID));
        AccessibilityMgr.Output(this, string.Join(". ", parts), interrupt: true);
        if (m_stack.Current == m_map) m_map.RefocusCurrent(announce: true, interrupt: false);
        else m_nav.FocusFirst(announce: true, interruptPrior: false);
    }

    public void HandleInput()
    {
        if (m_controller == null || !m_controller.gameObject.activeInHierarchy)
        {
            AccessibilityMgr.ClearScreen(this);
            return;
        }
        if (m_pendingResult is { } result)
        {
            m_pendingResult = null;
            AccessibilityMgr.Output(this, result, interrupt: false);
        }
        // Buttons swap as the run state changes (PLAY → START NEW RUN / COLLECT REWARDS when finished); re-present
        // when the set changes, keeping the position, and say what just appeared: that's the next step of the run.
        var sig = ButtonSig();
        var invalid = DeckInvalid();
        if (sig != m_lastButtonSig || invalid != m_lastDeckInvalid)
        {
            var old = m_lastButtonSig.Split(',');
            m_lastButtonSig = sig;
            if (invalid != m_lastDeckInvalid)
            {
                m_lastDeckInvalid = invalid;
                AccessibilityMgr.Output(this, LocalizationUtils.Get(invalid ? LocalizationKey.HUB_BNB_DECK_INVALID : LocalizationKey.HUB_BNB_DECK_VALID), interrupt: false);
            }
            var i = m_nav.Index;
            Rebuild();
            m_nav.FocusIndex(Mathf.Clamp(i, 0, Mathf.Max(m_nav.Count - 1, 0)), announce: false);
            foreach (var b in LobbyButtons().Where(b => !old.Contains(b.gameObject.name)))
                AccessibilityMgr.Output(this, LobbyLabel(b), interrupt: false);
            return;
        }
        m_stack.HandleInput();
    }

    private void Rebuild()
    {
        var keep = m_stack.Current;
        m_nav.SetItems(LobbyButtons().Cast<Selectable>().OrderBy(s => HubUi.ReadingOrder(s.transform)), LobbyLabel);
        m_nav.ForceFlatVertical();   // a small set of actions — predictable Up/Down through all of them
        m_stack.Set(new INavSection[] { m_nav, m_map }, keep);
    }

    // Active, interactable buttons of the lobby canvas: the PLAY / EDIT DECK / START NEW RUN / COLLECT REWARDS actions +
    // the top bar. Not the overlay parent's: lobby popups sit under the same canvas, and without this filter the buttons
    // of any popup would merge silently into this list.
    private List<Button> LobbyButtons()
    {
        if (m_controller == null) return new List<Button>();
        var canvas = m_controller.GetComponentInParent<Canvas>();
        var root = canvas != null ? canvas.rootCanvas.transform : m_controller.transform.root;
        var overlays = ReadField<RectTransform>(m_controller, "overlayParent");
        return root.GetComponentsInChildren<Button>(includeInactive: false)
            .Where(b => b.isActiveAndEnabled && b.IsInteractable() && (overlays == null || !b.transform.IsChildOf(overlays)))
            .ToList();
    }

    private string ButtonSig() => string.Join(",", LobbyButtons().Select(b => b.gameObject.name));

    private bool DeckInvalid() => ReadField<GameObject>(m_controller, "invalidDeckIcon") is { } icon && icon.activeInHierarchy;

    // "<done> of <total> matches" from the run data.
    private static string? ProgressText()
    {
        var mgr = ManagerSingleton<BnbDataManager>.instance;
        if (mgr == null || mgr.eventMatchCount <= 0) return null;
        return LocalizationUtils.Format(LocalizationKey.HUB_BNB_MATCHES_FORMAT, mgr.matchResults?.Count ?? 0, mgr.eventMatchCount);
    }

    private string? TimerText()
    {
        var timer = ReadField<StandaloneTimeLeftDisplay>(m_controller, "timerDisplay");
        if (timer == null) return null;
        var parts = new[] { HubUi.Text(ReadField<TextMeshProUGUI>(timer, "titleText")), HubUi.Text(ReadField<TextMeshProUGUI>(timer, "timeLeftText")) }
            .Where(s => s != null).ToList();
        return parts.Count > 0 ? string.Join(" ", parts) : null;
    }

    // Worded buttons (PLAY, EDIT DECK, …) keep their text; the icon-only top-bar buttons get a name.
    private static string LobbyLabel(Selectable s) => HubUi.IconButtonLabel(s, LocalizationKey.HUB_INFORMATION);

    // ── Map ──

    private static BnbDataManager? Data => ManagerSingleton<BnbDataManager>.instance;

    private static int MapRowCount() => Data != null && Data.eventMatchCount > 0 ? Data.eventMatchCount + 1 : 0;

    private static string MapRowLabel(int row)
    {
        var mgr = Data;
        if (mgr == null) return LocalizationUtils.Get(LocalizationKey.HUB_BNB);
        if (row >= mgr.eventMatchCount)
            return LocalizationUtils.Get(mgr.currentRunStatus == RunStatus.Ended ? LocalizationKey.HUB_BNB_PRIZE_POT_READY : LocalizationKey.HUB_BNB_PRIZE_POT);
        var results = mgr.matchResults;
        var played = results?.Count ?? 0;
        var state = row < played
            ? (results![row] ? LocalizationKey.HUB_BNB_MATCH_WON : LocalizationKey.HUB_BNB_MATCH_LOST)
            : row == played ? LocalizationKey.HUB_BNB_MATCH_NEXT : LocalizationKey.HUB_BNB_MATCH_UPCOMING;
        return LocalizationUtils.Format(LocalizationKey.HUB_BNB_MATCH_ROW_FORMAT, row + 1, LocalizationUtils.Get(state));
    }

    private void ActivateMapRow(int row)
    {
        if (m_bindings == null || Data == null) return;
        if (row >= Data.eventMatchCount) m_bindings.HandlePrizePotClick();
        else m_bindings.ShowRewardsInfoUiForNode(row);
    }
}
