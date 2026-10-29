using System.Linq;
using TPCI.Localization;
using UnityEngine;
using UnityEngine.UI;

namespace Accessibility;

// Small layout helpers shared by the profile / social / settings components (each old handler carried its own copy).
internal static class SocialUi
{
    // Top-to-bottom, then left-to-right: the order a sighted player reads the controls.
    public static float ReadingOrder(Transform t)
    {
        var p = t.position;
        return -p.y * 10000f + p.x;
    }

    // A button nested inside another button is a decorative icon child, not a separate control.
    public static bool IsNested(Component b, Transform root)
    {
        for (var t = b.transform.parent; t != null && t != root; t = t.parent)
            if (t.GetComponent<Button>() != null) return true;
        return false;
    }

    // An invisible "tap outside to dismiss" blocker covers most of the screen — never a real control.
    public static bool IsFullScreen(Component b)
    {
        if (b.transform is not RectTransform rt) return false;
        var corners = new Vector3[4];
        rt.GetWorldCorners(corners);
        var canvas = b.GetComponentInParent<Canvas>();
        Camera? cam = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.worldCamera : null;
        var min = RectTransformUtility.WorldToScreenPoint(cam, corners[0]);
        var max = RectTransformUtility.WorldToScreenPoint(cam, corners[2]);
        float w = Mathf.Abs(max.x - min.x), h = Mathf.Abs(max.y - min.y);
        return w * h > 0.6f * Screen.width * Screen.height;
    }

    // Window-chrome close affordances of the overflow panels: "Close" is the X (redundant with Escape) and
    // "CloseTransparent" is the invisible tap-outside blocker. Listing them clutters the list and, being off
    // to the side / full-screen, skews the navigator's axis detection.
    public static bool IsCloseChrome(string name) => name == "Close" || name == "CloseTransparent";

    // The game's text of a LocText, falling back to its resource id's string.
    public static string TextOf(LocText? text)
    {
        if (text == null) return "";
        try
        {
            var current = text.CurrentText;
            if (!string.IsNullOrWhiteSpace(current)) return current;
            return LocalizationUtils.TryGetGame(text.resourceId) ?? "";
        }
        catch { return ""; }
    }

    // "first, second" without empty halves or a repeated word.
    public static string Join(string? first, string? second)
    {
        first = first?.Trim() ?? "";
        second = second?.Trim() ?? "";
        if (first.Length == 0) return second;
        if (second.Length == 0 || string.Equals(first, second, System.StringComparison.OrdinalIgnoreCase)) return first;
        return LocalizationUtils.Format(LocalizationKey.COMMON_STATE_FORMAT, first, second);
    }

    public static bool HasWordedLabel(Component b) =>
        b.GetComponentsInChildren<TMPro.TMP_Text>(includeInactive: false).Any(t => !string.IsNullOrWhiteSpace(t.text) && t.text.Any(char.IsLetter));
}
