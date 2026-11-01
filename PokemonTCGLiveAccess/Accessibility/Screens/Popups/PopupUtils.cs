using System;
using TMPro;
using TPCI.Localization;
using UnityEngine;

namespace Accessibility;

// A popup that knows its own spoken title. PTCGL reuses resource ids between a screen header and its popup's
// title ("startup_select_language" heads both the language picker and its confirmation), so a popup opened over
// a titled component skips a title that just repeats the one the player already heard.
internal interface ITitledComponent : AccessibleComponent
{
    string? Title { get; }
}

// Small helpers shared by the startup and popup components (Screens/Popups). They return strings; only the
// components speak.
internal static class PopupUtils
{
    // Top-to-bottom, then left-to-right: the order a sighted player reads a popup in.
    public static float ReadingOrder(Transform t)
    {
        var p = t.position;
        return -p.y * 10000 + p.x;
    }

    // The visible text of a text component (or its first TMP child); null when hidden or blank.
    public static string? Text(Component? c)
    {
        if (c == null || !c.gameObject.activeInHierarchy) return null;
        var t = c.GetComponent<TMP_Text>() ?? c.GetComponentInChildren<TMP_Text>(includeInactive: false);
        return t != null && !string.IsNullOrWhiteSpace(t.text) ? t.text.Trim() : null;
    }

    // LocText.CurrentText is the already formatted display string; null when blank.
    public static string? Loc(LocText? t)
    {
        var s = t != null ? t.CurrentText : null;
        return string.IsNullOrWhiteSpace(s) ? null : s!.Trim();
    }

    // "title. body", without a title that repeats the parent component's (see ITitledComponent).
    public static string? Preamble(string? title, string? body, AccessibleComponent? parent)
    {
        var parentTitle = (parent as ITitledComponent)?.Title;
        var titleRedundant = string.IsNullOrWhiteSpace(title)
            || string.Equals(title!.Trim(), parentTitle?.Trim(), StringComparison.OrdinalIgnoreCase);
        if (titleRedundant) return string.IsNullOrWhiteSpace(body) ? title : body;
        if (string.IsNullOrWhiteSpace(body)) return title;
        return LocalizationUtils.Format(LocalizationKey.POPUP_TITLE_BODY, title, body);
    }

    // Re-announce the focused item (or focus the first one) QUEUED after a title/body the caller just spoke.
    public static void FocusQueued(KeyboardNavigator nav)
    {
        if (nav.Index < 0) nav.FocusFirst(announce: true, interruptPrior: false);
        else nav.RefocusCurrent(announce: true, interrupt: false);
    }

    // The game re-activated a popup that is already up (next page, next battle, new text): ShowUI ignores the
    // focused UI, so re-announce it directly; otherwise bring it to the top.
    public static void ShowOrRefresh(AccessibleUI ui)
    {
        if (AccessibilityMgr.IsFocused(ui)) ui.OnGainedFocus();
        else AccessibilityMgr.ShowUI(ui);
    }

    // The overlay's own navigation is the authority on whether it is open: lobby popups do not go through
    // OverlayManager, and a HUB overlay closed through it only disables its canvas (activeInHierarchy stays true).
    public static bool IsOverlayOpen(OverlayScreenGroup? overlay)
    {
        if (overlay == null) return false;
        try { return overlay.IsOpen(); }
        catch { return overlay.gameObject.activeInHierarchy; }   // no navigation assigned
    }

    // OverlayManager is the authority on which overlay is open: a HUB overlay closed through it only disables
    // its canvas, so activeInHierarchy stays true.
    public static bool IsCurrentOverlay(OverlayScreenGroup? overlay)
    {
        if (overlay == null) return false;
        try { return ManagerSingleton<OverlayManager>.instance?.currentOverlay == overlay; }
        catch { return true; }   // manager unreachable: don't yank focus prematurely
    }
}
