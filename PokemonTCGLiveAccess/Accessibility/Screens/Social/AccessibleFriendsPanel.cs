using System;
using System.Collections.Generic;
using System.Reflection;
using _Rainier.Scripts.Utils.Tweening;
using ClientNetworking.Models.Matchmaking;
using HarmonyLib;
using RainierClientSDK.source.Friend;
using RainierClientSDK.source.Matchmaking;
using SharedLogicUtils.source.Services.Query.Contexts;
using UnityEngine;
using static Accessibility.GameReflect;

namespace Accessibility;

// Friends panel — the slide-out on the Profile screen (driver: FriendsHUBScreenController, owned by
// HUBProfileScreenController.friendsHubScreenController). In landscape it's NOT a navigated screen:
// it's always present and slides in/out via a scene-wired tween toggled by the FRIENDS button, so
// there's no clean C# open/close callback. We detect it by GEOMETRY — the close tween moves the
// panel root's RectTransform off-screen, so "open" = the panel root is on-screen. A poller (cheap: it only
// measures while the Profile screen is up) shows us when it slides in and hides us when it slides out.
//
// The friend list is a virtualized EnhancedScroller, so — like the deck editor / battle-pass track —
// we navigate the DATA (FriendsHUBScreenController.cachedFriends), not the scroller cells, and drive
// the controller's own callbacks (the exact methods each cell button fires): SelectFriend (options),
// AcceptFriendRequestAsync / OnSelectDeclineFriendRequest (incoming requests), OpenSendJoinMatchOverlay
// (accept an incoming challenge), OpenAddFriendOverlay (the ADD FRIEND button). Each opens its own
// overlay, handled by the add-friend / friend-options / send-join-match / confirm components, which stack
// on top of us.
internal sealed class AccessibleFriendsPanel : AccessibleUI, ISharesPlayerName
{
    private enum Kind { AddFriend, Options, AcceptRequest, DeclineRequest, AcceptChallenge, Info }

    private readonly struct Row
    {
        public readonly Kind Kind;
        public readonly string Label;
        public readonly string Id;
        public readonly FriendInfo? Friend;

        public Row(Kind kind, string label, string id = "", FriendInfo? friend = null)
        {
            Kind = kind; Label = label; Id = id; Friend = friend;
        }
    }

    private static readonly AccessibleFriendsPanel s_instance = new();

    private static readonly MethodInfo? s_selectFriend = AccessTools.Method(typeof(FriendsHUBScreenController), "SelectFriend", new[] { typeof(string) });
    private static readonly MethodInfo? s_accept = AccessTools.Method(typeof(FriendsHUBScreenController), "AcceptFriendRequestAsync", new[] { typeof(string) });
    private static readonly MethodInfo? s_decline = AccessTools.Method(typeof(FriendsHUBScreenController), "OnSelectDeclineFriendRequest", new[] { typeof(string) });
    private static readonly MethodInfo? s_openSendJoin = AccessTools.Method(typeof(FriendsHUBScreenController), "OpenSendJoinMatchOverlay");
    private static readonly MethodInfo? s_prestige = AccessTools.Method(typeof(FriendsHUBScreenController), "GetPrestigeForFriend");

    // Set by the matchmaker's invitation callback (not the main thread), spoken by the poller.
    private static volatile string? s_pendingInviteFrom;

    private readonly List<Row> m_rows = new();
    private int m_row = -1;
    private bool m_announcedTitle;

    private HUBProfileScreenController? m_profile;
    private FriendsHUBScreenController? m_hub;
    private RectTransform? m_panelRoot;
    private bool m_shown;
    // Only show on a real closed→open transition. Guards against the panel reading as "open"
    // before we've ever seen it closed (e.g. a default-open scene state), which would otherwise
    // hijack focus the instant Profile opens. The notification auto-open still qualifies — its slide
    // tween starts from the closed position, so the first poll sees it closed.
    private bool m_sawClosed;

