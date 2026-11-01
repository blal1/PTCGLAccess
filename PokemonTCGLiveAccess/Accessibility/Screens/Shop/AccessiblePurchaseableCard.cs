using System.Collections.Generic;
using System.Reflection;
using CardDatabase.DataAccess;
using HarmonyLib;
using TPCI.Localization;
using UnityEngine;
using static Accessibility.GameReflect;

namespace Accessibility;

// The big card view with an optional buy button — PurchaseableCardOverlayController (OverlayManager.bigCardOverlay),
// opened from a shop product's card slot, a full deck view, and the exchange-all gallery. All of them come through
// BigCardOverlayController.Setup, which AddCardOverlayManager and the Card Dex overlay share: hence the type guard.
//
// Left/Right use the game's NextClicked/PrevClicked. Those CLOSE the overlay when there is no neighbour, so they are
// gated on the same prevButton/nextButton the game hides in that case. The buy button sits in purchaseUI, whose state
// is only written by SetupLargeCard(ICardDataRow), which nothing on this path calls in 1.43: trust what is active.
internal sealed class AccessiblePurchaseableCard : AccessibleUI
{
    private static readonly AccessiblePurchaseableCard s_instance = new();

    private static readonly MethodInfo? s_selectedCard = AccessTools.PropertyGetter(typeof(BigCardOverlayController), "SelectedCard");

    private readonly CardLines m_lines;
    private PurchaseableCardOverlayController? m_overlay;

    private AccessiblePurchaseableCard() => m_lines = new CardLines(this);

    public void HandleInput()
    {
        if (m_overlay == null || !m_overlay.gameObject.activeInHierarchy) { Hide(); return; }
        if (CollectionUi.BackPressed()) { m_overlay.Deactivate(); return; }
        if (AccessibleKey.READ_NEXT_ITEM.IsPressed()) { Step(ReadField<GameObject>(m_overlay, "nextButton"), m_overlay.NextClicked); return; }
        if (AccessibleKey.READ_PREV_ITEM.IsPressed()) { Step(ReadField<GameObject>(m_overlay, "prevButton"), m_overlay.PrevClicked); return; }
        if (AccessibleKey.CONFIRM.IsPressed())
        {
            if (m_overlay.purchaseBtn != null && m_overlay.purchaseBtn.isActiveAndEnabled && m_overlay.purchaseBtn.interactable) m_overlay.purchaseBtn.onClick.Invoke();
            return;
        }
        m_lines.HandleInput();
    }

    public string GetHelp() => LocalizationUtils.Get(LocalizationKey.SHOPUI_BIG_CARD_HELP);

    public void OnGainedFocus()
    {
        m_lines.ReadFirstLine();
    }

    private void Step(GameObject? button, System.Action move)
    {
        if (button == null || !button.activeInHierarchy) return;
        move();
        m_lines.ReadFirstLine();
    }

    private string Label()
    {
        var card = s_selectedCard?.Invoke(m_overlay, null) as ICardDataRow;
        if (card == null) return LocalizationUtils.Get(LocalizationKey.SHOPUI_ITEM);
        var row = ManagerSingleton<CardDatabaseManager>.instance?.TryGetCardFromDatabase(card.CardID);
        var parts = new List<string> { row != null ? CardText.NameWithType(row) : card.CardID };
        parts.Add(card.OwnedQuantity > 0 ? LocalizationUtils.Format(LocalizationKey.CARD_OWNED, card.OwnedQuantity) : LocalizationUtils.Get(LocalizationKey.CARD_NOT_OWNED));
        if (m_overlay!.purchaseBtn != null && m_overlay.purchaseBtn.isActiveAndEnabled)
        {
            if (CollectionUi.Text(m_overlay.purchaseBtnText) is { } buy) parts.Add(buy);
            if (CollectionUi.Text(m_overlay.purchaseCost) is { } cost) parts.Add(LocalizationUtils.Format(LocalizationKey.COLLECTION_COST, $"{cost} {AccessibleCraftCard.CreditsWord()}"));
        }
        return string.Join(", ", parts);
    }

    private sealed class CardLines : AccessibleItem
    {
        private readonly AccessiblePurchaseableCard m_owner;

        public CardLines(AccessiblePurchaseableCard owner) : base(owner) => m_owner = owner;

        protected internal override List<string> GetLines()
        {
            var lines = new List<string> { m_owner.Label() };
            var card = s_selectedCard?.Invoke(m_owner.m_overlay, null) as ICardDataRow;
            var row = card != null ? ManagerSingleton<CardDatabaseManager>.instance?.TryGetCardFromDatabase(card.CardID) : null;
            if (row != null) lines.AddRange(CardText.DetailLines(row));
            return lines;
        }
    }

    private void Hide()
    {
        m_overlay = null;
        AccessibilityMgr.HideUI(this);
    }

    [HarmonyPatch(typeof(BigCardOverlayController), "Setup")]
    private static class SetupHook
    {
        private static void Postfix(BigCardOverlayController __instance)
        {
            // Setup returns early when the game is busy: only follow an overlay that really opened.
            if (__instance is not PurchaseableCardOverlayController overlay || !overlay.IsOpen()) return;
            s_instance.m_overlay = overlay;
            if (AccessibilityMgr.IsFocused(s_instance)) s_instance.OnGainedFocus();
            else AccessibilityMgr.ShowUI(s_instance);
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
