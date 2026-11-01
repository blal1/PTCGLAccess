using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;
using static Accessibility.GameReflect;

namespace Accessibility;

// HUB overflow "⋮" menu — FORUMS / SUPPORT / NEWS / INBOX / SETTINGS + QUIT GAME / TERMS OF USE /
// PRIVACY NOTICE / ABOUT (plus close + logout). Driver: HUBOverflowMenuController (base; _L in
// landscape). Its overlay (MyOverlayScreenGroup) opens OVER the HUB without pausing it, so without a
// component the HUB keeps focus behind it. The controller registers OnOverlayGroupActivate/Deactivate
// as the overlay's activate/deactivate listeners — so those are clean, L/portrait-agnostic hooks.
//
// We present the menu's real buttons for keyboard nav; Escape dismisses (controller.Dismiss). Sub-
// screens it opens (Settings / About / TOS / Privacy) have their own components; the URL buttons
// (Forums/Support/News) and the Quit confirm (a handled modal) work through their own onClick.
internal sealed class AccessibleOverflowMenu : AccessibleUI
{
    private static readonly AccessibleOverflowMenu s_instance = new();

    private readonly KeyboardNavigator m_nav;
    private HUBOverflowMenuController? m_controller;
    private bool m_announcedTitle;

    private AccessibleOverflowMenu()
    {
        m_nav = new KeyboardNavigator(this) { AllowTab = true, OnBack = () => m_controller?.Dismiss() };
    }

    public void HandleInput()
    {
        // Backstop if the deactivate listener was missed (a destroyed controller, CloseAllOverlays).
        if (m_controller == null || !PopupUtils.IsOverlayOpen(m_controller.MyOverlayScreenGroup))
        {
            m_controller = null;
            AccessibilityMgr.HideUI(this);
            return;
        }
        m_nav.HandleInput();
    }

    public string GetHelp() => LocalizationUtils.Get(LocalizationKey.SOCIAL_MENU_HELP);

    public void OnGainedFocus() => Rebuild();

    private void Rebuild()
    {
        var ov = m_controller != null ? m_controller.MyOverlayScreenGroup : null;
        // The persistent top tab strip (HOME / DECKS / PROFILE / SHOP / BATTLE PASS / CARD DEX) and the
        // ⋮ button live in the same canvas hierarchy the overlay sweep reaches, so they'd otherwise show
        // up inside the menu — redundant, since they're always reachable on the top bar itself. Exclude
        // them by identity (robust regardless of how the prefab nests them).
        var topBar = TopBarButtons();
        var items = ov != null
            ? ov.GetComponentsInChildren<Button>(includeInactive: false)
                .Where(b => b.isActiveAndEnabled && !SocialUi.IsCloseChrome(b.gameObject.name) && !topBar.Contains(b))
                .Cast<Selectable>()
                .OrderBy(s => SocialUi.ReadingOrder(s.transform)).ToList()
            : new List<Selectable>();
        m_nav.SetItems(items);
        // The menu is a vertical list — force it (auto-axis detection was reading horizontal because
        // the full-screen tap-outside blocker skewed the bounding box, so Up/Down did nothing).
        m_nav.ForceFlatVertical();

        // The title once per opening; coming back from a sub-panel only re-reads the focused entry.
        var first = !m_announcedTitle;
        if (first)
        {
            m_announcedTitle = true;
            AccessibilityMgr.Output(this, LocalizationUtils.Get(LocalizationKey.UI_MENU), interrupt: true);
        }
        if (items.Count > 0) m_nav.FocusFirst(announce: true, interruptPrior: !first);
    }

    // Open the overflow menu from anywhere (e.g. Escape on the Profile screen) by invoking the top bar's
    // real ⋮ button — the same click a sighted player makes. Gated on the button being active and
    // interactable, so we never open it when the game wouldn't let a mouse user. If the menu is
    // already open, this closes it instead (so the same key toggles). Returns true when it acted.
    // The overlay's own activate/deactivate listeners route through the hooks below exactly like a
    // mouse click, so the component wires up automatically.
    public static bool TryOpen()
    {
        if (s_instance.m_controller != null)
        {
            s_instance.m_controller.Dismiss();
            return true;
        }
        var t = ReadField<Transform>(Object.FindAnyObjectByType<HUBTopController_L>(), "settingsButton");
        var btn = t != null ? t.GetComponent<Button>() : null;
        if (btn == null || !btn.isActiveAndEnabled || !btn.IsInteractable()) return false;
        btn.onClick?.Invoke();
        return true;
    }

    // The persistent top-bar buttons (the HOME/DECKS/… nav strip + the ⋮ overflow button), gathered by
    // identity from HUBTopController_L so they can be excluded from the menu's own list.
    private static HashSet<Selectable> TopBarButtons()
    {
        var set = new HashSet<Selectable>();
        var top = Object.FindAnyObjectByType<HUBTopController_L>();
        if (top == null) return set;
        var navGroups = ReadField<List<PersistentNavButtonGroup>>(top, "persistentNavButtons");
        if (navGroups != null)
            foreach (var pnb in navGroups)
                if (pnb != null && pnb.navButton != null) set.Add(pnb.navButton);
        var settingsT = ReadField<Transform>(top, "settingsButton");
        var overflow = settingsT != null ? settingsT.GetComponent<Button>() : null;
        if (overflow != null) set.Add(overflow);
        return set;
    }

    [HarmonyPatch(typeof(HUBOverflowMenuController), "OnOverlayGroupActivate")]
    private static class ActivateHook
    {
        private static void Postfix(HUBOverflowMenuController __instance)
        {
            if (s_instance.m_controller == null) s_instance.m_announcedTitle = false;
            s_instance.m_controller = __instance;
            if (AccessibilityMgr.IsFocused(s_instance)) s_instance.Rebuild();
            else AccessibilityMgr.ShowUI(s_instance);
        }
    }

    [HarmonyPatch(typeof(HUBOverflowMenuController), "OnOverlayGroupDeactivate")]
    private static class DeactivateHook
    {
        private static void Postfix(HUBOverflowMenuController __instance)
        {
            if (s_instance.m_controller != __instance) return;
            s_instance.m_controller = null;
            AccessibilityMgr.HideUI(s_instance);
        }
    }
}