    private AccessibleFriendsPanel()
    {
        // A friend's challenge only shows up visually (a badge on their row in this panel), so a blind
        // player on any other screen would never know. Same static hook the game's own controller uses.
        MatchMaker.onDirectMatchInvitation += OnDirectMatchInvitation;
    }

    // Called by AccessibleProfile when the Profile screen activates / goes away.
    internal static void OnProfileActivated(HUBProfileScreenController profile)
    {
        var self = s_instance;
        self.m_profile = profile;
        self.m_hub = ReadField<FriendsHUBScreenController>(profile, "friendsHubScreenController");
        self.m_panelRoot = self.ResolvePanelRoot();
        self.m_sawClosed = false;
    }

    internal static void OnProfileDeactivated(HUBProfileScreenController profile)
    {
        var self = s_instance;
        if (self.m_profile != profile) return;
        self.SetShown(false);
        self.m_profile = null;
        self.m_hub = null;
        self.m_panelRoot = null;
    }

    [AccessibilityPoller]
    private static void Poll()
    {
        var self = s_instance;
        if (s_pendingInviteFrom is { } from)
        {
            s_pendingInviteFrom = null;
            self.AnnounceInvite(from);
        }
        if (self.m_hub == null) return;
        var open = self.PanelOpen();
        if (!open) self.m_sawClosed = true;
        // The panel slid back out (our Escape's close tween, the X, or a flow that closes it) — leave.
        if (self.m_shown && !open) self.SetShown(false);
        else if (!self.m_shown && open && self.m_sawClosed) self.SetShown(true);
    }

    private void SetShown(bool shown)
    {
        if (m_shown == shown) return;
        m_shown = shown;
        if (shown)
        {
            m_announcedTitle = false;
            m_row = -1;
            AccessibilityMgr.ShowUI(this);
        }
        else
        {
            AccessibilityMgr.HideUI(this);
        }
    }

    private static void OnDirectMatchInvitation(DirectMatchInvitation invitation)
    {
        try { s_pendingInviteFrom = invitation?.sourceAccountId ?? ""; }
        catch { /* never let the mod break the game's network callback */ }
    }

    // Queued, not interrupting: it can land mid-sentence anywhere, including in a battle.
    private void AnnounceInvite(string accountId)
    {
        var name = FriendName(accountId);
        var msg = string.IsNullOrWhiteSpace(name)
            ? LocalizationUtils.Get(LocalizationKey.SOCIAL_INVITE)
            : LocalizationUtils.Format(LocalizationKey.SOCIAL_INVITE_FROM_FORMAT, name);
        Log.Info($"FriendsPanel: battle challenge received from {accountId}");
        AccessibilityMgr.OutputNotification(msg, interrupt: false);
        if (m_shown) Rebuild();
    }

    // The friend list lives on the friends controller, which exists (possibly inactive) whenever the HUB
    // is loaded; fall back to a search when the Profile screen hasn't been opened yet this session.
    private string? FriendName(string accountId)
    {
        if (string.IsNullOrEmpty(accountId)) return null;
        var hub = m_hub;
        if (hub == null)
        {
            var all = Resources.FindObjectsOfTypeAll<FriendsHUBScreenController>();
            hub = all.Length > 0 ? all[0] : null;
        }
        var friends = ReadField<List<FriendInfo>>(hub, "cachedFriends");
        if (friends == null) return null;
        foreach (var f in friends)
            if (f.id == accountId && !string.IsNullOrWhiteSpace(f.screenName)) return f.screenName;
        return null;
    }

    public void HandleInput()
    {
        if (AccessibleKey.ESCAPE.IsPressed() || (AccessibleKey.BACK.IsPressed() && !KeyboardNavigator.IsTextInputFocused()))
        {
            ClosePanel();
            return;
        }
        if (m_rows.Count == 0) return;
        if (AccessibleKey.READ_NEXT_MENU_OPTION.IsPressed()) Move(1);
        else if (AccessibleKey.READ_PREV_MENU_OPTION.IsPressed()) Move(-1);
        else if (AccessibleKey.CONFIRM.IsPressed()) Activate();
    }

