using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using TPCI.Avatar.Customizations;
using UnityEngine;
using UnityEngine.UI;
using static Accessibility.GameReflect;

namespace Accessibility;

// HUB Profile screen — driver: HUBProfileScreenController (TPCI.RainierClient/HUBProfileScreenController.cs).
// Shown on its OnActivate, cleared on its OnDeactivate.
//
// Groups (in reading order):
//   1. HUB top tab strip — HOME / DECKS / etc. on HUBTopController_L.persistentNavButtons.
//      Each button's GameObject is *inactive* during FTUE startup and only becomes active
//      when the game re-enables the strip. So we can't just snapshot once — we rebuild every
//      time the screen regains focus (i.e. whenever a UI on top of it closes).
//   2. FRIENDS button — friendsButton field on the controller.
//   3. Avatar customization grid — AvatarCustomizationGridItem cells (FACE / HAIR / etc.).
//   4. Catch-all for any remaining active Selectables on the controller (CUSTOMIZE / STATS /
//      avatar gender swap / etc.).
//
// Up at a group's top row enters the group above (landing on its bottom row, same column); Down at the
// bottom row enters the group below. Tab / Shift+Tab cycle groups. Escape opens the overflow (⋮) menu.
internal sealed class AccessibleProfile : AccessibleScreen, ISharesPlayerName
{
    private static readonly AccessibleProfile s_instance = new();

    private readonly List<KeyboardNavigator> m_groups = new();
    private int m_activeGroup = -1;
    private int m_avatarGridGroup = -1;
    private HUBProfileScreenController? m_controller;
    private bool m_announced;
    // Exact focus before a UI covered us, so closing the overflow menu / a popup lands where the player was.
    private int m_groupAtPause = -1;
    private int m_indexAtPause = -1;

    public void HandleInput()
    {
        if (m_groups.Count == 0 || m_activeGroup < 0) return;
        m_groupAtPause = m_activeGroup;
        m_indexAtPause = m_groups[m_activeGroup].Index;

        var next = AccessibleKey.NEXT_SECTION.IsPressed();
        if (next || AccessibleKey.PREV_SECTION.IsPressed())
        {
            if (m_groups.Count > 1)
            {
                m_activeGroup = (m_activeGroup + (next ? 1 : -1) + m_groups.Count) % m_groups.Count;
                m_groups[m_activeGroup].FocusFirst(announce: true, interruptPrior: true);
            }
            return;
        }

        // Section escape: groups are built in visual top-to-bottom order. Pressing Up at
        // the top row jumps to the section above and lands on its *bottom* row (closest
        // to where the user came from); Down at bottom row jumps below and lands on the
        // top row of the section below. The current column is carried over where the
        // target row has it — FACE (col 0 of grid) → SHOES (col 0 of grid's last row),
        // and into FRIENDS (single item, col 0 either way) → HOME (col 0 of tab strip).
        var nav = m_groups[m_activeGroup];
        if (AccessibleKey.READ_PREV_MENU_OPTION.IsPressed() && m_activeGroup > 0 && nav.IsAtTopRow)
        {
            var col = nav.CurrentColumn;
            m_activeGroup--;
            m_groups[m_activeGroup].FocusLastRow(preferredColumn: col, announce: true, interruptPrior: true);
            return;
        }
        if (AccessibleKey.READ_NEXT_MENU_OPTION.IsPressed() && m_activeGroup < m_groups.Count - 1 && nav.IsAtBottomRow)
        {
            var col = nav.CurrentColumn;
            m_activeGroup++;
            m_groups[m_activeGroup].FocusFirstRow(preferredColumn: col, announce: true, interruptPrior: true);
            return;
        }
        nav.HandleInput();
        m_indexAtPause = nav.Index;
    }

    public string GetHelp() => LocalizationUtils.Get(LocalizationKey.SOCIAL_PROFILE_HELP);

    // First entry: "Profile", then the default group's first item (the avatar grid, announced with its
    // section name). Back from a UI on top: rebuild (the screen may have grown, e.g. the top strip became
    // navigable) and restore the exact prior focus, queued (interrupt:false) after any result message the
    // closing UI just spoke.
    public void OnGainedFocus()
    {
        RebuildGroups();
        if (m_groups.Count == 0) return;

        if (!m_announced)
        {
            m_announced = true;
            m_activeGroup = PickDefaultGroup();
            AccessibilityMgr.Output(this, LocalizationUtils.Get(LocalizationKey.SOCIAL_PROFILE), interrupt: true);
            m_groups[m_activeGroup].FocusFirst(announce: true, interruptPrior: false);
            return;
        }

        m_activeGroup = m_groupAtPause >= 0 && m_groupAtPause < m_groups.Count ? m_groupAtPause : PickDefaultGroup();
        var g = m_groups[m_activeGroup];
        if (m_indexAtPause >= 0 && m_indexAtPause < g.Count) g.FocusIndex(m_indexAtPause, announce: true, interrupt: false);
        else g.FocusFirst(announce: true, interruptPrior: false);
    }

