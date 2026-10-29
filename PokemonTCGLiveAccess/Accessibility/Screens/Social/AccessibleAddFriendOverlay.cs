using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using static Accessibility.GameReflect;

namespace Accessibility;

// Add-friend search overlay — driver: AddFriendOverlayScreenGroup. It has one text field
// (_inputField, the friend's screen name), a SEND button (sendRequestButton), a clear (X) and a back
// arrow. Sending fires SendFriendRequestAsync and reports via PlaySuccessSequence / PlayInvalidSequence
// (a loc id), which we speak.
//
// IMPORTANT: unlike the other friend overlays, this one is NOT opened through OverlayManager —
// FriendsHUBScreenController.OpenAddFriendOverlay() just fires the scene-wired public `OnOpenAddFriend`
// UnityEvent, so OverlayScreenGroup.OnActivate is never called. So we open off OpenAddFriendOverlay itself
// (skipping its "friend list full" branch, which opens a confirm instead) and close off the overlay's own
// Close() / OnDeActivate, and when the Profile screen goes away (AccessibleProfile).
//
// Nav model: two groups — the text FIELD on its own (arrows are its caret), and
// the BUTTONS together (clear / send / back, navigated by arrows). TAB switches between the two groups;
// it does NOT step through each control.
internal sealed class AccessibleAddFriendOverlay : AccessibleUI, ISharesPlayerName
{
    private const int MaxFriends = 150;   // FriendsHUBScreenController.MAX_FRIENDS_COUNT

    private static readonly AccessibleAddFriendOverlay s_instance = new();

    private readonly List<KeyboardNavigator> m_groups = new();
    private int m_activeGroup;
    private AddFriendOverlayScreenGroup? m_overlay;

    internal static void Hide() => AccessibilityMgr.HideUI(s_instance);

    public void HandleInput()
    {
        if (m_groups.Count == 0) return;
        // TAB switches GROUPS (field <-> buttons), never steps through individual controls. Arrows are
        // handled inside the active group (the field's caret, or moving among the buttons).
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
        m_groups[m_activeGroup].HandleInput();
    }

    public string GetHelp() => LocalizationUtils.Get(LocalizationKey.SOCIAL_ADD_FRIEND_HELP);

    public void OnGainedFocus() => Rebuild();

    private void Close() => m_overlay?.Close();

    private void Rebuild()
    {
        foreach (var g in m_groups) g.Detach();
        m_groups.Clear();
        m_activeGroup = 0;
        if (m_overlay == null) return;

        // Group 1: the text field on its own (arrows are its caret; Tab leaves to the buttons).
        var field = ReadField<TMP_InputField>(m_overlay, "_inputField");
        if (field != null && field.isActiveAndEnabled) AddGroup(new List<Selectable> { field });

        // Group 2: the real buttons (clear / send / back) navigated by arrows. Drop decorative/junk
        // buttons (an icon child nested in another button, or a label-less "Icn"/"Bg"-style stray that
        // duplicates the close) so the user gets exactly the three controls, not a phantom 4th.
        var send = ReadField<Button>(m_overlay, "sendRequestButton");
        var root = m_overlay.transform;
        var buttons = m_overlay.GetComponentsInChildren<Button>(includeInactive: false)
            .Where(b => b.isActiveAndEnabled && IsRealButton(b, send, root))
            .OrderBy(b => SocialUi.ReadingOrder(b.transform))
            .Cast<Selectable>().ToList();
        if (buttons.Count > 0)
        {
            // Back/Clear sit on the top row with SEND below — a small grid. Force BOTH axes: auto-detect
            // measures the bounding box as wider than tall and picks horizontal-only, which strands SEND
            // in the second row (Down disabled). Both axes => Left/Right across the top, Down to SEND.
            var g = AddGroup(buttons);
            g.AllowVertical = true;
            g.AllowHorizontal = true;
        }

        AccessibilityMgr.Output(this, LocalizationUtils.Get(LocalizationKey.SOCIAL_ADD_FRIEND), interrupt: true);
        if (m_groups.Count > 0) m_groups[0].FocusFirst(announce: true, interruptPrior: false);   // land in the field
    }

    private KeyboardNavigator AddGroup(List<Selectable> items)
    {
        // AllowTab=false: this component owns Tab (group switching). Arrows move within the group.
        var g = new KeyboardNavigator(this) { AllowTab = false, OnBack = Close };
        g.SetItems(items);
        m_groups.Add(g);
        return g;
    }

    // A real, navigable control: the SEND button always; otherwise a button that isn't nested inside
    // another button and isn't a label-less decorative icon. PTCGL prefabs sometimes put a clickable
    // "Icn"/"Image"/"Bg" child alongside the real button — those have no meaningful label and (here)
    // just re-trigger the close, so they're noise.
    private static bool IsRealButton(Button b, Button? send, Transform root)
    {
        if (send != null && b == send) return true;
        if (SocialUi.IsNested(b, root)) return false;
        var n = b.gameObject.name.Trim().ToLowerInvariant();
        var decorative = n is "icn" or "icon" or "image" or "img" or "bg" or "background" or "blocker";
        return !decorative || SocialUi.HasWordedLabel(b);
    }

    // Speak the send-request outcome. The game reports it as a loc id; if that key isn't in the loaded
    // table, fall back to our own message so the user always hears whether it worked. interrupt:true so
    // it's spoken at once; on success the overlay then closes and the panel's re-announce QUEUES after this
    // (AccessibleFriendsPanel uses interrupt:false on regaining focus).
    private void SpeakResult(string locId)
    {
        var fallback = locId switch
        {
            "friend_add_friend_success" => LocalizationKey.SOCIAL_REQUEST_SENT,
            "friend_add_friend_already_friends" => LocalizationKey.SOCIAL_ALREADY_FRIENDS,
            "friend_max_friend_title" => LocalizationKey.SOCIAL_FRIEND_LIST_FULL,
            _ => LocalizationKey.SOCIAL_REQUEST_FAILED,
        };
        Log.Info($"AddFriend result: {locId}");
        AccessibilityMgr.Output(this, LocalizationUtils.GetGame(locId, fallback), interrupt: true);
    }

    [HarmonyPatch(typeof(FriendsHUBScreenController), "OpenAddFriendOverlay")]
    private static class OpenHook
    {
        private static void Postfix(FriendsHUBScreenController __instance)
        {
            // Full list: the game opened its "friend list full" confirm instead of the search.
            if (ReadField<System.Collections.ICollection>(__instance, "cachedFriends") is { Count: >= MaxFriends }) return;
            s_instance.m_overlay = Object.FindAnyObjectByType<AddFriendOverlayScreenGroup>(FindObjectsInactive.Include);
            AccessibilityMgr.ShowUI(s_instance);
        }
    }

    [HarmonyPatch(typeof(AddFriendOverlayScreenGroup), "Close")]
    private static class CloseHook
    {
        private static void Postfix() => Hide();
    }

    [HarmonyPatch(typeof(AddFriendOverlayScreenGroup), "OnDeActivate")]
    private static class OnDeActivateHook
    {
        private static void Postfix() => Hide();
    }

    [HarmonyPatch(typeof(AddFriendOverlayScreenGroup), "PlaySuccessSequence")]
    private static class SuccessHook
    {
        private static void Postfix(string locId) => s_instance.SpeakResult(locId);
    }

    [HarmonyPatch(typeof(AddFriendOverlayScreenGroup), "PlayInvalidSequence")]
    private static class FailHook
    {
        private static void Postfix(string locId) => s_instance.SpeakResult(locId);
    }
}
