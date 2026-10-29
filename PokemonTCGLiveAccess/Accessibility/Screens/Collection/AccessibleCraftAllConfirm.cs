using System.Collections.Generic;
using HarmonyLib;
using TMPro;
using _Rainier.Scripts.UI.DeckEditor.Collection;
using static Accessibility.GameReflect;

namespace Accessibility;

// "Exchange All" confirmation (new in 1.5.0) — driver: CraftAllConfirmOverlay (an OverlayScreenGroup) shown when
// the deck editor's "Exchange all unowned" button is pressed and the player can afford it
// (CraftUnownedCardsButtonController.ShowCraftingConfirmation -> Setup -> Activate). It shows a header
// ("Exchange N cards"), the total cost, a card gallery, and a confirm button (InvokePurchaseDelegate) +
// cancel/close. Silent without a handler.
//
// CraftAllConfirmOverlay does NOT override OnActivate or OnDeActivate, so both hooks sit on the shared base
// OverlayScreenGroup with a base-typed __instance and an `is` / identity guard (typing it as the subclass would
// bad-cast for every other overlay).
internal sealed class AccessibleCraftAllConfirm : AccessibleUI
{
    private static readonly AccessibleCraftAllConfirm s_instance = new();

    private readonly KeyboardNavigator m_nav;
    private CraftAllConfirmOverlay? m_overlay;

    private AccessibleCraftAllConfirm()
    {
        m_nav = new KeyboardNavigator(this) { AllowTab = false, OnBack = Close };
    }

    public void HandleInput()
    {
        if (m_overlay == null || !m_overlay.gameObject.activeInHierarchy) { Hide(); return; }
        m_nav.HandleInput();
    }

    public string GetHelp() => LocalizationUtils.Get(LocalizationKey.COLLECTION_POPUP_HELP);

    public void OnGainedFocus()
    {
        if (m_overlay == null) return;
        var buttons = CollectionUi.Buttons(m_overlay);
        m_nav.SetItems(buttons);
        m_nav.AllowHorizontal = true;
        AccessibilityMgr.Output(this, Message(), interrupt: true);
        if (buttons.Count > 0) m_nav.FocusFirst(announce: true, interruptPrior: false);
    }

    // Escape = cancel (don't buy): the overlay's own teardown.
    private void Close() => m_overlay?.Deactivate();

    private void Hide()
    {
        m_overlay = null;
        AccessibilityMgr.HideUI(this);
    }

    // "<header>. Cost <cost>" — header is the game's own "Exchange N cards" string, cost the total.
    private string Message()
    {
        var title = LocalizationUtils.Get(LocalizationKey.COLLECTION_EXCHANGE_CARDS);
        if (m_overlay == null) return title;
        var parts = new List<string>(2);
        if (CollectionUi.Text(ReadField<TMP_Text>(m_overlay, "headerLabel")) is { } header) parts.Add(header);
        if (CollectionUi.Text(ReadField<TMP_Text>(m_overlay, "costLabel")) is { } cost) parts.Add(LocalizationUtils.Format(LocalizationKey.COLLECTION_COST, cost));
        return parts.Count == 0 ? title : string.Join(". ", parts);
    }

    private void Open(CraftAllConfirmOverlay overlay)
    {
        m_overlay = overlay;
        if (AccessibilityMgr.IsFocused(this)) OnGainedFocus();
        else AccessibilityMgr.ShowUI(this);
    }

    [HarmonyPatch(typeof(OverlayScreenGroup), "OnActivate", new System.Type[] { })]
    private static class OnActivateHook
    {
        private static void Postfix(OverlayScreenGroup __instance)
        {
            if (__instance is CraftAllConfirmOverlay overlay) s_instance.Open(overlay);
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
