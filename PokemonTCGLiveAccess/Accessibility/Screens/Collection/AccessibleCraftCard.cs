using System.Reflection;
using HarmonyLib;

namespace Accessibility;

// Per-card EXCHANGE popup — driver: CardPurchasePopupParts, the quantity/cost popup the deck editor's card-detail
// "EXCHANGE" button opens (the game's code calls it Craft/CraftOpen; the UI says Exchange)
// (AddCardOverlayManager.PurchaseCardButtonActions -> cardPurchasePopup.ShowPurchasePopup). The same popup component
// is reused by the Card Dex purchase overlay, so one component covers both. It is a quantity stepper
// ([-] N cards [+]) plus the Credit cost, your owned count, and a Confirm button — fully silent without us.
//
// CardPurchasePopupParts is a plain MonoBehaviour (NOT an OverlayScreenGroup), shown/hidden via its own
// ShowPurchasePopup/HidePurchasePopup, so those are the hooks. We drive the game's own public action methods —
// IncreaseCardCountActions / DecreaseCardCountActions / ConfirmPurchaseButtonActions — exactly what the popup's own
// +/- and confirm buttons call.
//
// Quantity moves with Left/Right OR Up/Down OR +/- (it's a single stepper, so every axis adjusts it); Enter confirms,
// Escape cancels. Confirmation feedback: on a successful craft (owned count rose) we set JustCrafted so the
// card-detail component underneath can say "Exchanged" before re-reading the now-owned card — gated to the
// card-detail contexts so a craft from elsewhere can't strand the flag.
internal sealed class AccessibleCraftCard : AccessibleUI
{
    private static readonly AccessibleCraftCard s_instance = new();

    // Set when a craft just SUCCEEDED with a card detail open beneath us. Read + cleared by that card detail.
    public static bool JustCrafted;

    // Exact popup state read straight off its private ints — more reliable than parsing the formatted, sprite-bearing
    // LocText. cardCount = copies to craft, numOwned = owned, calculatedCurrencyAmount = total cost in Trade Currency.
    private static readonly FieldInfo? s_count = AccessTools.Field(typeof(CardPurchasePopupParts), "cardCount");
    private static readonly FieldInfo? s_owned = AccessTools.Field(typeof(CardPurchasePopupParts), "numOwned");
    private static readonly FieldInfo? s_cost = AccessTools.Field(typeof(CardPurchasePopupParts), "calculatedCurrencyAmount");

    private CardPurchasePopupParts? m_popup;
    private bool m_confirming;        // a purchase is in flight — ignore input until it resolves
    private int m_ownedAtConfirm = -1;

    public void HandleInput()
    {
        if (m_popup == null || !IsOpen()) { Hide(); return; }

        if (CollectionUi.BackPressed()) { m_popup.HidePurchasePopup(); return; }   // the Hide hook tears us down
        if (m_confirming) return;   // mid-purchase: ignore further input until the popup resolves

        if (AccessibleKey.READ_NEXT_ITEM.IsPressed() || AccessibleKey.READ_PREV_MENU_OPTION.IsPressed() || CollectionUi.AddCopyPressed()) Step(+1);
        else if (AccessibleKey.READ_PREV_ITEM.IsPressed() || AccessibleKey.READ_NEXT_MENU_OPTION.IsPressed() || CollectionUi.RemoveCopyPressed()) Step(-1);
        else if (AccessibleKey.CONFIRM.IsPressed()) Confirm();
    }

    public string GetHelp() => LocalizationUtils.Get(LocalizationKey.COLLECTION_CRAFT_HELP);

    // Also on the way back from an error / pending overlay that sat on top of us.
    public void OnGainedFocus()
    {
        m_confirming = false;
        AccessibilityMgr.Output(this, LocalizationUtils.Format(LocalizationKey.COLLECTION_TITLE_BODY, Title(), Details()), interrupt: true);
    }

    private static string Title() => LocalizationUtils.Get(LocalizationKey.COLLECTION_EXCHANGE);

