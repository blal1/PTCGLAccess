using System.Collections;
using System.Linq;
using HarmonyLib;

namespace Accessibility;

// "View all bundles" overlay — driver: BundlesOverlay (an OverlayScreenGroup, an EnhancedScroller of month-grouped
// bundle rows), opened from the BUNDLES tab's VIEW ALL button. Without us it pops over the HUB and traps a keyboard
// user (focus stays on the shop behind it).
//
// One scroller cell is one month: we show the month the player is on and drive the scroller to the next one with its
// own JumpToDataIndex (Tab, or Up/Down past the edge of the month), then wait for the cell to spawn. Bundle tiles read
// "name, price, N available" (UiLabels.Describe → ShopLabels). Recycling or late tiles rebuild the list in place.
internal sealed class AccessibleBundles : AccessibleUI
{
    private static readonly AccessibleBundles s_instance = new();

    private readonly KeyboardNavigator m_nav;
    private BundlesOverlay? m_overlay;
    private string m_sig = "";
    private int m_group;
    private bool m_announceGroup;

    private AccessibleBundles()
    {
        m_nav = new KeyboardNavigator(this) { AllowTab = false, OnBack = Close };
    }

    public void HandleInput()
    {
        if (m_overlay == null || !m_overlay.gameObject.activeInHierarchy) { Hide(); return; }
        var row = CurrentRow();
        var sig = row == null ? "" : string.Join(",", row.productCollection.GetComponentsInChildren<ShopProductController>(includeInactive: false).Select(p => p.GetItemSetID()));
        if (sig != m_sig) { m_sig = sig; Rebuild(row); }

        if (AccessibleKey.NEXT_SECTION.IsPressed()) { MoveGroup(1); return; }
        if (AccessibleKey.PREV_SECTION.IsPressed()) { MoveGroup(-1); return; }
        if (AccessibleKey.READ_NEXT_MENU_OPTION.IsPressed() && m_nav.IsAtBottomRow) { MoveGroup(1); return; }
        if (AccessibleKey.READ_PREV_MENU_OPTION.IsPressed() && m_nav.IsAtTopRow) { MoveGroup(-1); return; }
        m_nav.HandleInput();
    }

    public string GetHelp() => LocalizationUtils.Get(LocalizationKey.SHOPUI_BUNDLES_HELP);

    // Back from a product overlay: same month, same bundle.
    public void OnGainedFocus()
    {
        if (m_announceGroup || m_nav.Current == null)
        {
            AccessibilityMgr.Output(this, LocalizationUtils.Get(LocalizationKey.SHOPUI_ALL_BUNDLES), interrupt: true);
            return;
        }
        m_nav.RefocusCurrent();
    }

    private ProductCollectionCellRow? CurrentRow()
    {
        var cells = m_overlay!.scrollView.ActiveCellViews;
        for (var i = 0; i < cells.Count; i++)
            if (cells[i] != null && cells[i].dataIndex == m_group && cells[i] is ProductCollectionCellRow row) return row;
        return null;
    }

    private void Rebuild(ProductCollectionCellRow? row)
    {
        var keep = m_nav.Index;
        var items = row != null ? AccessibleShop.ProductButtons(row.productCollection) : new System.Collections.Generic.List<UnityEngine.UI.Selectable>();
        m_nav.SetItems(items);   // default resolver → UiLabels.Describe → "name, price, N available"
        if (items.Count == 0) return;   // cells not spawned yet: the next change lands here again
        if (m_announceGroup)
        {
            m_announceGroup = false;
            AccessibilityMgr.Output(this, ShopLabels.CollectionTitle(row!.productCollection), interrupt: false);
            m_nav.FocusFirst(announce: true, interruptPrior: false);
            return;
        }
        m_nav.FocusIndex(System.Math.Min(System.Math.Max(keep, 0), items.Count - 1), announce: false);
    }

    private void MoveGroup(int dir)
    {
        var count = Groups();
        var next = m_group + dir;
        if (next < 0 || next >= count) return;
        m_group = next;
        m_sig = "";
        m_announceGroup = true;
        m_nav.SetItems(new System.Collections.Generic.List<UnityEngine.UI.Selectable>());
        m_overlay!.scrollView.JumpToDataIndex(m_group);
    }

    private int Groups() => GameReflect.ReadField<IList>(m_overlay, "productGroups")?.Count ?? 0;

    private void Close() => m_overlay?.Deactivate();

    private void Hide()
    {
        m_overlay = null;
        AccessibilityMgr.HideUI(this);
    }

    private void Open(BundlesOverlay overlay)
    {
        m_overlay = overlay;
        m_group = 0;
        m_sig = "";
        m_announceGroup = true;
        m_nav.SetItems(new System.Collections.Generic.List<UnityEngine.UI.Selectable>());
        if (AccessibilityMgr.IsFocused(this)) OnGainedFocus();
        else AccessibilityMgr.ShowUI(this);
    }

    [HarmonyPatch(typeof(BundlesOverlay), "OnActivate", new System.Type[] { })]
    private static class OnActivateHook
    {
        private static void Postfix(BundlesOverlay __instance) => s_instance.Open(__instance);
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
