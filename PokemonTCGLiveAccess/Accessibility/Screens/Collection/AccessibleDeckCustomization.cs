using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using SharedLogicUtils.source.DataTypes;
using SharedSDKUtils;
using TMPro;
using TPCI.Localization;
using UnityEngine.UI;
using static Accessibility.GameReflect;

namespace Accessibility;

// Deck customization screen — HUBDeckCustomizationController: deck box, sleeve and coin for one deck.
//
// The item grid is a virtualized scroller, so items are moved with the controller's own SelectNextItem, not its cells.
// That is also what a click on an item amounts to, with a side effect the player must know about: selecting an OWNED
// item assigns it to the deck at once (AssignSelectionToDeck). An unowned one only sets up the buy button, whose
// confirmation is a generic modal (AccessibleModalContainer) and whose wait is AccessiblePurchaseRequest.
//
// SetupForDeck is the hook, not OnActivate: this class does not override OnActivate, and the controller only
// finishes preparing the category after Activate() returns.
internal sealed class AccessibleDeckCustomization : AccessibleScreen
{
    private static readonly AccessibleDeckCustomization s_instance = new();

    private static readonly FieldInfo? s_showUnowned = AccessTools.Field(typeof(HUBDeckCustomizationController), "showUnowned");
    private static readonly MethodInfo? s_isOwned = AccessTools.Method(typeof(HUBDeckCustomizationController), "IsCurrentSelectionOwned");
    private static readonly MethodInfo? s_costInfo = AccessTools.Method(typeof(HUBDeckCustomizationController), "TryGetSelectedItemCostInfo");

    private const int CategoryCount = (int)DeckCustomizationType.NumTypes;

    private readonly KeyboardNavigator m_nav;
    private HUBDeckCustomizationController? m_controller;
    private int m_notCurrentFrames;

    private AccessibleDeckCustomization()
    {
        m_nav = new KeyboardNavigator(this);
    }

    public void HandleInput()
    {
        if (m_controller == null) return;
        // Same guard as the deck editor: some HUB paths leave a screen without its OnDeactivate.
        var nav = ManagerSingleton<RainierManager>.instance?._mainMenuController?.navigation;
        if (nav != null && nav.currentScreen != null && nav.currentScreen != m_controller)
        {
            if (++m_notCurrentFrames >= 30) Deactivated(m_controller);
            return;
        }
        m_notCurrentFrames = 0;

        if (CollectionUi.BackPressed()) { m_controller.BackButtonActions(); return; }
        if (AccessibleKey.NEXT_SECTION.IsPressed()) { MoveCategory(1); return; }
        if (AccessibleKey.PREV_SECTION.IsPressed()) { MoveCategory(-1); return; }
        if (AccessibleKey.READ_NEXT_ITEM.IsPressed()) { MoveItem(1); return; }
        if (AccessibleKey.READ_PREV_ITEM.IsPressed()) { MoveItem(-1); return; }

        if (AccessibleKey.CONFIRM.IsPressed())
        {
            if (m_nav.Current is TMP_Dropdown dropdown)
            {
                // The list a dropdown pops is unreachable by keyboard: step its value, which fires the same onValueChanged.
                if (dropdown.options.Count > 0) dropdown.value = (dropdown.value + 1) % dropdown.options.Count;
                ReadItem(interrupt: true);
                m_nav.RefocusCurrent(interrupt: false);
                return;
            }
            m_nav.Activate(isSpaceKey: m_nav.Current is Toggle);
            RebuildControls();
            return;
        }
        m_nav.HandleInput();
    }

    public string GetHelp() => LocalizationUtils.Get(LocalizationKey.COLLECTION_CUSTOMIZE_HELP);

    public void OnGainedFocus()
    {
        if (m_controller == null) return;
        RebuildControls();
        var name = ReadField<DeckInfo>(m_controller, "currentDeck")?.deckName;
        var title = LocalizationUtils.Get(LocalizationKey.COLLECTION_CUSTOMIZE);
        AccessibilityMgr.Output(this, string.IsNullOrEmpty(name) ? title : LocalizationUtils.Format(LocalizationKey.COLLECTION_TITLE_BODY, title, name!), interrupt: true);
        if (CollectionUi.Text(ReadField<LocText>(m_controller, "itemDescriptionBody")) is { } showing) AccessibilityMgr.Output(this, showing, interrupt: false);
        ReadCategory(interrupt: false);
    }

    private void MoveItem(int dir)
    {
        if (ItemList().Count == 0) return;   // the game's own SetSelectedItem would throw on an empty category
        m_controller!.SelectNextItem(dir);
        RebuildControls();
        ReadItem(interrupt: true);
    }

    private void MoveCategory(int dir)
    {
        m_controller!.SelectCategory((m_controller.SelectedCategory + dir + CategoryCount) % CategoryCount);
        RebuildControls();
        ReadCategory(interrupt: true);
    }

