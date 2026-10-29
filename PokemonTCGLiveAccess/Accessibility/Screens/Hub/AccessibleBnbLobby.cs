using System.Collections.Generic;
using System.Linq;
using _Rainier.Features.BuildAndBattle;
using HarmonyLib;
using TMPro;
using TPCI.Rainier.UI.DisplayModules.TimeRemaining;
using UnityEngine;
using UnityEngine.UI;
using static Accessibility.GameReflect;

namespace Accessibility;

// The Build & Battle RUN LOBBY — the hex-path event map you land on after buying in (driver HUBBnbLobbyRunController,
// loaded as its own root scene by BuildAndBattleLobbySpawner, replacing the HUB). The hex path is a visual
// match-progression track (you don't navigate nodes individually — PLAY starts the next match), so the actionable
// surface is the buttons: PLAY (StartNextMatch), EDIT DECK, START NEW RUN / COLLECT REWARDS (when the run is done),
// plus the top bar's home / info / settings. Entry reads the header + match progress (BnbDataManager match count) +
// the "ends in" timer.
//
// The previous mod polled for the controller every frame (no OnActivate: it's a plain MonoBehaviour). Its Start
// coroutine sets every text, then calls ShowPlayButtonState synchronously, so that call is the "lobby is up" hook;
// OnDestroy (the scene unloading) is the close.
internal sealed class AccessibleBnbLobby : AccessibleScreen
{
    private static readonly AccessibleBnbLobby s_instance = new();

    private readonly KeyboardNavigator m_nav;
    private HUBBnbLobbyRunController? m_controller;
    private string m_lastButtonSig = "";

    private AccessibleBnbLobby()
    {
        m_nav = new KeyboardNavigator(this);
    }

    [HarmonyPatch(typeof(HUBBnbLobbyRunController), "ShowPlayButtonState")]
    private static class ShowHook
    {
        private static void Postfix(HUBBnbLobbyRunController __instance)
        {
            s_instance.m_controller = __instance;
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

    public string GetHelp() => LocalizationUtils.Get(LocalizationKey.HUB_BNB_LOBBY_HELP);

    public void OnGainedFocus()
    {
        Rebuild();
        m_lastButtonSig = ButtonSig();
        if (m_controller == null) return;
        var parts = new List<string> { LocalizationUtils.Get(LocalizationKey.HUB_BNB_LOBBY) };
        if (HubUi.Text(ReadField<TextMeshProUGUI>(m_controller, "lobbyHeaderText")) is { } header) parts.Add(header);
        if (ProgressText() is { } progress) parts.Add(progress);
        if (TimerText() is { } timer) parts.Add(timer);
        AccessibilityMgr.Output(this, string.Join(". ", parts), interrupt: true);
        m_nav.FocusFirst(announce: true, interruptPrior: false);
    }

    public void HandleInput()
    {
        if (m_controller == null || !m_controller.gameObject.activeInHierarchy)
        {
            AccessibilityMgr.ClearScreen(this);
            return;
        }
        // Buttons swap as the run state changes (PLAY → START NEW RUN / COLLECT REWARDS when finished); re-present
        // when the set changes, keeping the position.
        var sig = ButtonSig();
        if (sig != m_lastButtonSig)
        {
            m_lastButtonSig = sig;
            var i = m_nav.Index;
            Rebuild();
            m_nav.FocusIndex(Mathf.Clamp(i, 0, m_nav.Count - 1), announce: false);
            return;
        }
        m_nav.HandleInput();
    }

    private void Rebuild()
    {
        m_nav.SetItems(LobbyButtons().Cast<Selectable>().OrderBy(s => HubUi.ReadingOrder(s.transform)), LobbyLabel);
        m_nav.ForceFlatVertical();   // a small set of actions — predictable Up/Down through all of them
    }

    // Active, interactable buttons of the lobby canvas. The hex nodes aren't buttons, so this is the PLAY / EDIT DECK
    // / START NEW RUN / COLLECT REWARDS actions + the top bar (home / info / settings).
    private List<Button> LobbyButtons()
    {
        if (m_controller == null) return new List<Button>();
        var canvas = m_controller.GetComponentInParent<Canvas>();
        var root = canvas != null ? canvas.rootCanvas.transform : m_controller.transform.root;
        return root.GetComponentsInChildren<Button>(includeInactive: false).Where(b => b.isActiveAndEnabled && b.IsInteractable()).ToList();
    }

    private string ButtonSig() => string.Join(",", LobbyButtons().Select(b => b.gameObject.name));

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
}
