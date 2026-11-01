using System.Collections.Generic;
using System.Reflection;
using CardDatabase.DataAccess;
using HarmonyLib;
using RainierClientSDK.source.Commerce;
using SharedLogicUtils.source.DataTypes;
using UnityEngine.UI;

namespace Accessibility;

// Product purchase overlay — driver: ShopProductOverlay (an OverlayScreenGroup), opened when a shop tile is activated
// (ShopProductController.OnClick). It shows the product header (name + price + availability, a ShopProductController =
// productHeader), the breakdown of what's inside (a row of ProductItemSummarySlot — cards / boosters / currency / a
// deck), and a CONFIRM PURCHASE button (+ booster odds when the bundle includes a pack).
//
// We announce the header and a spoken "Contains …" summary on entry, then present the real action buttons for Enter;
// Escape closes the overlay. Purchase routes through the game's own OnClickPurchase (the button's wired onClick) —
// never a parallel purchase call. A completed purchase tears the overlay down and chains into the reward reveal.
// ShopProductOverlay doesn't override OnDeActivate, so the close hook is the shared OverlayScreenGroup.OnDeActivate,
// identity-checked; the HandleInput backstop covers a teardown that skips it.
internal sealed class AccessibleShopProduct : AccessibleUI
{
    private static readonly AccessibleShopProduct s_instance = new();

    // ProductItemSummarySlot.currentItem is an ItemData STRUCT and numberOfItems an int — value types, read by FieldInfo.
    private static readonly FieldInfo? s_currentItem = AccessTools.Field(typeof(ProductItemSummarySlot), "currentItem");
    private static readonly FieldInfo? s_numberOfItems = AccessTools.Field(typeof(ProductItemSummarySlot), "numberOfItems");

    private readonly KeyboardNavigator m_nav;
    private ShopProductOverlay? m_overlay;
    private Button? m_oddsButton;
    private Button? m_buyButton;
    private readonly Dictionary<Selectable, ProductItemSummarySlot> m_slotButtons = new();
    private bool m_fresh;

    private AccessibleShopProduct()
    {
        m_nav = new KeyboardNavigator(this) { AllowTab = false, AllowHorizontal = true, OnBack = Close };
    }

    public void HandleInput()
    {
        if (m_overlay == null || !m_overlay.gameObject.activeInHierarchy) { Hide(); return; }
        m_nav.HandleInput();
    }

    public string GetHelp() => LocalizationUtils.Get(LocalizationKey.SHOPUI_PRODUCT_HELP);

    public void OnGainedFocus()
    {
        if (m_overlay == null) return;
        // Back from a card, deck or odds view opened from a slot: same place.
        if (!m_fresh && m_nav.Current != null) { m_nav.RefocusCurrent(); return; }
        m_fresh = false;
        var buttons = ActionButtons();
        m_nav.SetItems(buttons, Label);
        m_nav.ForceFlatVertical();
        var header = m_overlay.productHeader != null ? ShopLabels.Product(m_overlay.productHeader) : LocalizationUtils.Get(LocalizationKey.SHOP_PRODUCT);
        AccessibilityMgr.Output(this, header, interrupt: true);
        if (Contents() is { Length: > 0 } contents) AccessibilityMgr.Output(this, contents, interrupt: false);
        if (buttons.Count > 0) m_nav.FocusFirst(announce: true, interruptPrior: false);
    }

    // CONFIRM PURCHASE first (the primary action), then booster odds when present, then the content slots with something
    // to open: cards, boosters and decks (MainMenuController.OnClickProduct has nothing to show for the others).
    private List<Selectable> ActionButtons()
    {
        var result = new List<Selectable>();
        m_oddsButton = null;
        m_slotButtons.Clear();
        var buy = m_overlay!.productHeader != null ? m_overlay.productHeader.purchaseButton : null;
        m_buyButton = buy;
        if (buy != null && buy.isActiveAndEnabled) result.Add(buy);
        var odds = m_overlay.boosterButton;
        if (odds != null && odds.activeInHierarchy)
        {
            m_oddsButton = odds.GetComponent<Button>() ?? odds.GetComponentInChildren<Button>(includeInactive: false);
            if (m_oddsButton != null && m_oddsButton.isActiveAndEnabled) result.Add(m_oddsButton);
        }
        foreach (var slot in Slots())
        {
            if (s_currentItem?.GetValue(slot) is not ItemData item) continue;
            if (item.itemType is not (ItemData.ItemType.Card or ItemData.ItemType.Booster or ItemData.ItemType.BoosterParent or ItemData.ItemType.Deck)) continue;
            var button = slot.GetComponentInChildren<Button>(includeInactive: false);
            if (button == null || !button.isActiveAndEnabled) continue;
            m_slotButtons[button] = slot;
            result.Add(button);
        }
        return result;
    }