    private void ReadCategory(bool interrupt)
    {
        if (CollectionUi.Text(ReadField<LocText>(m_controller, "categoryTitle")) is { } title) AccessibilityMgr.Output(this, title, interrupt);
        ReadItem(interrupt: false);
    }

    private void ReadItem(bool interrupt)
    {
        var list = ItemList();
        var position = list.IndexOf(m_controller!.SelectedItem);
        AccessibilityMgr.Output(this, Announce.Item(ItemLabel(), null, position + 1, list.Count), interrupt);
    }

    private List<int> ItemList()
    {
        var showUnowned = s_showUnowned?.GetValue(m_controller) is true;
        return ReadField<List<int>>(m_controller, showUnowned ? "ownedOrPurchasableIndexList" : "ownedIndexList") ?? new List<int>();
    }

    private string ItemLabel()
    {
        var name = CollectionUi.Text(ReadField<LocText>(m_controller, "selectedItemNameDisplay")) ?? LocalizationUtils.Get(LocalizationKey.SHOPUI_ITEM);
        if (s_isOwned?.Invoke(m_controller, null) is true) return LocalizationUtils.Format(LocalizationKey.COLLECTION_CUSTOM_EQUIPPED, name);
        var args = new object?[] { CurrencyType.None, 0 };
        s_costInfo?.Invoke(m_controller, args);
        var word = args[0] is CurrencyType type ? ShopLabels.CurrencyWord(type) : "";
        return LocalizationUtils.Format(LocalizationKey.COLLECTION_CUSTOM_BUYABLE, name, $"{args[1]} {word}".Trim());
    }

    // Everything but the item cells and the category/preview toggles, which Tab and Left/Right already cover.
    private void RebuildControls()
    {
        var keep = m_nav.Current;
        var categoryGroup = ReadField<ImprovedToggle>(m_controller, "deckBoxToggle")?.ParentGroupID ?? -1;
        var items = m_controller!.GetComponentsInChildren<Selectable>(includeInactive: false)
            .Where(s => (s is Button || s is Toggle || s is TMP_Dropdown) && s.isActiveAndEnabled && s.GetComponentInParent<CustomizationItem>() == null)
            .Where(s => s is not Toggle || s.GetComponent<ImprovedToggle>() is not { } t || t.ParentGroupID == -1 || t.ParentGroupID != categoryGroup)
            .Where(s => s != ReadField<ImprovedToggle>(m_controller, "previewAllToggle")?.MyToggle && s != ReadField<ImprovedToggle>(m_controller, "focusModeToggle")?.MyToggle);
        var ordered = CollectionUi.InReadingOrder(items);
        m_nav.SetItems(ordered, ControlLabel);
        m_nav.ForceFlatVertical();
        var i = keep != null ? ordered.IndexOf(keep) : -1;
        if (i >= 0) m_nav.FocusIndex(i, announce: false);
    }

    private string ControlLabel(Selectable s)
    {
        if (s is TMP_Dropdown dropdown)
        {
            var sort = LocalizationUtils.GetGame("global_sortby", LocalizationKey.COLLECTION_SORT_BY);
            return LocalizationUtils.Format(LocalizationKey.COMMON_STATE_FORMAT, sort, CollectionUi.Text(dropdown.captionText) ?? "");
        }
        var purchase = ReadField<PurchaseButton>(m_controller, "purchaseButtonLandscape");
        if (purchase != null && s == ReadField<Button>(purchase, "buyButton"))
            return LocalizationUtils.GetGame("global_purchase", LocalizationKey.COLLECTION_BUY);
        return KeyboardNavigator.DefaultLabel(s);
    }

    private void Activated(HUBDeckCustomizationController controller)
    {
        m_controller = controller;
        m_notCurrentFrames = 0;
        if (AccessibilityMgr.CurrentScreen == this) { if (AccessibilityMgr.IsFocused(this)) OnGainedFocus(); }
        else AccessibilityMgr.SetScreen(this);
    }

    private void Deactivated(HUBDeckCustomizationController controller)
    {
        if (controller != m_controller) return;
        m_controller = null;
        AccessibilityMgr.ClearScreen(this);
    }

    [HarmonyPatch(typeof(HUBDeckCustomizationController), "SetupForDeck")]
    private static class SetupForDeckHook
    {
        private static void Postfix(HUBDeckCustomizationController __instance)
        {
            // SetupForDeck refuses when the shop data has not loaded, with a message of its own.
            if (__instance.SelectedCategory >= 0) s_instance.Activated(__instance);
        }
    }

    [HarmonyPatch(typeof(HUBDeckCustomizationController), "OnDeactivate")]
    private static class OnDeactivateHook
    {
        private static void Postfix(HUBDeckCustomizationController __instance) => s_instance.Deactivated(__instance);
    }
}
