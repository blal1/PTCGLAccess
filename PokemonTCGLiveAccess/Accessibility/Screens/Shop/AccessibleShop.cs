using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using TMPro;
using TPCI.Localization;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;
using static Accessibility.GameReflect;

namespace Accessibility;

// SHOP screen — driver: HUBShopScreenController (a HUBScreenController, so the lifecycle hook matches HOME/Profile:
// postfix OnActivate / OnDeactivate).
//
// The shop has three sub-tabs (BUNDLES / EXPANSIONS / REDEEM), each a ShopPageSubScreen built by ShopBuilder. We
// discover the *current* sub-screen via builder.GetPageDragManager().GetCurrentScreen() and present its content as a
// section stack (top-to-bottom; Up/Down escapes between sections, Left/Right within, Tab cycles — the same model as
// HOME):
//   1. HUB top tab strip (HOME / DECKS / … / CARD DEX) + overflow.
//   2. Shop sub-tabs (BUNDLES / EXPANSIONS / REDEEM) — PageDragTab toggles (radio).
//   3..N. One section PER product collection (RECOMMENDED / NEW ARRIVALS / decks / boosters …), named by the
//         collection's header title; each item is a product tile read as "name, price, N available"
//         (UiLabels.Describe → ShopLabels). Plus a trailing "extras" section for screen-level controls (VIEW ALL, set
//         picker, …). On REDEEM: the code-entry controls, then a separate Rewards section.
//
// The sub-tabs switch content asynchronously (LoadContent is a coroutine), so a content-signature poll rebuilds the
// sections when the active page or its product count changes — never losing a keypress in the load window. A coached
// tutorial control (FtueTracker.FocusedElement) wins over everything, as on every HUB screen.
internal sealed class AccessibleShop : AccessibleScreen
{
    private static readonly AccessibleShop s_instance = new();

    private HUBShopScreenController? m_controller;
    private readonly List<KeyboardNavigator> m_groups = new();
    private int m_active = -1;
    private int m_tabGroup = -1, m_firstContentGroup = -1, m_rewardsGroup = -1;
    private bool m_fresh;
    private string m_lastContentSig = "";

    // REDEEM rewards: subscribe to the scanned list's own update event (a clean signal, no polling) so a scanned code
    // surfaces its reward and we focus into it like a modal.
    private ScannedCardListView? m_redeemList;
    private UnityAction? m_redeemListener;
    private int m_lastScannedCount;
    private bool m_redeemResultPending;   // a failed submit just fired; speak its status message next frame

    // Tutorial: the coached control, presented alone.
    private readonly KeyboardNavigator m_ftueNav;
    private Selectable? m_ftueShown;

    private AccessibleShop()
    {
        m_ftueNav = new KeyboardNavigator(this) { AllowTab = false };
    }

    public string GetHelp() => LocalizationUtils.Get(LocalizationKey.SHOPUI_HELP);

    public void OnGainedFocus()
    {
        if (m_controller == null) return;
        if (FtueFocus() != null) { m_ftueShown = null; UpdateFtue(); return; }
        if (m_fresh || m_groups.Count == 0)
        {
            m_fresh = false;
            Rebuild();
            if (m_groups.Count == 0) return;
            m_active = DefaultGroup();
            var tab = ActiveTabName();
            var title = LocalizationUtils.Get(LocalizationKey.SHOPUI_SHOP);
            AccessibilityMgr.Output(this, tab == null ? title : LocalizationUtils.Format(LocalizationKey.COMMON_STATE_FORMAT, title, tab), interrupt: true);
            FocusGroup(m_active, announce: true, interrupt: false);
            return;
        }
        // Back from a product overlay / popup: same section, same item, unless the content changed.
        if (ContentSig() != m_lastContentSig) OnContentChanged();
        if (m_active >= 0 && m_active < m_groups.Count) m_groups[m_active].RefocusCurrent(announce: true, interrupt: true);
    }