    // currentlyVisible flips synchronously in Show/Hide (the GameObject only goes inactive after the hide animation),
    // so it's the reliable "is the popup up" signal.
    private bool IsOpen()
    {
        if (m_popup == null) return false;
        var anim = m_popup.AnimatedSetActive;
        return anim != null ? anim.currentlyVisible : m_popup.gameObject.activeInHierarchy;
    }

    private void Step(int dir)
    {
        var before = Count();
        if (dir > 0) m_popup!.IncreaseCardCountActions();
        else m_popup!.DecreaseCardCountActions();
        if (Count() == before)
        {
            AccessibilityMgr.Output(this, LocalizationUtils.Get(dir > 0 ? LocalizationKey.COLLECTION_MAXIMUM : LocalizationKey.COLLECTION_MINIMUM));
            return;
        }
        AccessibilityMgr.Output(this, CountAndCost());
    }

    private void Confirm()
    {
        m_confirming = true;
        m_ownedAtConfirm = Owned();
        AccessibilityMgr.Output(this, LocalizationUtils.Get(LocalizationKey.COLLECTION_EXCHANGING));
        try { m_popup!.ConfirmPurchaseButtonActions(); }
        catch { m_confirming = false; throw; }
    }

    // "3 cards, cost 225 Credits, 1 owned".
    private string Details()
    {
        var owned = Owned();
        return owned >= 0 ? LocalizationUtils.Format(LocalizationKey.COMMON_STATE_FORMAT, CountAndCost(), LocalizationUtils.Format(LocalizationKey.CARD_OWNED, owned)) : CountAndCost();
    }

    private string CountAndCost()
    {
        int n = Count(), cost = Cost();
        var s = LocalizationUtils.Format(n == 1 ? LocalizationKey.COLLECTION_ONE_CARD : LocalizationKey.COLLECTION_CARDS, n);
        if (cost > 0) s = LocalizationUtils.Format(LocalizationKey.COMMON_STATE_FORMAT, s, LocalizationUtils.Format(LocalizationKey.COLLECTION_COST, $"{cost} {CreditsWord()}"));
        return s;
    }

    public static string CreditsWord() => LocalizationUtils.GetGame("currency_tc", LocalizationKey.SHOP_CREDITS).Trim();

    private int Count() => ReadInt(s_count, 1);
    private int Owned() => ReadInt(s_owned, -1);
    private int Cost() => ReadInt(s_cost, -1);

    private int ReadInt(FieldInfo? f, int fallback) => m_popup != null && f?.GetValue(m_popup) is int v ? v : fallback;

    private void Hide()
    {
        m_popup = null;
        m_confirming = false;
        m_ownedAtConfirm = -1;
        AccessibilityMgr.HideUI(this);
    }

    private void Open(CardPurchasePopupParts popup)
    {
        m_popup = popup;
        m_confirming = false;
        m_ownedAtConfirm = -1;
        if (AccessibilityMgr.IsFocused(this)) OnGainedFocus();
        else AccessibilityMgr.ShowUI(this);
    }

    private void Closed(CardPurchasePopupParts popup)
    {
        if (popup != m_popup) return;
        // A purchase we issued that bumped the owned count = a successful craft (independent of m_confirming, which an
        // intervening error/pending overlay may already have cleared).
        if (m_ownedAtConfirm >= 0 && Owned() > m_ownedAtConfirm && (AccessibleCardDetail.IsOpen || AccessibleCardDexDetail.IsOpen))
            JustCrafted = true;
        Hide();
    }

    [HarmonyPatch(typeof(CardPurchasePopupParts), "ShowPurchasePopup")]
    private static class ShowHook
    {
        private static void Postfix(CardPurchasePopupParts __instance) => s_instance.Open(__instance);
    }

    [HarmonyPatch(typeof(CardPurchasePopupParts), "HidePurchasePopup")]
    private static class HideHook
    {
        private static void Postfix(CardPurchasePopupParts __instance) => s_instance.Closed(__instance);
    }
}
