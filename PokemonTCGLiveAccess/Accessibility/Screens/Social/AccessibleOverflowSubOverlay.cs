using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using TMPro;
using TPCI.Localization;
using UnityEngine;
using UnityEngine.UI;
using static Accessibility.GameReflect;

namespace Accessibility;

// The overflow "⋮" menu's text/link sub-panels: ABOUT, TERMS OF USE, PRIVACY NOTICE. Each is opened
// by its own HUBOverflowMenuController.OnClick* method, which OverlayManager.OpenOverlay's a dedicated
// OverlayScreenGroup field (_aboutController / _tosController / _privacyController). Without a component,
// focus stays stranded on the menu behind the panel.
//
// One component covers all three (same shape: an OverlayScreenGroup of readable text + a few buttons):
//   • ABOUT (HUBOverflowAboutController) — the app version line plus DELETE ACCOUNT and the
//     Privacy Notice / EULA / Credits / Attributions link buttons. (Delete Account especially must be
//     reachable + readable, so this isn't optional polish.)
//   • TERMS OF USE / PRIVACY NOTICE — a legal-text panel with a back/close affordance.
//
// Read the panel name + its body text as a preamble, present the real buttons as a flat vertical nav
// (close-chrome filtered, since Escape already closes). Closed by the shared OverlayScreenGroup.OnDeActivate
// (identity guard); Escape calls OverlayManager.CloseOverlay → back to the still-open ⋮ menu beneath. Buttons
// keep their own onClick, so links / Delete Account fire exactly as a mouse would.
internal sealed class AccessibleOverflowSubOverlay : AccessibleUI
{
    private static readonly AccessibleOverflowSubOverlay s_instance = new();

    private readonly KeyboardNavigator m_nav;
    private OverlayManager? m_overlayManager;
    private OverlayScreenGroup? m_group;
    private string m_panelName = "";

    private AccessibleOverflowSubOverlay()
    {
        m_nav = new KeyboardNavigator(this) { AllowTab = false, OnBack = Close };
    }

    public void HandleInput() => m_nav.HandleInput();

    public string GetHelp() => LocalizationUtils.Get(LocalizationKey.SOCIAL_PANEL_HELP);

    public void OnGainedFocus() => Rebuild();

    private void Rebuild()
    {
        if (m_group == null) return;

        // Force layout before reading positions — overlays open animated, so transforms are at origin
        // on the first frame and reading-order sorts would scramble.
        if (m_group.transform is RectTransform rt)
        {
            Canvas.ForceUpdateCanvases();
            LayoutRebuilder.ForceRebuildLayoutImmediate(rt);
        }

        // Buttons: real entries only (drop the X / invisible tap-outside blocker; Escape closes).
        var items = m_group.GetComponentsInChildren<Button>(includeInactive: false)
            .Where(b => b.isActiveAndEnabled && !SocialUi.IsCloseChrome(b.gameObject.name))
            .Cast<Selectable>()
            .OrderBy(s => SocialUi.ReadingOrder(s.transform))
            .ToList();
        m_nav.SetItems(items);
        m_nav.ForceFlatVertical();   // a vertical list of links — predictable Up/Down

        var body = BodyText();
        AccessibilityMgr.Output(this, string.IsNullOrWhiteSpace(body) ? m_panelName : string.Join(". ", m_panelName, body), interrupt: true);
        if (items.Count > 0) m_nav.FocusFirst(announce: true, interruptPrior: false);
    }

    // The panel's readable text (version line, legal copy, descriptions): every TMP NOT under a
    // button, in reading order. Long legal docs read as one interruptible utterance for now;
    // if paragraph navigation is wanted, that's a follow-up.
    private string BodyText()
    {
        var parts = new List<(float order, string text)>();
        foreach (var t in m_group!.GetComponentsInChildren<TMP_Text>(includeInactive: false))
        {
            if (t == null || t.GetComponentInParent<Button>() != null) continue;   // skip button labels
            var txt = t.text;
            if (string.IsNullOrWhiteSpace(txt)) txt = SocialUi.TextOf(t.GetComponent<LocText>());
            if (!string.IsNullOrWhiteSpace(txt)) parts.Add((SocialUi.ReadingOrder(t.transform), txt.Trim()));
        }
        return string.Join(". ", parts.OrderBy(p => p.order).Select(p => p.text));
    }

    private void Close()
    {
        try
        {
            if (m_overlayManager != null && m_group != null && m_overlayManager.IsOverlayOpen(m_group))
            {
                m_overlayManager.CloseOverlay(m_group);
                return;   // OnDeActivate hides us
            }
        }
        catch (System.Exception ex) { Log.Error($"OverflowSubOverlay.Close threw: {ex.Message}"); }
        m_group = null;
        AccessibilityMgr.HideUI(this);
    }

    private static void Open(HUBOverflowMenuController ctrl, string field, string resourceId, LocalizationKey fallback)
    {
        var group = ReadField<OverlayScreenGroup>(ctrl, field);
        if (group == null) { Log.Warn($"OverflowSubOverlay: {field} was null"); return; }
        s_instance.m_overlayManager = ReadField<OverlayManager>(ctrl, "overlayManager");
        s_instance.m_group = group;
        s_instance.m_panelName = LocalizationUtils.GetGame(resourceId, fallback);
        if (AccessibilityMgr.IsFocused(s_instance)) s_instance.Rebuild();
        else AccessibilityMgr.ShowUI(s_instance);
    }

    [HarmonyPatch(typeof(HUBOverflowMenuController), "OnClickAbout")]
    private static class AboutHook
    {
        private static void Postfix(HUBOverflowMenuController __instance) =>
            Open(__instance, "_aboutController", "overflow_about", LocalizationKey.SOCIAL_ABOUT);
    }

    [HarmonyPatch(typeof(HUBOverflowMenuController), "OnClickTOS")]
    private static class TosHook
    {
        private static void Postfix(HUBOverflowMenuController __instance) =>
            Open(__instance, "_tosController", "overflow_termsofuse", LocalizationKey.SOCIAL_TERMS_OF_USE);
    }

    [HarmonyPatch(typeof(HUBOverflowMenuController), "OnClickPrivacyPolicy")]
    private static class PrivacyHook
    {
        private static void Postfix(HUBOverflowMenuController __instance) =>
            Open(__instance, "_privacyController", "overflow_privacynotice", LocalizationKey.SOCIAL_PRIVACY_NOTICE);
    }

    [HarmonyPatch(typeof(OverlayScreenGroup), "OnDeActivate")]
    private static class OnDeActivateHook
    {
        private static void Postfix(OverlayScreenGroup __instance)
        {
            if (s_instance.m_group == null || !ReferenceEquals(s_instance.m_group, __instance)) return;
            s_instance.m_group = null;
            AccessibilityMgr.HideUI(s_instance);
        }
    }
}