    public void HandleInput()
    {
        if (m_controller == null) return;
        if (UpdateFtue()) return;

        var sig = ContentSig();
        if (sig != m_lastContentSig) { OnContentChanged(); return; }
        if (m_redeemResultPending) AnnounceRedeemResult();
        if (m_groups.Count == 0 || m_active < 0) return;

        var nav = m_groups[m_active];
        // In a text field (redeem code box), arrows are caret movement and Tab exits the field — so suppress the
        // arrow-based section escape and turn Tab into a move WITHIN the section.
        var editing = nav.CurrentIsTextField;

        if (AccessibleKey.NEXT_SECTION.IsPressed() || AccessibleKey.PREV_SECTION.IsPressed())
        {
            var dir = AccessibleKey.PREV_SECTION.IsPressed() ? -1 : 1;
            if (editing)
            {
                var before = nav.Index;
                if (dir > 0) nav.MoveNext(); else nav.MovePrev();
                if (nav.Index != before) return;   // left the field for the next control in the section
            }
            if (m_groups.Count > 1) FocusGroup((m_active + dir + m_groups.Count) % m_groups.Count, announce: true, interrupt: true);
            return;
        }
        if (!editing && AccessibleKey.READ_PREV_MENU_OPTION.IsPressed() && m_active > 0 && nav.IsAtTopRow)
        {
            var col = nav.CurrentColumn;
            m_active--;
            if (m_active == m_tabGroup) FocusActiveTab(announce: true);
            else m_groups[m_active].FocusLastRow(col, announce: true, interruptPrior: true);
            return;
        }
        if (!editing && AccessibleKey.READ_NEXT_MENU_OPTION.IsPressed() && m_active < m_groups.Count - 1 && nav.IsAtBottomRow)
        {
            var col = nav.CurrentColumn;
            m_active++;
            m_groups[m_active].FocusFirstRow(col, announce: true, interruptPrior: true);
            return;
        }
        nav.HandleInput();
    }

    private void FocusGroup(int i, bool announce, bool interrupt)
    {
        m_active = i;
        if (i == m_tabGroup) FocusActiveTab(announce);
        else m_groups[i].FocusFirst(announce, interrupt);
    }

    // Focus the selected sub-tab (the toggle that's on), so escaping up into the tab strip — or rebuilding after a
    // switch — lands on the active tab rather than the first.
    private void FocusActiveTab(bool announce)
    {
        if (m_tabGroup < 0 || m_tabGroup >= m_groups.Count) return;
        var kn = m_groups[m_tabGroup];
        for (var i = 0; i < kn.Items.Count; i++)
            if (kn.Items[i] is Toggle t && t.isOn) { kn.FocusIndex(i, announce, interrupt: true); return; }
        kn.FocusFirst(announce);
    }

    // ── Groups ──

    private void Rebuild()
    {
        m_groups.Clear();
        m_tabGroup = m_firstContentGroup = m_rewardsGroup = -1;
        m_lastContentSig = ContentSig();
        if (m_controller == null) return;

        var known = new HashSet<Selectable>();
        var top = CollectionUi.TopStrip();
        if (top.Count > 0) { AddGroup(top, UiLabels.TopNavLabel); known.UnionWith(top); }

        // Shop sub-tabs (BUNDLES / EXPANSIONS / REDEEM) — PageDragTab toggles, radio.
        var tabs = CollectionUi.InReadingOrder(m_controller.GetComponentsInChildren<PageDragTab>(includeInactive: false)
            .Select(t => t.toggle).Where(t => t != null && t.isActiveAndEnabled).Cast<Selectable>());
        if (tabs.Count > 0)
        {
            known.UnionWith(tabs);
            AddGroup(tabs, TabLabel, radio: true, LocalizationUtils.Get(LocalizationKey.SHOPUI_SECTION));
            m_tabGroup = m_groups.Count - 1;
        }

        var sub = ActiveSubScreen();
        if (sub is ShopPageCodeCardScreen redeem)
        {
            SubscribeRedeem(redeem);
            var controls = RedeemInputControls(redeem);
            if (controls.Count > 0) AddContent(controls, RedeemLabel, LocalizationUtils.Get(LocalizationKey.SHOPUI_REDEEM_CODE));
            var rewards = RedeemRewardControls(redeem);
            if (rewards.Count > 0)
            {
                AddContent(rewards, RedeemLabel, LocalizationUtils.Get(LocalizationKey.SHOPUI_REWARDS));
                m_rewardsGroup = m_groups.Count - 1;
            }
        }
        else if (sub != null)
        {
            // One section per product collection plus a trailing "extras" section, all in reading order so side-by-side
            // panels read left to right and footer controls (VIEW ALL) come last.
            var content = new List<(float key, List<Selectable> items, string name)>();
            foreach (var col in ActiveCollections(sub))
            {
                var items = ProductButtons(col);
                if (items.Count == 0) continue;
                known.UnionWith(items);
                content.Add((CollectionUi.ReadingOrder(col.transform), items, ShopLabels.CollectionTitle(col)));
            }
            var extras = ExtrasUnder(sub, known);
            if (extras.Count > 0)
                content.Add((CollectionUi.ReadingOrder(extras[0].transform), extras,
                    LocalizationUtils.Get(sub is ShopPageExpansionsScreen ? LocalizationKey.SHOPUI_CHOOSE_SET : LocalizationKey.SHOPUI_MORE)));
            foreach (var c in content.OrderBy(c => c.key)) AddContent(c.items, null, c.name);
        }
        // else: sub == null → the page-drag builder hasn't mounted the current sub-screen yet (it loads asynchronously).
        // Build no content this pass — the content-signature poll rebuilds the instant it resolves. We must NOT sweep
        // the whole controller as a fallback: the page carousel keeps the adjacent tabs' pages mounted off-screen, so a
        // controller-wide sweep pulls in another tab's products (EXPANSIONS content bleeding onto REDEEM).
    }