    private void RebuildGroups()
    {
        foreach (var g in m_groups) g.Detach();
        m_groups.Clear();
        m_avatarGridGroup = -1;
        if (m_controller == null) return;

        // Group 1: HUB top tab strip (persistent across HUB). The buttons live on each
        // PersistentNavButtonGroup's navButton field. We check the *button's* full native
        // click-route chain (isActiveAndEnabled + IsInteractable) rather than the group's hierarchy,
        // because the group's container may be animated to a visually-hidden state while the buttons
        // themselves remain active and clickable (which is what FTUEWaitForScreenLeave relies on —
        // OnlyAllowHome leaves HOME interactable so the user can advance the tutorial by clicking it).
        var topController = Object.FindAnyObjectByType<HUBTopController_L>();
        var navGroups = ReadField<List<PersistentNavButtonGroup>>(topController, "persistentNavButtons");
        if (navGroups != null)
        {
            var tabs = navGroups
                .Select(pnb => pnb != null ? pnb.navButton : null)
                .Where(b => b != null && b.isActiveAndEnabled && b.IsInteractable())
                .OrderBy(b => SocialUi.ReadingOrder(b!.transform))
                .Cast<Selectable>()
                .ToList();
            if (tabs.Count > 0) AddGroup(tabs, UiLabels.TopNavLabel);
        }

        var known = new HashSet<Selectable>();

        // Group 2: FRIENDS button.
        var friendsBtn = ReadField<Button>(m_controller, "friendsButton");
        if (friendsBtn != null && friendsBtn.gameObject.activeInHierarchy)
        {
            known.Add(friendsBtn);
            AddGroup(new List<Selectable> { friendsBtn });
        }

        // Group 3: Avatar customization grid (FACE / HAIR / etc.).
        var grid = new List<Selectable>();
        foreach (var item in m_controller.GetComponentsInChildren<AvatarCustomizationGridItem>(includeInactive: false))
        {
            // Each AvatarCustomizationGridItem may have a Button on the same GameObject and
            // sometimes an additional Selectable (e.g. a Toggle wrapper). Collect ALL of the
            // item's Selectables so we don't have duplicates in the catch-all group.
            foreach (var sel in item.GetComponents<Selectable>())
            {
                if (sel == null || !sel.gameObject.activeInHierarchy) continue;
                known.Add(sel);
                if (sel is Button) grid.Add(sel);
            }
            foreach (var sel in item.GetComponentsInChildren<Selectable>(includeInactive: false)) known.Add(sel);
            // The grid item's interactive Button lives on a child "DynamicButton" GameObject,
            // but the visual wrapper parent (named "AvatarCustomizationCategoryFace" /
            // "...Hair" / etc.) also carries its own Selectable for hover/click effects. Walk
            // up the hierarchy from each grid item and add those parent Selectables to
            // known so the catch-all doesn't double-list every category.
            for (var t = item.transform.parent; t != null && t != m_controller.transform; t = t.parent)
                foreach (var sel in t.GetComponents<Selectable>()) known.Add(sel);
        }
        if (grid.Count > 0)
        {
            var g = AddGroup(grid.OrderBy(s => SocialUi.ReadingOrder(s.transform)).ToList());
            m_avatarGridGroup = m_groups.Count - 1;
            // Use the panel's visible header ("Avatar Customization") as this group's section
            // name, so section-escape into the grid (e.g. Down from FRIENDS) announces it.
            // For groups without a visual header above them (top tab strip, Friends, the
            // bottom Customize/Stats row) we leave GroupName null — the items themselves
            // are the labels.
            g.GroupName = LocalizationUtils.TryGetGame("avatar_customization_title");
        }

        // Group 4: catch-all for remaining active Selectables on the controller (CUSTOMIZE /
        // STATS / SwapAvatarBtn / etc.).
        //
        // Sub-controller exclusions use direct references to the game's own containers
        // (no heuristics, no alpha walking, no name matching):
        //
        //   • HUBProfileController._itemsMenu — the entire avatar customization items menu
        //     animator. Its GameObject parents the FACE/EYES tabs, the EnhancedScroller of
        //     item cells, the skin-tone strip's parent — everything in the inner panel.
        //     Excluding the whole subtree handles both the scroller cells (CustomizationItem
        //     cells that stay activeInHierarchy through the close animation) and the tabs /
        //     zoom icons that sit as siblings of the scroller.
        //   • HUBProfileController._avatarGroupObjectsController — owns the skin-tone /
        //     hair-color group strip that appears when a category opens. Belt-and-suspenders
        //     in case the prefab parents it outside _itemsMenu.
        //   • HUBProfileScreenController.friendsHubScreenController — the friends overlay
        //     contains its own Close / Btn_AddFriend / etc. that only become reachable when
        //     the user opens the friends sub-screen.
        //
        // Type filter (`s is Button || s is Toggle`) — real interactive UI controls. Excludes pure
        // `Selectable` instances used as decorative hover-targets on the nameplate (RankFrame, Rank)
        // without falsely dropping greyed-out buttons. It DOES NOT filter by IsInteractable(): a button
        // with interactable=false (e.g. STATS when the avatar customization view is the active one) is
        // still a real button the player can see; KeyboardNavigator appends "unavailable".
        var hubProfile = ReadField<HUBProfileController>(m_controller, "_hubProfileController");
        var itemsMenu = ReadField<Animator>(hubProfile, "_itemsMenu");
        var groupObjects = ReadField<AvatarGroupObjectsController>(hubProfile, "_avatarGroupObjectsController");
        var friendsHub = ReadField<FriendsHUBScreenController>(m_controller, "friendsHubScreenController");
        var excluded = new[] { itemsMenu?.transform, groupObjects?.transform, friendsHub?.transform }.Where(t => t != null).ToList();

        var others = m_controller.GetComponentsInChildren<Selectable>(includeInactive: false)
            .Where(s => !known.Contains(s) && (s is Button || s is Toggle) && s.isActiveAndEnabled)
            .Where(s => !excluded.Any(t => s.transform.IsChildOf(t!)))
            .OrderBy(s => SocialUi.ReadingOrder(s.transform))
            .ToList();
        if (others.Count > 0) AddGroup(others);
    }

