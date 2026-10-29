using System;
using System.Collections.Generic;
using System.Reflection;
using CardDatabase.DataAccess;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;
using static Accessibility.GameReflect;

namespace Accessibility;

// CARD DEX card-detail overlay — driver CardDexPurchaseCardOverlayManager : BigCardOverlayController :
// OverlayScreenGroup, the big single-card view a collection tile opens. It shows the card, its owned count,
// craft/exchange availability, and left/right arrows to browse the rest of the set.
//
// Sister to the deck editor's AccessibleCardDetail (same BigCardOverlayController base) but simpler: the Card Dex
// has no deck (no +/− deck counts) and no rarity-version drawer — it's read-and-exchange.
//   • Card     — "name, type, rarity, set, N owned / not owned[, exchangeable]". Left/Right browse the
//                previous/next card in the collection (the overlay's own Prev/NextClicked, wired to the gallery's
//                adjacent-card provider).
//   • Exchange — present whenever the game's purchase button is on screen. Enter opens the quantity/cost popup
//                (PurchaseCardButtonActions → CardPurchasePopupParts → AccessibleCraftCard).
//   • Details  — HP / type / attacks / rules (CardText.DetailLines), read line by line with Up/Down.
//
// Opens off the overlay's own 1-arg Setup (unique to this overlay, so the deck editor's isn't caught), closes on its
// OnDeActivate. Ownership is read per the exact CardID.
internal sealed class AccessibleCardDexDetail : AccessibleUI
{
    private enum Section { Card, Exchange, Details }

    private static readonly AccessibleCardDexDetail s_instance = new();

    private static readonly MethodInfo? s_selectedCard = AccessTools.PropertyGetter(typeof(BigCardOverlayController), "SelectedCard");
    private static readonly MethodInfo? s_selectedStack = AccessTools.PropertyGetter(typeof(BigCardOverlayController), "SelectedStack");

    // True while the Card Dex card detail is open — lets the exchange popup hand back its "Exchanged" confirmation.
    public static bool IsOpen => s_instance.m_overlay != null;

    private CardDexPurchaseCardOverlayManager? m_overlay;
    private Section m_section = Section.Card;
    private int m_lineIndex;
    private readonly List<string> m_lines = new();
    private string? m_linesCardId;
    private bool m_fresh;

    public void HandleInput()
    {
        if (m_overlay == null) return;
        if (CollectionUi.BackPressed()) { m_overlay.ClosePurchaseCardOverlay(); return; }

        if (AccessibleKey.READ_NEXT_MENU_OPTION.IsPressed()) { if (m_section == Section.Details) StepLine(+1); else MoveSection(+1); return; }
        if (AccessibleKey.READ_PREV_MENU_OPTION.IsPressed()) { if (m_section == Section.Details && m_lineIndex > 0) StepLine(-1); else MoveSection(-1); return; }
        if (AccessibleKey.READ_NEXT_ITEM.IsPressed()) { if (m_section == Section.Card) Flip(+1); return; }
        if (AccessibleKey.READ_PREV_ITEM.IsPressed()) { if (m_section == Section.Card) Flip(-1); return; }
        if (AccessibleKey.CONFIRM.IsPressed())
        {
            if (m_section == Section.Exchange) m_overlay.PurchaseCardButtonActions();   // → AccessibleCraftCard
            else m_overlay.ClosePurchaseCardOverlay();   // Card / Details: Enter closes (Escape does too)
        }
    }

    public string GetHelp() => LocalizationUtils.Get(LocalizationKey.COLLECTION_CARD_DEX_DETAIL_HELP);

    public void OnGainedFocus()
    {
        ResetToCard();
        if (m_fresh)
        {
            m_fresh = false;
            AccessibilityMgr.Output(this, LocalizationUtils.Get(LocalizationKey.COLLECTION_CARD_DETAILS), interrupt: true);
            AnnounceCard(interrupt: false);
            return;
        }
        if (AccessibleCraftCard.JustCrafted)
        {
            AccessibleCraftCard.JustCrafted = false;
            AccessibilityMgr.Output(this, LocalizationUtils.Get(LocalizationKey.COLLECTION_EXCHANGED), interrupt: true);
            AnnounceCard(interrupt: false);
        }
        else AnnounceCard(interrupt: true);
    }

    private void ResetToCard()
    {
        m_section = Section.Card;
        m_lineIndex = 0;
        m_linesCardId = null;
    }

    private List<Section> Sections()
    {
        var list = new List<Section> { Section.Card };
        if (IsExchangeable()) list.Add(Section.Exchange);
        EnsureLines();
        if (m_lines.Count > 0) list.Add(Section.Details);
        return list;
    }