    public string GetHelp() => LocalizationUtils.Get(LocalizationKey.SOCIAL_FRIENDS_HELP);

    // First focus: title, then the first row. Coming back from an overlay (add friend, options, a confirm):
    // the row only, queued (interrupt:false) so a result the closing overlay just spoke (e.g. Add Friend's
    // "request sent") isn't cut off.
    public void OnGainedFocus()
    {
        Rebuild();
        if (!m_announcedTitle)
        {
            m_announcedTitle = true;
            AccessibilityMgr.Output(this, LocalizationUtils.Get(LocalizationKey.SOCIAL_FRIENDS), interrupt: true);
        }
        if (m_row < 0 || m_row >= m_rows.Count) m_row = m_rows.Count > 0 ? 0 : -1;
        AnnounceRow(interrupt: false);
    }

    private void Move(int dir)
    {
        int next = Mathf.Clamp(m_row + dir, 0, m_rows.Count - 1);
        if (next == m_row) return;
        m_row = next;
        AnnounceRow(interrupt: true);
    }

    private void Activate()
    {
        if (m_row < 0 || m_row >= m_rows.Count || m_hub == null) return;
        var row = m_rows[m_row];
        switch (row.Kind)
        {
            case Kind.AddFriend: m_hub.OpenAddFriendOverlay(); break;
            case Kind.Options: Invoke(s_selectFriend, row.Id); break;
            case Kind.AcceptRequest: Invoke(s_accept, row.Id); break;
            case Kind.DeclineRequest: Invoke(s_decline, row.Id); break;
            case Kind.AcceptChallenge: AcceptChallenge(row); break;
            case Kind.Info: break;   // the no-friends message — nothing to activate
        }
    }

    // Accept an incoming challenge: same call the friend cell makes (onAcceptChallenge =
    // OpenSendJoinMatchOverlay(id, outgoing:true, context)).
    private void AcceptChallenge(Row row)
    {
        if (s_openSendJoin == null || m_hub == null) return;
        FriendDirectMatchContext? ctx = null;
        try { ctx = row.Friend?.GetFriendDirectMatchContext(); } catch { ctx = null; }
        try { s_openSendJoin.Invoke(m_hub, new object?[] { row.Id, true, ctx }); }
        catch (Exception e) { Log.Warn($"FriendsPanel: accept challenge failed: {e.Message}"); }
    }

    private void Invoke(MethodInfo? m, string id)
    {
        if (m == null || m_hub == null) return;
        try { m.Invoke(m_hub, new object[] { id }); }
        catch (Exception e) { Log.Warn($"FriendsPanel: {m.Name} failed: {e.Message}"); }
    }

    // Close the slide-out the way the game does — play the close tween. The poller then sees it off-screen
    // and hides us, back to the Profile screen.
    private void ClosePanel()
    {
        var tween = ReadField<TweenRectTransform>(m_hub, "tweenerClose");
        if (tween != null) tween.Play();
        else Log.Warn("FriendsPanel: no tweenerClose to close the panel");
    }

    private void Rebuild()
    {
        m_rows.Clear();
        if (m_hub == null) return;

        m_rows.Add(new Row(Kind.AddFriend, LocalizationUtils.Get(LocalizationKey.SOCIAL_ADD_FRIEND)));

        var friends = ReadField<List<FriendInfo>>(m_hub, "cachedFriends");
        if (friends == null || friends.Count == 0)
        {
            if (NoFriendsMessage() is { } msg) m_rows.Add(new Row(Kind.Info, msg));
            return;
        }

        foreach (var f in friends)
        {
            if (string.IsNullOrEmpty(f.id)) continue;
            var name = string.IsNullOrWhiteSpace(f.screenName) ? LocalizationUtils.Get(LocalizationKey.SOCIAL_UNKNOWN) : f.screenName;
            if (f.requestType == FriendRequestType.Incoming)
            {
                m_rows.Add(new Row(Kind.AcceptRequest, LocalizationUtils.Format(LocalizationKey.SOCIAL_ACCEPT_REQUEST_FORMAT, name), f.id, f));
                m_rows.Add(new Row(Kind.DeclineRequest, LocalizationUtils.Format(LocalizationKey.SOCIAL_DECLINE_REQUEST_FORMAT, name), f.id, f));
            }
            else if (f.challengeRequest != null)
            {
                m_rows.Add(new Row(Kind.AcceptChallenge, LocalizationUtils.Format(LocalizationKey.SOCIAL_CHALLENGE_FORMAT, name), f.id, f));
            }
            else
            {
                m_rows.Add(new Row(Kind.Options, FriendLabel(f, name), f.id, f));
            }
        }
    }

