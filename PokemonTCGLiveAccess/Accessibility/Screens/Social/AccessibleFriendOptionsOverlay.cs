using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using TMPro;
using TPCI.UI.PlaySubGroups;
using UnityEngine.UI;
using static Accessibility.GameReflect;

namespace Accessibility;

// Friend-options popup (landscape) — driver: FriendOptionsOverlayScreenGroup (an OverlayScreenGroup),
// opened when a friend is selected in the panel (FriendsHUBScreenController.SelectFriend, usePopupOptions).
// The two real choices are CHALLENGE (interactable only when the friend is online) and REMOVE — known
// fields challengeOption / removeOption. The prefab also carries several close affordances (a visible X,
// a bottom close, and an invisible tap-outside blocker), so a blind GetComponentsInChildren<Button> sweep
// listed three "Close" entries. We present the two options + exactly ONE close (the others are filtered:
// full-screen blockers, and icon children nested inside another button). Escape also closes (Close()).
//
// Hooks: the overlay's own OnActivate(OverlayManager) / OnDeActivate overrides (declared on this class, so no
// shared-base cast hazard).
internal sealed class AccessibleFriendOptionsOverlay : AccessibleUI, ISharesPlayerName
{
    private static readonly AccessibleFriendOptionsOverlay s_instance = new();

    private readonly KeyboardNavigator m_nav;
    private FriendOptionsOverlayScreenGroup? m_overlay;
    private Selectable? m_closeButton;

    private AccessibleFriendOptionsOverlay()
    {
        m_nav = new KeyboardNavigator(this) { AllowTab = false, OnBack = Close };
    }

    public void HandleInput()
    {
        // Enter on our "Close" item routes through the overlay's real Close() — the same call Escape
        // makes — rather than the button's own onClick, which (on the X we surface) does nothing.
        if (m_closeButton != null && m_nav.Current == m_closeButton && AccessibleKey.CONFIRM.IsPressed())
        {
            Close();
            return;
        }
        m_nav.HandleInput();
    }

    public string GetHelp() => LocalizationUtils.Get(LocalizationKey.SOCIAL_OVERLAY_HELP);

    public void OnGainedFocus() => Rebuild();

    private void Close() => m_overlay?.Close();

    private void Rebuild()
    {
        if (m_overlay == null || !m_overlay.gameObject.activeInHierarchy) return;

        // The two real choices, from the known fields (Challenge greys out when the friend is offline —
        // KeyboardNavigator still lists it and says "unavailable").
        var items = new List<Selectable>();
        var known = new HashSet<Button>();
        foreach (var b in new[] { ReadField<Button>(m_overlay, "challengeOption"), ReadField<Button>(m_overlay, "removeOption") })
        {
            if (b != null && b.isActiveAndEnabled) { items.Add(b); known.Add(b); }
        }

        // Exactly one close. Candidates = real buttons that aren't the options, dropping the invisible
        // full-screen blocker and icon children nested in another button. Prefer the one actually named
        // "close" (the prefab also has a styled red button we don't want); else the topmost.
        var root = m_overlay.transform;
        var candidates = m_overlay.GetComponentsInChildren<Button>(includeInactive: false)
            .Where(b => b.isActiveAndEnabled && !known.Contains(b) && !SocialUi.IsNested(b, root) && !SocialUi.IsFullScreen(b))
            .OrderBy(b => SocialUi.ReadingOrder(b.transform))
            .ToList();
        m_closeButton = candidates.FirstOrDefault(b => b.gameObject.name.ToLowerInvariant().Contains("close")) ?? candidates.FirstOrDefault();
        if (m_closeButton != null) items.Add(m_closeButton);

        m_nav.SetItems(items, OptionLabel);
        m_nav.AllowVertical = true;
        m_nav.AllowHorizontal = true;   // small button set — both axes so nothing is stranded by auto-detect

        AccessibilityMgr.Output(this, Header(), interrupt: true);
        if (items.Count > 0) m_nav.FocusFirst(announce: true, interruptPrior: false);
    }

    // The close item reads "Close" regardless of its underlying GameObject name (could be a bare "Close"
    // or a styled DynamicButton); the option buttons keep their own labels.
    private string OptionLabel(Selectable s) =>
        s == m_closeButton ? LocalizationUtils.GetGame("global_close", LocalizationKey.SOCIAL_CLOSE) : KeyboardNavigator.DefaultLabel(s);

    // The game's header ("Options for <friend>"), else ours.
    private string Header()
    {
        var t = ReadField<TMP_Text>(m_overlay, "headerText");
        var s = t != null ? t.text : null;
        return string.IsNullOrWhiteSpace(s) ? LocalizationUtils.Get(LocalizationKey.SOCIAL_FRIEND_OPTIONS) : s!.Trim();
    }

    [HarmonyPatch(typeof(FriendOptionsOverlayScreenGroup), "OnActivate", new[] { typeof(OverlayManager) })]
    private static class OnActivateHook
    {
        private static void Postfix(FriendOptionsOverlayScreenGroup __instance)
        {
            s_instance.m_overlay = __instance;
            if (AccessibilityMgr.IsFocused(s_instance)) s_instance.Rebuild();
            else AccessibilityMgr.ShowUI(s_instance);
        }
    }

    [HarmonyPatch(typeof(FriendOptionsOverlayScreenGroup), "OnDeActivate")]
    private static class OnDeActivateHook
    {
        private static void Postfix(FriendOptionsOverlayScreenGroup __instance)
        {
            if (s_instance.m_overlay != __instance) return;
            AccessibilityMgr.HideUI(s_instance);
        }
    }
}
