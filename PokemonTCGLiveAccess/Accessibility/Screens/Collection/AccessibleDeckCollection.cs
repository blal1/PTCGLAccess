using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using TPCI.Localization;
using TPCI.Rainier;
using UnityEngine;
using UnityEngine.UI;
using static Accessibility.GameReflect;

namespace Accessibility;

// Full deck view — DeckCollectionOverlayController (OverlayManager.deckCollectionOverlay), opened from the deck options
// and editor (ActivateWithDeckHeader), a shop deck slot (ActivateWithShopHeader) and the deck grid modal.
// Both entry points write the header AFTER Activate, so they are the hooks, not OnActivate.
//
// The gallery is a virtualized scroller, so we walk its data (ExpansionGalleryView._curCollection), not its cells.
// Tab cycles cards / categories / buttons. Enter on a card opens the big card the same way a stub click does
// (the view's purchaseableCardOverlayController.Setup, wired in PopulateFromDeck).
internal sealed class AccessibleDeckCollection : AccessibleUI
{
    private enum Section { Cards, Categories, Buttons }

    private static readonly AccessibleDeckCollection s_instance = new();

    private readonly KeyboardNavigator m_categoryNav;
    private readonly KeyboardNavigator m_buttonNav;
    private readonly CardLines m_lines;
    private DeckCollectionOverlayController? m_overlay;
    private BasicCategorySelector? m_categories;
    private Section m_section;
    private int m_index;
    private CardCategory m_lastCategory;
    private bool m_exportShown;

    private AccessibleDeckCollection()
    {
        m_categoryNav = new KeyboardNavigator(this) { RadioToggleMode = true };
        m_buttonNav = new KeyboardNavigator(this);
        m_lines = new CardLines(this);
    }

    public void HandleInput()
    {
        if (m_overlay == null || !m_overlay.gameObject.activeInHierarchy) { Hide(); return; }
        if (CollectionUi.BackPressed()) { m_overlay.Deactivate(); return; }
        AnnounceExport();

        if (m_categories != null && m_categories.CurrentVal != m_lastCategory)
        {
            m_lastCategory = m_categories.CurrentVal;
            m_index = 0;   // PopulateFromDeck re-sorted the list synchronously
        }

        if (AccessibleKey.NEXT_SECTION.IsPressed()) { Cycle(1); return; }
        if (AccessibleKey.PREV_SECTION.IsPressed()) { Cycle(-1); return; }

        switch (m_section)
        {
            case Section.Categories: m_categoryNav.HandleInput(); break;
            case Section.Buttons: m_buttonNav.HandleInput(); break;
            default: HandleCards(); break;
        }
    }

    public string GetHelp() => LocalizationUtils.Get(LocalizationKey.COLLECTION_DECK_VIEW_HELP);

    public void OnGainedFocus()
    {
        if (m_overlay == null) return;
        m_categories = ReadField<BasicCategorySelector>(m_overlay, "categorySelector");
        if (m_categories != null) m_lastCategory = m_categories.CurrentVal;
        m_categoryNav.SetItems(CollectionUi.InReadingOrder(m_categories != null
            ? m_categories.GetComponentsInChildren<Toggle>(includeInactive: false).Where(t => t.isActiveAndEnabled)
            : Enumerable.Empty<Toggle>()), s => CollectionUi.CategoryLabel(m_categories, s));
        m_categoryNav.GroupName = LocalizationUtils.Get(LocalizationKey.COLLECTION_CATEGORY);
        m_buttonNav.SetItems(CollectionUi.Buttons(m_overlay));
        m_buttonNav.ForceFlatVertical();

        var header = CollectionUi.Text(ReadField<LocText>(m_overlay, "deckHeaderLocText")) ?? LocalizationUtils.Get(LocalizationKey.COLLECTION_DECK_VIEW);
        var quantity = CollectionUi.Text(ReadField<LocText>(m_overlay, "deckQuantityLocText"));
        AccessibilityMgr.Output(this, quantity != null ? LocalizationUtils.Format(LocalizationKey.COMMON_STATE_FORMAT, header, quantity) : header, interrupt: true);
        m_section = Section.Cards;
        ReadCard(interrupt: false);
    }

