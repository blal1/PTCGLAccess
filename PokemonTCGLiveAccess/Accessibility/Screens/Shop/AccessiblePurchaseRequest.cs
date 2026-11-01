using System;
using HarmonyLib;
using TPCI.Localization;
using TPCI.UI.Screens.ShopScreen;
using UnityEngine.UI;
using static Accessibility.GameReflect;

namespace Accessibility;

// The wait overlay of every purchase, exchange, deck import or export and reward grant: PurchaseRequestOverlay
// (OverlayManager.pendingPopupOverlay and the views passed to CardPurchaser / CardPurchasePopupParts). Text only, plus a
// close button that the game shows only on failure: SetDisplayText("ui_purchasing_failed", enableCloseButton: true).
// Without us that failure is a silent trap: nothing closes it on its own and the keyboard is still on the screen below.
//
// Callers open the overlay and THEN write its text, and write again as the request progresses, so the text is polled
// rather than read at open. Taking the keyboard during the wait is deliberate: the screen below would otherwise still
// answer Enter, through our direct onClick calls, while the game's raycast blocker stops the mouse.
internal sealed class AccessiblePurchaseRequest : AccessibleUI
{
    private static readonly AccessiblePurchaseRequest s_instance = new();

    private PurchaseRequestOverlay? m_overlay;
    private string? m_lastText;
    private bool m_lastClosable;

    public void HandleInput()
    {
        if (m_overlay == null || !m_overlay.gameObject.activeInHierarchy) { Hide(); return; }
        var close = CloseButton();
        var closable = close != null && close.isActiveAndEnabled;
        var text = CollectionUi.Text(ReadField<LocText>(m_overlay, "text"));
        if (text != m_lastText || closable != m_lastClosable)
        {
            m_lastText = text;
            m_lastClosable = closable;
            if (text != null)
                AccessibilityMgr.Output(this, closable ? LocalizationUtils.Format(LocalizationKey.SHOPUI_PURCHASE_CLOSE_HINT, text) : text, interrupt: true);
        }
        if (closable && (AccessibleKey.CONFIRM.IsPressed() || CollectionUi.BackPressed())) m_overlay.Close();
    }

    public string GetHelp() => LocalizationUtils.Get(LocalizationKey.SHOPUI_PURCHASE_HELP);

    // Spoken from HandleInput: at open the caller has not written its text yet.
    public void OnGainedFocus() => m_lastText = null;

    private Button? CloseButton() => ReadField<Button>(m_overlay, "closeButton");

    private void Hide()
    {
        m_overlay = null;
        AccessibilityMgr.HideUI(this);
    }

    [HarmonyPatch(typeof(OverlayScreenGroup), "OnActivate", new Type[] { })]
    private static class OnActivateHook
    {
        private static void Postfix(OverlayScreenGroup __instance)
        {
            if (__instance is not PurchaseRequestOverlay overlay) return;
            s_instance.m_overlay = overlay;
            s_instance.m_lastText = null;
            if (!AccessibilityMgr.IsFocused(s_instance)) AccessibilityMgr.ShowUI(s_instance);
        }
    }

    [HarmonyPatch(typeof(OverlayScreenGroup), "OnDeActivate")]
    private static class OnDeActivateHook
    {
        private static void Postfix(OverlayScreenGroup __instance)
        {
            if (s_instance.m_overlay != null && __instance == s_instance.m_overlay) s_instance.Hide();
        }
    }
}