    private void AddContent(List<Selectable> items, KeyboardNavigator.LabelResolver? resolver, string name)
    {
        AddGroup(items, resolver, radio: false, name);
        if (m_firstContentGroup < 0) m_firstContentGroup = m_groups.Count - 1;
    }

    private void AddGroup(List<Selectable> items, KeyboardNavigator.LabelResolver? resolver, bool radio = false, string? name = null)
    {
        var g = new KeyboardNavigator(this) { AllowTab = false, RadioToggleMode = radio, GroupName = name, OnBack = OpenOverflow };
        g.SetItems(items, resolver);
        m_groups.Add(g);
    }

    // Escape on any Shop section opens the overflow menu — same as the top-bar button.
    private static void OpenOverflow() => CollectionUi.OpenOverflowMenu();

    private int DefaultGroup() => m_firstContentGroup >= 0 ? m_firstContentGroup : m_tabGroup >= 0 ? m_tabGroup : 0;

    // Signature of the current content: active sub-screen type + page index + product count. Changes only on a tab
    // switch or async product (un)load.
    private string ContentSig()
    {
        var sub = ActiveSubScreen();
        if (sub == null) return "none";
        var page = m_controller?.builder?.GetPageDragManager()?.currentPage ?? -1;
        return $"{sub.GetType().Name}|{page}|{sub.GetComponentsInChildren<ShopProductController>(includeInactive: false).Length}";
    }

    private void OnContentChanged()
    {
        var prev = m_active;
        Rebuild();
        if (m_groups.Count == 0) { m_active = -1; return; }
        m_active = Mathf.Clamp(prev < 0 ? DefaultGroup() : prev, 0, m_groups.Count - 1);
        if (m_active == m_tabGroup) FocusActiveTab(announce: false);
        else m_groups[m_active].RefocusCurrent(announce: false);
    }

    private ShopPageSubScreen? ActiveSubScreen()
    {
        var go = m_controller?.builder?.GetPageDragManager()?.GetCurrentScreen();
        return go != null ? go.GetComponent<ShopPageSubScreen>() : null;
    }

    // The active product collections under the sub-screen. A collection with no products is SetActive(false) by the
    // game, so the filter drops empty ones (e.g. an absent "LEAVING SOON").
    private static IEnumerable<ShopProductCollection> ActiveCollections(Component sub) =>
        sub.GetComponentsInChildren<ShopProductCollection>(includeInactive: false)
            .Where(c => c != null && c.gameObject.activeInHierarchy && c.GetComponentsInChildren<ShopProductController>(includeInactive: false).Length > 0);

    // The clickable button for each product tile (opens the product overlay), in reading order.
    internal static List<Selectable> ProductButtons(Component root) =>
        CollectionUi.InReadingOrder(root.GetComponentsInChildren<ShopProductController>(includeInactive: false)
            .Select(p => p.GetComponentInChildren<Button>(includeInactive: false))
            .Where(b => b != null && b.isActiveAndEnabled).Cast<Selectable>());

    // Screen-level controls on the sub-screen that aren't product tiles: VIEW ALL, the (i) info buttons, the expansion
    // set picker / drawer.
    private static List<Selectable> ExtrasUnder(Component sub, HashSet<Selectable> known) =>
        CollectionUi.InReadingOrder(sub.GetComponentsInChildren<Selectable>(includeInactive: false)
            .Where(s => (s is Button || s is Toggle || s is TMP_InputField || s is InputField) && s.isActiveAndEnabled)
            .Where(s => !known.Contains(s) && s.GetComponentInParent<ShopProductController>() == null)
            // The EXPANSIONS "filter by card series" menu starts collapsed: a CanvasGroup turns its toggles
            // non-interactable + invisible, but the GameObjects stay active — a plain sweep grabbed them as a long list
            // of unlabeled checkboxes. IsInteractable() follows the CanvasGroup, so they come back once it opens.
            .Where(s => s.GetComponentInParent<ExpandableSeriesSelector>() == null || s.IsInteractable()));