    private void Cycle(int dir)
    {
        m_section = (Section)(((int)m_section + dir + 3) % 3);
        switch (m_section)
        {
            case Section.Categories: if (m_categoryNav.Count > 0) m_categoryNav.FocusFirst(); else Cycle(dir); break;
            case Section.Buttons: if (m_buttonNav.Count > 0) m_buttonNav.FocusFirst(); else Cycle(dir); break;
            default: ReadCard(interrupt: true); break;
        }
    }

    private void HandleCards()
    {
        var cards = Cards();
        if (AccessibleKey.READ_NEXT_ITEM.IsPressed()) { if (m_index < cards.Count - 1) { m_index++; ReadCard(interrupt: true); } return; }
        if (AccessibleKey.READ_PREV_ITEM.IsPressed()) { if (m_index > 0) { m_index--; ReadCard(interrupt: true); } return; }
        if (AccessibleKey.CONFIRM.IsPressed())
        {
            if (Current() is { } stack) ReadField<PurchaseableCardOverlayController>(m_overlay!.GetGalleryView(), "purchaseableCardOverlayController")?.Setup(stack, stack.GetPreferredDisplayCard());
            return;
        }
        m_lines.HandleInput();
    }

    private List<ArchetypeDBCards> Cards() =>
        (m_overlay != null ? ReadField<List<ArchetypeDBCards>>(m_overlay.GetGalleryView(), "_curCollection") : null) ?? new List<ArchetypeDBCards>();

    private ArchetypeDBCards? Current()
    {
        var cards = Cards();
        return m_index >= 0 && m_index < cards.Count ? cards[m_index] : null;
    }

    private void ReadCard(bool interrupt)
    {
        if (Current() is not { } stack) return;
        m_lines.Reset();
        AccessibilityMgr.Output(this, Announce.Item(AccessibleEditDeck.CardLabel(stack), null, m_index + 1, Cards().Count), interrupt);
    }

    // The "copied to clipboard" panel the export button shows for a moment.
    private void AnnounceExport()
    {
        var export = ReadField<CanvasGroup>(m_overlay, "exportOverlay");
        var shown = export != null && export.gameObject.activeInHierarchy;
        if (shown && !m_exportShown && CollectionUi.FirstLetterText(export!) is { } text) AccessibilityMgr.Output(this, text);
        m_exportShown = shown;
    }

    private sealed class CardLines : AccessibleItem
    {
        private readonly AccessibleDeckCollection m_owner;

        public CardLines(AccessibleDeckCollection owner) : base(owner) => m_owner = owner;

        protected internal override List<string> GetLines()
        {
            var lines = new List<string>();
            if (m_owner.Current() is not { } stack) return lines;
            lines.Add(AccessibleEditDeck.CardLabel(stack));
            if (AccessibleEditDeck.Row(stack) is { } row) lines.AddRange(CardText.DetailLines(row));
            return lines;
        }
    }

    private void Hide()
    {
        m_overlay = null;
        AccessibilityMgr.HideUI(this);
    }

    private void Open(DeckCollectionOverlayController overlay)
    {
        if (!overlay.IsOpen()) return;   // refused by a busy game
        m_overlay = overlay;
        m_index = 0;
        if (AccessibilityMgr.IsFocused(this)) OnGainedFocus();
        else AccessibilityMgr.ShowUI(this);
    }

    [HarmonyPatch(typeof(DeckCollectionOverlayController), "ActivateWithDeckHeader")]
    private static class DeckHeaderHook
    {
        private static void Postfix(DeckCollectionOverlayController __instance) => s_instance.Open(__instance);
    }

    [HarmonyPatch(typeof(DeckCollectionOverlayController), "ActivateWithShopHeader")]
    private static class ShopHeaderHook
    {
        private static void Postfix(DeckCollectionOverlayController __instance) => s_instance.Open(__instance);
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
