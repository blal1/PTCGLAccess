using HarmonyLib;
using TPCI.Localization;
using UnityEngine.UI;
using static Accessibility.GameReflect;

namespace Accessibility;

// Deck-options popup — driver: LandscapeDeckOptions (a HUBGroupController) opened via ShowPopup(deckName) from BOTH
// the DECKS screen (HUBDeckSelectController.OpenLandscapeDeckOptions) and the deck editor (HUBEditDeckController).
// It's the menu with a deck-name header + DECK CUSTOMIZATION / TEST DECK / DUPLICATE / VIEW DECK / DECK STATS /
// EXPORT / DELETE (+ a close X). Each option's onClick routes through LandscapeDeckOptions.Settings_* -> the
// IDeckOptionsHandler (DeckPanel_L on DECKS, or the editor) and then Deactivate. Without a handler the whole menu is
// silent — this is the one where DECK STATS / VIEW DECK / DELETE couldn't be reached.
//
// HUBGroupController.Activate/Deactivate just SetActive(true/false) the GameObject, so ShowPopup is the open hook and
// the base HUBGroupController.Deactivate (identity-checked) the close hook. DECK STATS here opens
// DeckStatsOverlayController -> AccessibleDeckStats; the others route to their real flows.
internal sealed class AccessibleDeckOptions : AccessibleUI
{
    private static readonly AccessibleDeckOptions s_instance = new();

    private readonly KeyboardNavigator m_nav;
    private LandscapeDeckOptions? m_popup;

    private AccessibleDeckOptions()
    {
        m_nav = new KeyboardNavigator(this) { AllowTab = false, OnBack = Close };
    }

    public void HandleInput()
    {
        if (m_popup == null || !m_popup.gameObject.activeInHierarchy) { Hide(); return; }
        m_nav.HandleInput();
    }

    public string GetHelp() => LocalizationUtils.Get(LocalizationKey.COLLECTION_MENU_POPUP_HELP);

    public void OnGainedFocus()
    {
        if (m_popup == null) return;
        var buttons = CollectionUi.Buttons(m_popup);
        m_nav.SetItems(buttons);
        m_nav.ForceFlatVertical();   // a vertical menu of options (+ a close X) — predictable Up/Down

        var name = LocalizationUtils.GetGame("deck_options", LocalizationKey.UI_DECK_OPTIONS);
        var title = CollectionUi.Text(ReadField<LocText>(m_popup, "deckNameText"));
        AccessibilityMgr.Output(this, title == null ? name : LocalizationUtils.Format(LocalizationKey.COLLECTION_TITLE_BODY, name, title), interrupt: true);

        // Land on the first labelled option (CUSTOMIZATION/…), not the icon-only close X which sorts first by
        // reading order (it sits above the list). Close stays reachable (Up / Escape).
        var first = buttons.FindIndex(b => CollectionUi.FirstLetterText(b) != null);
        if (first >= 0) m_nav.FocusIndex(first, announce: true, interrupt: false);
        else m_nav.FocusFirst(announce: true, interruptPrior: false);
    }

    // Escape = cancel: the popup's own teardown (HUBGroupController.Deactivate, public).
    private void Close() => m_popup?.Deactivate();

    private void Hide()
    {
        m_popup = null;
        AccessibilityMgr.HideUI(this);
    }

    private void Open(LandscapeDeckOptions popup)
    {
        m_popup = popup;
        if (AccessibilityMgr.IsFocused(this)) OnGainedFocus();
        else AccessibilityMgr.ShowUI(this);
    }

    [HarmonyPatch(typeof(LandscapeDeckOptions), "ShowPopup")]
    private static class ShowPopupHook
    {
        private static void Postfix(LandscapeDeckOptions __instance) => s_instance.Open(__instance);
    }

    // LandscapeDeckOptions doesn't override Deactivate: the base method, identity-checked.
    [HarmonyPatch(typeof(HUBGroupController), "Deactivate")]
    private static class DeactivateHook
    {
        private static void Postfix(HUBGroupController __instance)
        {
            if (s_instance.m_popup != null && __instance == s_instance.m_popup) s_instance.Hide();
        }
    }
}