    // ── REDEEM ──

    // The code-entry side: the code field (landing spot), SUBMIT, then the worded buttons (CLEAR, DROP RATES). The
    // reward-side buttons (delete / COLLECT ALL) and the desktop camera scanner are excluded.
    private static List<Selectable> RedeemInputControls(ShopPageCodeCardScreen redeem)
    {
        var items = new List<Selectable>();
        var field = ReadField<TMP_InputField>(redeem, "_scanResultInputField");
        if (field != null && field.isActiveAndEnabled) items.Add(field);
        var buttons = redeem.GetComponentsInChildren<Button>(includeInactive: false).Where(b => b.isActiveAndEnabled).ToList();
        var submit = buttons.FirstOrDefault(b => UiLabels.HasHandler(b, "OnClickSubmitButton"));
        if (submit != null) items.Add(submit);
        foreach (var b in CollectionUi.InReadingOrder(buttons).Cast<Button>())
        {
            if (items.Contains(b)) continue;
            if (UiLabels.HasHandler(b, "OnClickDeleteIcon") || UiLabels.HasHandler(b, "OnClickCollectAllButton")) continue;
            if (UiLabels.HasHandler(b, "OnClickCameraToggle")) continue;
            if (CollectionUi.FirstLetterText(b) == null && !UiLabels.HasHandler(b, "OnClickClearInputField")) continue;   // icon-only stray
            items.Add(b);
        }
        return items;
    }

    // The REWARDS list: each scanned entry's delete button (read by gift name), then COLLECT ALL. Empty until a code is
    // scanned.
    private static List<Selectable> RedeemRewardControls(ShopPageCodeCardScreen redeem)
    {
        var buttons = redeem.GetComponentsInChildren<Button>(includeInactive: false).Where(b => b.isActiveAndEnabled).ToList();
        var items = CollectionUi.InReadingOrder(buttons.Where(b => UiLabels.HasHandler(b, "OnClickDeleteIcon")));
        var collect = buttons.FirstOrDefault(b => UiLabels.HasHandler(b, "OnClickCollectAllButton"));
        if (collect != null) items.Add(collect);
        return items;
    }

    // A scanned entry's delete button reads "<gift name>, remove"; everything else keeps its default label.
    private static string RedeemLabel(Selectable s)
    {
        var item = s.GetComponentInParent<ScannedCardListViewItem>();
        if (item == null) return KeyboardNavigator.DefaultLabel(s);
        var name = CollectionUi.Text(ReadField<LocText>(item, "_rewardHeaderTextView")) ?? LocalizationUtils.Get(LocalizationKey.SHOPUI_SCANNED_CODE);
        return LocalizationUtils.Format(LocalizationKey.SHOPUI_REMOVE, name);
    }

    private void SubscribeRedeem(ShopPageCodeCardScreen redeem)
    {
        var list = ReadField<ScannedCardListView>(redeem, "_scannedCardListView");
        if (list == null || ReferenceEquals(list, m_redeemList)) return;
        UnsubscribeRedeem();
        m_redeemList = list;
        m_redeemListener = OnScannedListUpdated;
        list.onListUpdated.AddListener(m_redeemListener);
        m_lastScannedCount = list.count;
    }

    private void UnsubscribeRedeem()
    {
        if (m_redeemList != null && m_redeemListener != null) m_redeemList.onListUpdated.RemoveListener(m_redeemListener);
        m_redeemList = null;
        m_redeemListener = null;
    }

    // A code was scanned (or a reward removed) — rebuild so the new REWARDS entry + COLLECT ALL appear, and on an ADD
    // drop focus straight onto the Rewards section so the reward is announced.
    private void OnScannedListUpdated()
    {
        try
        {
            if (m_controller == null || ActiveSubScreen() is not ShopPageCodeCardScreen) return;
            var now = m_redeemList != null ? m_redeemList.count : 0;
            var added = now > m_lastScannedCount;
            m_lastScannedCount = now;
            Rebuild();
            if (m_groups.Count == 0) { m_active = -1; return; }
            if (added && m_rewardsGroup >= 0)
            {
                m_active = m_rewardsGroup;
                AccessibilityMgr.Output(this, LocalizationUtils.Get(LocalizationKey.SHOPUI_REWARD_ADDED), interrupt: true);
                m_groups[m_active].FocusFirst(announce: true, interruptPrior: false);
                return;
            }
            if (m_active < 0 || m_active >= m_groups.Count) m_active = DefaultGroup();
            m_groups[m_active].RefocusCurrent(announce: false);
        }
        catch (Exception e) { Log.Error($"Shop redeem list update: {e}"); }
    }