    private KeyboardNavigator AddGroup(List<Selectable> items, KeyboardNavigator.LabelResolver? resolver = null)
    {
        // Escape on any Profile section opens the overflow (⋮) menu — the same affordance a sighted
        // player gets from the top-bar button (the navigator guards Backspace while a field is focused).
        var g = new KeyboardNavigator(this) { AllowTab = false, OnBack = () => AccessibleOverflowMenu.TryOpen() };
        g.SetItems(items, resolver);
        m_groups.Add(g);
        return g;
    }

    // The avatar customization grid is the conceptual "main content" of the Profile screen,
    // so default to focusing it. Falls back to the first group if the grid wasn't built
    // (e.g. Profile entered before AvatarCustomizationGridItem children were spawned).
    private int PickDefaultGroup() => m_avatarGridGroup >= 0 && m_avatarGridGroup < m_groups.Count ? m_avatarGridGroup : 0;

    [HarmonyPatch(typeof(HUBProfileScreenController), "OnActivate", new[] { typeof(HUBGroupController) })]
    private static class OnActivateHook
    {
        private static void Postfix(HUBProfileScreenController __instance)
        {
            AccessibleFriendsPanel.OnProfileActivated(__instance);
            if (s_instance.m_controller == __instance && AccessibilityMgr.CurrentScreen == s_instance) return;
            s_instance.m_controller = __instance;
            s_instance.m_announced = false;
            s_instance.m_groupAtPause = s_instance.m_indexAtPause = -1;
            AccessibilityMgr.SetScreen(s_instance);
        }
    }

    [HarmonyPatch(typeof(HUBProfileScreenController), "OnDeactivate", new[] { typeof(HUBGroupController) })]
    private static class OnDeactivateHook
    {
        private static void Postfix(HUBProfileScreenController __instance)
        {
            AccessibleFriendsPanel.OnProfileDeactivated(__instance);
            if (s_instance.m_controller != __instance) return;
            AccessibleAddFriendOverlay.Hide();
            AccessibleAvatarCategory.Hide();
            s_instance.m_controller = null;
            AccessibilityMgr.ClearScreen(s_instance);
        }
    }
}