    private void MoveSection(int dir)
    {
        var secs = Sections();
        var next = Math.Max(0, secs.IndexOf(m_section)) + dir;
        if (next < 0 || next >= secs.Count) return;
        m_section = secs[next];
        switch (m_section)
        {
            case Section.Card:
                AnnounceCard(interrupt: true);
                break;
            case Section.Exchange:
                var label = LocalizationUtils.Get(LocalizationKey.COLLECTION_EXCHANGE);
                var b = ExchangeButton();
                AccessibilityMgr.Output(this, b != null && !b.IsInteractable() ? Announce.WithState(label, LocalizationKey.GLOBAL_DISABLED) : label);
                break;
            case Section.Details:
                m_lineIndex = 0;
                AccessibilityMgr.Output(this, LocalizationUtils.Get(LocalizationKey.COLLECTION_DETAILS));
                if (m_lines.Count > 0) AccessibilityMgr.Output(this, m_lines[0], interrupt: false);
                break;
        }
    }

    private void Flip(int dir)
    {
        if (dir > 0) m_overlay!.NextClicked();
        else m_overlay!.PrevClicked();
        if (m_overlay == null) return;   // flipping past the end closes the overlay
        ResetToCard();
        AnnounceCard(interrupt: true);
    }

    private void AnnounceCard(bool interrupt)
    {
        var card = SelectedCard();
        if (card == null) { AccessibilityMgr.Output(this, LocalizationUtils.Get(LocalizationKey.COLLECTION_CARD_DETAILS), interrupt); return; }
        var row = ManagerSingleton<CardDatabaseManager>.instance?.TryGetCardFromDatabase(card.CardID);
        var parts = new List<string> { row != null ? CardText.NameWithType(row) : card.CardID };
        if ((CardText.RarityWithVariant(card) ?? CardText.RarityWithVariant(row)) is { Length: > 0 } rarity) parts.Add(rarity);
        if (CardText.SetName(card) is { Length: > 0 } set) parts.Add(set);
        var owned = Owned(card);
        parts.Add(owned > 0 ? LocalizationUtils.Format(LocalizationKey.CARD_OWNED, owned) : LocalizationUtils.Get(LocalizationKey.CARD_NOT_OWNED));
        if (IsExchangeable()) parts.Add(LocalizationUtils.Get(LocalizationKey.COLLECTION_EXCHANGEABLE));
        AccessibilityMgr.Output(this, string.Join(", ", parts), interrupt);
    }

    private AddCardPurchaseParts? PurchaseParts() => ReadField<AddCardPurchaseParts>(m_overlay, "addCardPurchaseParts");

    private Button? ExchangeButton()
    {
        var p = PurchaseParts();
        return p != null ? p.GetComponentInParent<Button>(includeInactive: true) : null;
    }

    // The game shows addCardPurchaseParts only when the card can be exchanged (CardPurchaseEvaluator says so) — that's
    // our "Exchange section present" signal, matching the deck editor's read.
    private bool IsExchangeable()
    {
        var p = PurchaseParts();
        if (p != null && p.gameObject.activeInHierarchy) return true;
        var b = ExchangeButton();
        return b != null && b.isActiveAndEnabled;
    }

    private void StepLine(int dir)
    {
        EnsureLines();
        var next = m_lineIndex + dir;
        if (next < 0 || next >= m_lines.Count) return;
        m_lineIndex = next;
        AccessibilityMgr.Output(this, m_lines[m_lineIndex]);
    }

    private void EnsureLines()
    {
        var id = SelectedCard()?.CardID;
        if (id != null && id == m_linesCardId) return;
        m_lines.Clear();
        m_linesCardId = id;
        if (id == null) return;
        var row = ManagerSingleton<CardDatabaseManager>.instance?.TryGetCardFromDatabase(id);
        if (row != null) m_lines.AddRange(CardText.DetailLines(row));
    }

    private ICardDataRow? SelectedCard() => m_overlay != null ? s_selectedCard?.Invoke(m_overlay, null) as ICardDataRow : null;

    private int Owned(ICardDataRow? card) =>
        card != null && m_overlay != null && s_selectedStack?.Invoke(m_overlay, null) is ArchetypeDBCards s ? s.TotalOwnedQuantityForSpecificCard(card.CardID) : 0;

    private void Open(CardDexPurchaseCardOverlayManager overlay)
    {
        m_overlay = overlay;
        if (AccessibilityMgr.IsFocused(this))
        {
            ResetToCard();
            AnnounceCard(interrupt: true);
            return;
        }
        m_fresh = true;
        AccessibilityMgr.ShowUI(this);
    }

    private void Closed(CardDexPurchaseCardOverlayManager overlay)
    {
        if (overlay != m_overlay) return;
        m_overlay = null;
        AccessibilityMgr.HideUI(this);
    }

    [HarmonyPatch(typeof(CardDexPurchaseCardOverlayManager), "Setup", new[] { typeof(ArchetypeDBCards) })]
    private static class SetupHook
    {
        private static void Postfix(CardDexPurchaseCardOverlayManager __instance) => s_instance.Open(__instance);
    }

    [HarmonyPatch(typeof(CardDexPurchaseCardOverlayManager), "OnDeActivate")]
    private static class OnDeActivateHook
    {
        private static void Postfix(CardDexPurchaseCardOverlayManager __instance) => s_instance.Closed(__instance);
    }
}