    private string FriendLabel(FriendInfo f, string name)
    {
        var label = LocalizationUtils.Format(f.online ? LocalizationKey.SOCIAL_ONLINE_FORMAT : LocalizationKey.SOCIAL_OFFLINE_FORMAT, name);
        int rank = Prestige(f);
        return rank > 0 ? LocalizationUtils.Format(LocalizationKey.SOCIAL_RANK_FORMAT, label, rank) : label;
    }

    private int Prestige(FriendInfo f)
    {
        if (s_prestige == null || m_hub == null) return -1;
        try { return s_prestige.Invoke(m_hub, new object[] { f }) is int n ? n : -1; }
        catch { return -1; }
    }

    private void AnnounceRow(bool interrupt)
    {
        if (m_row < 0 || m_row >= m_rows.Count) return;
        var row = m_rows[m_row];
        if (row.Kind is Kind.AddFriend or Kind.Info)
        {
            AccessibilityMgr.Output(this, row.Label, interrupt);
            return;
        }
        // Index/total over the friend rows only (the ADD FRIEND action and the no-friends message
        // aren't list entries).
        int total = 0, index = 0;
        for (int i = 0; i < m_rows.Count; i++)
        {
            if (m_rows[i].Kind is Kind.AddFriend or Kind.Info) continue;
            total++;
            if (i <= m_row) index = total;
        }
        AccessibilityMgr.Output(this, Announce.Item(row.Label, description: null, index: index, total: total), interrupt);
    }

    private string? NoFriendsMessage()
    {
        var go = ReadField<GameObject>(m_hub, "noFriendsMessage");
        if (go == null) return null;
        foreach (var t in go.GetComponentsInChildren<TMPro.TMP_Text>(includeInactive: true))
            if (!string.IsNullOrWhiteSpace(t.text)) return t.text.Trim();
        return null;
    }

    // The sliding panel root = the RectTransform the close tween animates. Fall back to the
    // controller's own transform if the tween/field isn't resolvable.
    private RectTransform? ResolvePanelRoot()
    {
        if (m_hub == null) return null;
        var tween = ReadField<TweenRectTransform>(m_hub, "tweenerClose");
        return ReadField<RectTransform>(tween, "_rectTransform") ?? m_hub.transform as RectTransform;
    }

    // "Open" = the panel root is active and substantially on-screen. The close tween slides it off the
    // left edge, so its center crosses off-screen when closed. A near-zero size means layout hasn't run
    // yet (treat as closed) so we don't false-trigger before the panel is real.
    private bool PanelOpen()
    {
        if (m_hub == null || !m_hub.gameObject.activeInHierarchy) return false;
        var root = m_panelRoot ??= ResolvePanelRoot();
        if (root == null) return true;

        var corners = new Vector3[4];
        root.GetWorldCorners(corners);
        var canvas = root.GetComponentInParent<Canvas>();
        Camera? cam = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.worldCamera : null;
        var c0 = RectTransformUtility.WorldToScreenPoint(cam, corners[0]);
        var c2 = RectTransformUtility.WorldToScreenPoint(cam, corners[2]);
        if (Mathf.Abs(c2.x - c0.x) < 2f || Mathf.Abs(c2.y - c0.y) < 2f) return false;   // not laid out
        var center = (c0 + c2) * 0.5f;
        return center.x >= 0f && center.x <= Screen.width && center.y >= 0f && center.y <= Screen.height;
    }
}
