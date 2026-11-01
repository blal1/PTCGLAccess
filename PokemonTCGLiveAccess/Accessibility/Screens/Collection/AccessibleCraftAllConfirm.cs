using System.Collections.Generic;
using HarmonyLib;
using TMPro;
using TPCI.Rainier.Features.DeckEditor;
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
    private int m_card = -1;
    private bool m_fresh;

    private AccessibleCraftAllConfirm()
    {
        m_nav = new KeyboardNavigator(this) { AllowTab = false, OnBack = Close };
    }

    public void HandleInput()
    {
        if (m_overlay == null || !m_overlay.gameObject.activeInHierarchy) { Hide(); return; }
        // The cards to be exchanged: the gallery's data, since the scroller only realizes what is on screen.
        if (AccessibleKey.READ_NEXT_ITEM.IsPressed()) { MoveCard(1); return; }
        if (AccessibleKey.READ_PREV_ITEM.IsPressed()) { MoveCard(-1); return; }
        if (AccessibleKey.INSPECT_CARD.IsPressed())
        {
            // The same big card a click opens; the game already made this overlay its adjacent-card provider.
            var bigCard = ReadField<PurchaseableCardOverlayController>(UnityEngine.Object.FindFirstObjectByType<CraftUnownedCardsButtonController>(), "bigCardOverlay");
            if (CurrentCard() is { } stack) bigCard?.Setup(stack, stack.GetPreferredDisplayCard());
            return;
        }
        m_nav.HandleInput();
    }

    public string GetHelp() => LocalizationUtils.Get(LocalizationKey.COLLECTION_CRAFT_ALL_HELP);

    private ArchetypeDBCards? CurrentCard()
    {
        var gallery = ReadField<SectionedCardGalleryView>(m_overlay, "cardGalleryView");
        return gallery != null && m_card >= 0 && m_card < gallery.Count ? gallery.AtIndex(m_card) : null;
    }

    private void MoveCard(int dir)
    {
        var gallery = ReadField<SectionedCardGalleryView>(m_overlay, "cardGalleryView");
        if (gallery == null || gallery.Count == 0) return;
        var next = System.Math.Max(0, System.Math.Min(gallery.Count - 1, m_card + dir));
        if (next == m_card) return;
        m_card = next;
        AccessibilityMgr.Output(this, Announce.Item(AccessibleEditDeck.CardLabel(gallery.AtIndex(m_card)), null, m_card + 1, gallery.Count), interrupt: true);
    }

    public void OnGainedFocus()
    {
        if (m_overlay == null) return;
        // Back from the big card: same button, same card.
        if (!m_fresh && m_nav.Current != null) { m_nav.RefocusCurrent(); return; }
        m_fresh = false;
        var buttons = CollectionUi.Buttons(m_overlay);
        m_nav.SetItems(buttons);
        m_nav.ForceFlatVertical();
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
        m_card = -1;
        m_fresh = true;
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
