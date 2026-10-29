using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using TPCI.Localization;
using UnityEngine.UI;
using static Accessibility.GameReflect;

namespace Accessibility;

// Generic confirm dialog — driver: ConfirmOverlay (an OverlayScreenGroup). It carries a title, a
// message, and a user-action line (titleLocText / messageLocText / userActionLocText) plus a
// CONFIRM and a CANCEL button whose onClicks fire the overlay's OnConfirm / OnCancel UnityEvents
// (wired per-caller). The friends system uses it for "decline friend request?", "remove friend?",
// and "too many friends"; it's generic, so this covers any caller that opens it. It is NOT an
// ISharesPlayerName component for that reason.
//
// ConfirmOverlay overrides neither OnActivate nor OnDeActivate, so we hook the SHARED base
// OverlayScreenGroup methods with a base-typed __instance + an `is` guard (typing it as the subclass
// would bad-cast for every other overlay). The overlay GameObject going inactive is a backstop.
internal sealed class AccessibleConfirmOverlay : AccessibleUI
{
    private static readonly AccessibleConfirmOverlay s_instance = new();

    private readonly KeyboardNavigator m_nav;
    private ConfirmOverlay? m_overlay;

    private AccessibleConfirmOverlay()
    {
        // Escape = cancel (the safe no-op): fire the overlay's own Cancel(), routing through whatever
        // OnCancel handler the caller wired (which closes it).
        m_nav = new KeyboardNavigator(this) { AllowTab = false, OnBack = () => s_instance.m_overlay?.Cancel() };
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

        AccessibilityMgr.Output(this, Message(), interrupt: true);
        if (buttons.Count > 0) m_nav.FocusFirst(announce: true, interruptPrior: false);
    }

    private void Close()
    {
        m_overlay = null;
        AccessibilityMgr.HideUI(this);
    }

    // "<title>. <message>" — read from the localized text components.
    private string Message()
    {
        var parts = new List<string>(2);
        if (PopupUtils.Loc(ReadField<LocText>(m_overlay, "titleLocText")) is { } title) parts.Add(title);
        if (PopupUtils.Loc(ReadField<LocText>(m_overlay, "messageLocText")) is { } body) parts.Add(body);
        return parts.Count == 0 ? LocalizationUtils.Get(LocalizationKey.POPUP_CONFIRM) : string.Join(". ", parts);
    }

    // Fires for every overlay activation (base method) — act only for ConfirmOverlay.
    [HarmonyPatch(typeof(OverlayScreenGroup), "OnActivate", new Type[0])]
    private static class ActivateHook
    {
        private static void Postfix(OverlayScreenGroup __instance)
        {
            if (__instance is not ConfirmOverlay co) return;
            s_instance.m_overlay = co;
            PopupUtils.ShowOrRefresh(s_instance);
        }
    }

    [HarmonyPatch(typeof(OverlayScreenGroup), "OnDeActivate")]
    private static class DeactivateHook
    {
        private static void Postfix(OverlayScreenGroup __instance)
        {
            if (__instance is ConfirmOverlay && __instance == s_instance.m_overlay) s_instance.Close();
        }
    }
}