    // The buy button sits inside the product header, so the generic label would be the product's name; and when the player
    // can't afford it the game turns it into a Close button: its own text says which.
    private string Label(Selectable s)
    {
        if (m_oddsButton != null && s == m_oddsButton) return LocalizationUtils.Format(LocalizationKey.SHOPUI_OPENS_BROWSER, LocalizationUtils.Get(LocalizationKey.SHOPUI_BOOSTER_ODDS));
        if (m_buyButton != null && s == m_buyButton && CollectionUi.Text(m_overlay?.purchaseButtonText) is { } buy) return buy;
        if (m_slotButtons.TryGetValue(s, out var slot) && SlotName(slot) is { } name) return name;
        return KeyboardNavigator.DefaultLabel(s);
    }

    // OnActivate destroys the previous product's slots with a deferred Destroy, so a scan of the holder still finds them
    // this frame: the game's own list is the only one that is right.
    private List<ProductItemSummarySlot> Slots() =>
        GameReflect.ReadField<List<ProductItemSummarySlot>>(m_overlay, "spawnedProductItems") ?? new List<ProductItemSummarySlot>();

    private static string? SlotName(ProductItemSummarySlot slot)
    {
        if (s_currentItem?.GetValue(slot) is not ItemData item || item.itemType == ItemData.ItemType.None) return null;
        var n = s_numberOfItems?.GetValue(slot) is int i && i > 0 ? i : item.amount > 0 ? item.amount : 1;
        var name = ItemName(item);
        if (string.IsNullOrWhiteSpace(name)) return null;
        return n > 1 ? LocalizationUtils.Format(LocalizationKey.SHOPUI_TIMES, name, n) : name;
    }

    // "Contains <item>, <item> …" read from the spawned ProductItemSummarySlots — cards by name, currencies by name,
    // everything else by its content loc / item type.
    private string Contents()
    {
        var parts = new List<string>();
        foreach (var slot in Slots())
            if (SlotName(slot) is { } name) parts.Add(name);
        return parts.Count > 0 ? LocalizationUtils.Format(LocalizationKey.SHOPUI_CONTAINS, string.Join(", ", parts)) : "";
    }

    private static string ItemName(ItemData item)
    {
        if (item.itemType == ItemData.ItemType.Card && !string.IsNullOrEmpty(item.clientId)
            && ManagerSingleton<CardDatabaseManager>.instance?.TryGetCardFromDatabase(item.clientId) is { } row)
            return CardText.NameWithType(row);

        switch (item.itemType)
        {
            case ItemData.ItemType.HardCurrency: return ShopLabels.CurrencyWord(CurrencyType.HardCurrency);
            case ItemData.ItemType.TraderCurrency: return ShopLabels.CurrencyWord(CurrencyType.TradeCurrency);
            case ItemData.ItemType.TokenCurrency: return ShopLabels.CurrencyWord(CurrencyType.TokenCurrency);
            case ItemData.ItemType.SoftCurrency: return ShopLabels.CurrencyWord(CurrencyType.SoftCurrency);
        }

        if (LocalizationUtils.TryGetGame(item.contentLocId)?.Trim() is { Length: > 0 } loc) return loc;

        return LocalizationUtils.Get(item.itemType switch
        {
            ItemData.ItemType.Booster or ItemData.ItemType.BoosterParent => LocalizationKey.SHOPUI_ITEM_BOOSTER,
            ItemData.ItemType.Coin => LocalizationKey.SHOPUI_ITEM_COIN,
            ItemData.ItemType.Sleeve => LocalizationKey.SHOPUI_ITEM_SLEEVE,
            ItemData.ItemType.DeckBox => LocalizationKey.SHOPUI_ITEM_DECK_BOX,
            ItemData.ItemType.Deck => LocalizationKey.SHOPUI_ITEM_DECK,
            ItemData.ItemType.Avatar => LocalizationKey.SHOPUI_ITEM_AVATAR,
            ItemData.ItemType.BuildBattleBox => LocalizationKey.SHOPUI_ITEM_BUILD_BATTLE,
            ItemData.ItemType.Feature => LocalizationKey.SHOPUI_ITEM_FEATURE,
            _ => LocalizationKey.SHOPUI_ITEM,
        });
    }

    private void Close() => m_overlay?.Deactivate();

    private void Hide()
    {
        m_overlay = null;
        AccessibilityMgr.HideUI(this);
    }

    private void Open(ShopProductOverlay overlay)
    {
        m_overlay = overlay;
        m_fresh = true;
        if (AccessibilityMgr.IsFocused(this)) OnGainedFocus();
        else AccessibilityMgr.ShowUI(this);
    }

    [HarmonyPatch(typeof(ShopProductOverlay), "OnActivate", new System.Type[] { })]
    private static class OnActivateHook
    {
        private static void Postfix(ShopProductOverlay __instance) => s_instance.Open(__instance);
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