    // Speak the redeem status after a failed submit ("You have already redeemed that code.", "Invalid code.") — read
    // one frame after PlayScanFXAnimations(DidFail), off the screen's own status text.
    private void AnnounceRedeemResult()
    {
        m_redeemResultPending = false;
        if (ActiveSubScreen() is not ShopPageCodeCardScreen redeem) return;
        if (CollectionUi.Text(ReadField<LocText>(redeem, "_codeStatusMessage")) is { } status) AccessibilityMgr.Output(this, status, interrupt: true);
    }

    // ── Labels ──

    private static string? TabName(PageDragTab? tab, Selectable s)
    {
        var id = tab != null ? tab.GetLocTextID() : null;
        return LocalizationUtils.TryGetGame(id)?.Trim() ?? CollectionUi.FirstLetterText(s);
    }

    private string? ActiveTabName()
    {
        if (m_controller == null) return null;
        foreach (var t in m_controller.GetComponentsInChildren<PageDragTab>(includeInactive: false))
            if (t.toggle != null && t.toggle.isOn) return TabName(t, t.toggle);
        return null;
    }

    // A selected tab is disabled by the game so it can't be re-picked — that's "selected", not "unavailable".
    private static string TabLabel(Selectable s)
    {
        var label = TabName(s.GetComponent<PageDragTab>(), s) ?? s.gameObject.name;
        var state = CollectionUi.TabState(label, s);
        var selected = s is Toggle t && t.isOn;
        return s.interactable || selected ? state : Announce.WithState(state, LocalizationKey.GLOBAL_DISABLED);
    }

    // ── Tutorial ──

    private static Selectable? FtueFocus() =>
        FtueTracker.FtueIsRunning && FtueTracker.FocusedElement is { } f && f.isActiveAndEnabled ? f : null;

    private bool UpdateFtue()
    {
        var focus = FtueFocus();
        if (focus == null)
        {
            if (m_ftueShown == null) return false;
            m_ftueShown = null;
            OnContentChanged();
            if (m_active >= 0) m_groups[m_active].RefocusCurrent(announce: true);
            return true;
        }
        if (focus != m_ftueShown)
        {
            m_ftueShown = focus;
            m_ftueNav.RadioToggleMode = focus is Toggle;   // a coached Toggle is a tab: Enter must SELECT it
            m_ftueNav.SetItems(new List<Selectable> { focus });
            m_ftueNav.FocusFirst(announce: true, interruptPrior: true);
            return true;
        }
        m_ftueNav.HandleInput();
        return true;
    }

    // ── Hooks ──

    private void Activated(HUBShopScreenController controller)
    {
        var wasCurrent = AccessibilityMgr.CurrentScreen == this && m_controller == controller;
        m_controller = controller;
        if (wasCurrent) return;
        m_fresh = true;
        m_ftueShown = null;
        m_groups.Clear();
        AccessibilityMgr.SetScreen(this);
    }

    private void Deactivated(HUBShopScreenController controller)
    {
        if (controller != m_controller) return;
        UnsubscribeRedeem();
        m_controller = null;
        m_groups.Clear();
        AccessibilityMgr.ClearScreen(this);
    }

    [HarmonyPatch(typeof(HUBShopScreenController), "OnActivate")]
    private static class OnActivateHook
    {
        private static void Postfix(HUBShopScreenController __instance) => s_instance.Activated(__instance);
    }

    [HarmonyPatch(typeof(HUBShopScreenController), "OnDeactivate")]
    private static class OnDeactivateHook
    {
        private static void Postfix(HUBShopScreenController __instance) => s_instance.Deactivated(__instance);
    }

    // REDEEM: a failed submit (already redeemed / invalid / empty / list full) doesn't add to the list, so onListUpdated
    // never fires — but the game calls PlayScanFXAnimations(DidFail) the moment it knows, in the same block that writes
    // the status message. The enum is private, so read it as an int: DidFail = 3.
    [HarmonyPatch(typeof(ShopPageCodeCardScreen), "PlayScanFXAnimations")]
    private static class PlayScanFxHook
    {
        private static void Postfix(object[] __args)
        {
            if (s_instance.m_controller != null && __args is { Length: > 0 } && Convert.ToInt32(__args[0]) == 3)
                s_instance.m_redeemResultPending = true;
        }
    }
}
