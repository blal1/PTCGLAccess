using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using TPCI.Localization;
using UnityEngine.UI;

namespace Accessibility;

// The InfoPopup family — a header + body (+ optional extra blocks) overlay with a close X. Covers:
//   • InfoPopup (base) — the League Tower season info (ⓘ → HUBLeagueTowerController.OpenSeasonInfo).
//   • CasualModeInfoPopup : InfoPopup — the "WELCOME TO CASUAL MODE" popup, auto-shown on entering
//     Casual mode (HUBGameModeController) and via the mode info button. Adds a third text block
//     (bottomText) describing the format.
//
// One hook covers both: a postfix on OverlayScreenGroup.Activate() with a base-typed, guarded
// `__instance is InfoPopup` (NOT a subclass-typed patch, which would bad-cast for other overlays).
// CasualModeInfoPopup overrides Activate() to set its three texts THEN calls base.Activate(), so by the
// time the base method (the patched one) runs, every block is populated. We read ALL active LocText under
// the popup in reading order, so the extra blocks (bottomText, and any future subclass field) are spoken
// without per-type reflection. Close = the popup's own Deactivate (Escape / the X).
internal sealed class AccessibleInfoPopup : AccessibleUI
{
    private static readonly AccessibleInfoPopup s_instance = new();

    private readonly KeyboardNavigator m_nav;
    private InfoPopup? m_popup;

    private AccessibleInfoPopup()
    {
        m_nav = new KeyboardNavigator(this) { AllowTab = false, OnBack = () => s_instance.m_popup?.Deactivate() };
    }

    public void HandleInput()
    {
        if (m_popup == null || !m_popup.gameObject.activeInHierarchy)
        {
            Close();
            return;
        }
        m_nav.HandleInput();
    }

    public string GetHelp() => LocalizationUtils.Get(LocalizationKey.POPUP_INFO_HELP);

    public void OnGainedFocus()
    {
        if (m_popup == null || !m_popup.gameObject.activeInHierarchy) return;
        var buttons = m_popup.GetComponentsInChildren<Button>(includeInactive: false)
            .Where(b => b.isActiveAndEnabled).Cast<Selectable>()
            .OrderBy(s => PopupUtils.ReadingOrder(s.transform)).ToList();
        m_nav.SetItems(buttons);
        m_nav.ForceFlatVertical();

        var text = AllText();
        AccessibilityMgr.Output(this, string.IsNullOrWhiteSpace(text) ? LocalizationUtils.Get(LocalizationKey.POPUP_INFORMATION) : text, interrupt: true);
        // Keep the close button reachable (Enter / Escape) without reading it loudly after the content.
        if (buttons.Count > 0) m_nav.FocusFirst(announce: false, interruptPrior: false);
    }

    private void Close()
    {
        m_popup = null;
        AccessibilityMgr.HideUI(this);
    }

    // Every readable text block of the popup (header, body, and any extra like the Casual format note),
    // in reading order so the title comes first.
    private string AllText()
    {
        var seen = new HashSet<string>();
        var parts = new List<string>();
        foreach (var loc in m_popup!.GetComponentsInChildren<LocText>(includeInactive: false).OrderBy(t => PopupUtils.ReadingOrder(t.transform)))
        {
            if (loc == null || !loc.gameObject.activeInHierarchy) continue;
            if (PopupUtils.Loc(loc) is { } s && seen.Add(s)) parts.Add(s);
        }
        return string.Join(". ", parts);
    }

    // Base-typed + guarded: the patch lands on OverlayScreenGroup.Activate (shared by every overlay),
    // so the parameter MUST be the base type; we act only for InfoPopup instances.
    [HarmonyPatch(typeof(OverlayScreenGroup), "Activate")]
    private static class ActivateHook
    {
        private static void Postfix(OverlayScreenGroup __instance)
        {
            if (__instance is not InfoPopup popup) return;
            s_instance.m_popup = popup;
            PopupUtils.ShowOrRefresh(s_instance);
        }
    }

    [HarmonyPatch(typeof(OverlayScreenGroup), "OnDeActivate")]
    private static class DeactivateHook
    {
        private static void Postfix(OverlayScreenGroup __instance)
        {
            if (__instance is InfoPopup && __instance == s_instance.m_popup) s_instance.Close();
        }
    }
}
