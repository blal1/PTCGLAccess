using System.Collections.Generic;
using UnityEngine.UI;

namespace Accessibility;

// Global fallback for FTUE-highlighted elements on screens with no accessible component yet (Battle Pass,
// Decks, Shop, …). During the tutorial the game reparents exactly one element under an input blocker so
// only it is clickable. A handled screen surfaces that itself; but when nothing owns the screen the
// highlighted element would be keyboard-unreachable, leaving the player stuck (observed: FTUE pointed at
// QuestBtn on the Battle Pass screen with nothing to read or activate).
//
// No game method marks "an unhandled screen is coached", so a cheap poller watches FtueTracker.FocusedElement
// and shows this UI ONLY while no screen and no other UI has focus (so it never fights a real component for
// speech or keys). It steps aside the moment a screen or another UI appears.
internal sealed class AccessibleFtueFocus : AccessibleUI
{
    private static readonly AccessibleFtueFocus s_instance = new();

    private readonly KeyboardNavigator m_nav;
    private Selectable? m_shown;

    private AccessibleFtueFocus()
    {
        m_nav = new KeyboardNavigator(this) { AllowTab = false };
    }

    // Enter/Space → the element's own activation (Button.onClick / Toggle.isOn / submit). Arrows are
    // no-ops on a single item.
    public void HandleInput() => m_nav.HandleInput();

    public string GetHelp() => LocalizationUtils.Get(LocalizationKey.POPUP_FTUE_FOCUS_HELP);

    public void OnGainedFocus()
    {
        if (m_shown == null) return;
        // A coached Toggle is a tab, so Enter must SELECT it — see FtueTracker.
        m_nav.RadioToggleMode = m_shown is Toggle;
        m_nav.GroupName = LocalizationUtils.Get(LocalizationKey.POPUP_TUTORIAL);   // read per presentation, so it follows the game language
        m_nav.SetItems(new List<Selectable> { m_shown });
        m_nav.FocusFirst(announce: true, interruptPrior: true);
        Log.Info($"FTUE fallback presenting: {m_shown.gameObject.name}");
    }

    [AccessibilityPoller]
    private static void Poll()
    {
        var focus = FtueTracker.FtueIsRunning ? FtueTracker.FocusedElement : null;
        var free = AccessibilityMgr.CurrentScreen == null && (AccessibilityMgr.Focused == null || AccessibilityMgr.Focused == s_instance);
        if (!free || focus == null || !focus.isActiveAndEnabled)
        {
            if (s_instance.m_shown == null) return;
            s_instance.m_shown = null;
            AccessibilityMgr.HideUI(s_instance);
            return;
        }
        if (focus == s_instance.m_shown) return;
        s_instance.m_shown = focus;
        PopupUtils.ShowOrRefresh(s_instance);
    }
}
