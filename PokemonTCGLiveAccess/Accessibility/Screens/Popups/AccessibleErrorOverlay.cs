using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using TMPro;
using TPCI.Localization;
using TPCI.UI.Screens;
using UnityEngine.UI;
using static Accessibility.GameReflect;

namespace Accessibility;

// Generic error overlay — driver: ErrorOverlay (an OverlayScreenGroup) shown for purchase failures
// ("insufficient funds"), network errors, etc. It carries an error line + a user-action line
// (errorText/errorLocText, userActionText/userActionLocText) and one CLOSE button whose onClick is
// HandleButtonAction (close, or restart on a fatal error). Without this it pops over the HUB
// and traps a keyboard user.
//
// ErrorOverlay overrides neither OnActivate nor OnDeActivate, so we hook the SHARED base
// OverlayScreenGroup methods with a base-typed __instance + an `is ErrorOverlay` guard (typing
// __instance as the subclass would throw a bad cast for every other overlay). The overlay GameObject
// going inactive is a backstop.
internal sealed class AccessibleErrorOverlay : AccessibleUI
{
    private static readonly AccessibleErrorOverlay s_instance = new();

    private readonly KeyboardNavigator m_nav;
    private ErrorOverlay? m_overlay;

    private AccessibleErrorOverlay()
    {
        m_nav = new KeyboardNavigator(this) { AllowTab = false, OnBack = () => s_instance.Dismiss() };
    }

    public void HandleInput()
    {
        if (m_overlay == null || !m_overlay.gameObject.activeInHierarchy)
        {
            Close();
            return;
        }
        m_nav.HandleInput();
    }

    public string GetHelp() => LocalizationUtils.Get(LocalizationKey.POPUP_BUTTONS_HELP);

    public void OnGainedFocus()
    {
        if (m_overlay == null || !m_overlay.gameObject.activeInHierarchy) return;
        var buttons = m_overlay.GetComponentsInChildren<Button>(includeInactive: false)
            .Where(b => b.isActiveAndEnabled)
            .OrderBy(b => PopupUtils.ReadingOrder(b.transform))
            .Cast<Selectable>().ToList();
        m_nav.SetItems(buttons);
        m_nav.AllowHorizontal = true;

        var msg = Message();
        AccessibilityMgr.Output(this, string.IsNullOrWhiteSpace(msg) ? LocalizationUtils.Get(LocalizationKey.POPUP_ERROR) : msg, interrupt: true);
        if (buttons.Count > 0) m_nav.FocusFirst(announce: true, interruptPrior: false);
    }

    // Route through the real CLOSE button so a fatal-error "restart" still happens; fall back
    // to closing the overlay directly.
    private void Dismiss()
    {
        var btn = m_nav.Items.OfType<Button>().FirstOrDefault();
        if (btn != null && btn.isActiveAndEnabled && btn.IsInteractable()) { btn.onClick?.Invoke(); return; }
        m_overlay?.Deactivate();
    }

    private void Close()
    {
        m_overlay = null;
        AccessibilityMgr.HideUI(this);
    }

    // The error line + the user-action line, preferring the localized component then the raw TMP.
    private string Message()
    {
        var parts = new List<string>(2);
        var err = PopupUtils.Loc(ReadField<LocText>(m_overlay, "errorLocText")) ?? PopupUtils.Text(ReadField<TMP_Text>(m_overlay, "errorText"));
        if (err != null) parts.Add(err);
        var act = PopupUtils.Loc(ReadField<LocText>(m_overlay, "userActionLocText")) ?? PopupUtils.Text(ReadField<TMP_Text>(m_overlay, "userActionText"));
        if (act != null) parts.Add(act);
        return string.Join(". ", parts);
    }

    // Fires for every overlay activation (base method) — act only for ErrorOverlay.
    [HarmonyPatch(typeof(OverlayScreenGroup), "OnActivate", new Type[0])]
    private static class ActivateHook
    {
        private static void Postfix(OverlayScreenGroup __instance)
        {
            if (__instance is not ErrorOverlay eo) return;
            s_instance.m_overlay = eo;
            PopupUtils.ShowOrRefresh(s_instance);
        }
    }

    [HarmonyPatch(typeof(OverlayScreenGroup), "OnDeActivate")]
    private static class DeactivateHook
    {
        private static void Postfix(OverlayScreenGroup __instance)
        {
            if (__instance is ErrorOverlay && __instance == s_instance.m_overlay) s_instance.Close();
        }
    }
}
