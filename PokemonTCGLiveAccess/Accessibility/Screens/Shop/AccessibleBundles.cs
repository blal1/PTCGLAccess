using System.Linq;
using HarmonyLib;

namespace Accessibility;

// "View all bundles" overlay — driver: BundlesOverlay (an OverlayScreenGroup, an EnhancedScroller of month-grouped
// bundle rows), opened from the BUNDLES tab's VIEW ALL button. Without us it pops over the HUB and traps a keyboard
// user (focus stays on the shop behind it).
//
// We navigate the bundle tiles it has spawned (same "name, price, N available" label as the main tab, via
// UiLabels.Describe → ShopLabels) and close on Escape. The scroller instantiates cells asynchronously and recycles them
// on scroll, so a product-signature poll rebuilds the nav as the spawned set changes. NOTE: rows beyond the first
// screenful need the scroller driven by keyboard — not done yet; the curated tabs and EXPANSIONS cover browsing.
internal sealed class AccessibleBundles : AccessibleUI
{
    private static readonly AccessibleBundles s_instance = new();

    private readonly KeyboardNavigator m_nav;
    private BundlesOverlay? m_overlay;
    private string m_sig = "";
    private bool m_announced;

    private AccessibleBundles()
    {
        m_nav = new KeyboardNavigator(this) { AllowTab = false, OnBack = Close };
    }

    public void HandleInput()
    {
        if (m_overlay == null || !m_overlay.gameObject.activeInHierarchy) { Hide(); return; }
        if (ProductSig() != m_sig) { Rebuild(announce: !m_announced); return; }
        m_nav.HandleInput();
    }

    public string GetHelp() => LocalizationUtils.Get(LocalizationKey.SHOPUI_BUNDLES_HELP);

    public void OnGainedFocus()
    {
        m_announced = false;
        Rebuild(announce: true);
    }

    private void Rebuild(bool announce)
    {
        if (m_overlay == null) return;
        var items = AccessibleShop.ProductButtons(m_overlay);
        m_nav.SetItems(items);   // default resolver → UiLabels.Describe → "name, price, N available"
        m_sig = ProductSig();
        if (!announce) return;
        AccessibilityMgr.Output(this, LocalizationUtils.Get(LocalizationKey.SHOPUI_ALL_BUNDLES), interrupt: true);
        if (items.Count == 0) return;   // cells not spawned yet — the poll announces them once they appear
        m_announced = true;
        m_nav.FocusFirst(announce: true, interruptPrior: false);
    }

    private string ProductSig() =>
        m_overlay == null ? "none" : string.Join(",", m_overlay.GetComponentsInChildren<ShopProductController>(includeInactive: false).Select(p => p.GetItemSetID()));

    private void Close() => m_overlay?.Deactivate();

    private void Hide()
    {
        m_overlay = null;
        AccessibilityMgr.HideUI(this);
    }

    private void Open(BundlesOverlay overlay)
    {
        m_overlay = overlay;
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
