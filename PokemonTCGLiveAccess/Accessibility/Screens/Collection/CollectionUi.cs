using System.Collections.Generic;
using System.Linq;
using TMPro;
using TPCI.Localization;
using UnityEngine;
using UnityEngine.UI;
using static Accessibility.GameReflect;

namespace Accessibility;

// Small helpers shared by the collection, deck and shop screens (they all sit on the HUB and read the same
// top strip). Return values only; nothing here speaks.
internal static class CollectionUi
{
    // Top-to-bottom, then left-to-right: the order a sighted player reads a panel in.
    public static float ReadingOrder(Transform t)
    {
        var p = t.position;
        return -p.y * 10000f + p.x;
    }

    public static List<Selectable> InReadingOrder(IEnumerable<Selectable> items) =>
        items.Where(s => s != null).OrderBy(s => ReadingOrder(s.transform)).ToList();

    // The HUB top strip (HOME / DECKS / ... / CARD DEX) plus the overflow "⋮", in reading order. Read from
    // HUBTopController_L by identity, the same discovery every HUB screen uses.
    public static List<Selectable> TopStrip()
    {
        var items = new List<Selectable>();
        var top = Object.FindAnyObjectByType<HUBTopController_L>();
        if (top == null) return items;
        var groups = ReadField<List<PersistentNavButtonGroup>>(top, "persistentNavButtons");
        if (groups != null)
        {
            foreach (var g in groups)
            {
                var btn = g != null ? g.navButton : null;
                if (btn != null && btn.isActiveAndEnabled && btn.IsInteractable()) items.Add(btn);
            }
        }
        var overflow = OverflowButton(top);
        if (overflow != null) items.Add(overflow);
        return InReadingOrder(items);
    }

    // Escape on a HUB screen opens the ⋮ overflow menu (those screens have no other "back"): the top bar's own
    // button, so the menu's screen announces itself.
    public static bool OpenOverflowMenu()
    {
        var btn = OverflowButton(Object.FindAnyObjectByType<HUBTopController_L>());
        if (btn == null) return false;
        btn.onClick?.Invoke();
        return true;
    }

    private static Button? OverflowButton(HUBTopController_L? top)
    {
        if (top == null) return null;
        var t = ReadField<Transform>(top, "settingsButton");
        var btn = t != null ? t.GetComponent<Button>() : null;
        return btn != null && btn.isActiveAndEnabled && btn.IsInteractable() ? btn : null;
    }

    // Every active button under a panel, in reading order.
    public static List<Selectable> Buttons(Component root) =>
        InReadingOrder(root.GetComponentsInChildren<Button>(includeInactive: false).Where(b => b.isActiveAndEnabled).Cast<Selectable>());

    public static string? Text(LocText? t)
    {
        var s = t != null ? t.CurrentText : null;
        return string.IsNullOrWhiteSpace(s) ? null : ShopLabels.StripTags(s!).Trim();
    }

    public static string? Text(TMP_Text? t)
    {
        var s = t != null ? t.text : null;
        return string.IsNullOrWhiteSpace(s) ? null : ShopLabels.StripTags(s!).Trim();
    }

    // The first worded text under a control (skips icon glyphs and bare numbers).
    public static string? FirstLetterText(Component c)
    {
        foreach (var t in c.GetComponentsInChildren<TMP_Text>(includeInactive: false))
            if (UiLabels.HasLetter(t.text)) return t.text.Trim();
        return null;
    }

    // "label, selected" / "label, not selected" for a tab-like toggle.
    public static string TabState(string label, Selectable s)
    {
        var selected = s is Toggle t && t.isOn;
        var text = LocalizationUtils.Format(LocalizationKey.COMMON_STATE_FORMAT, label,
            LocalizationUtils.Get(selected ? LocalizationKey.GLOBAL_SELECTED : LocalizationKey.COLLECTION_NOT_SELECTED));
        // The game disables the selected tab so it can't be re-picked: that is "selected", not "unavailable".
        return s.interactable || selected ? text : Announce.WithState(text, LocalizationKey.GLOBAL_DISABLED);
    }

    // "A", "A and B", "A, B and C".
    public static string Conjoin(IReadOnlyList<string> parts)
    {
        if (parts.Count <= 1) return parts.Count == 1 ? parts[0] : "";
        return LocalizationUtils.Format(LocalizationKey.COLLECTION_AND, string.Join(", ", parts.Take(parts.Count - 1)), parts[parts.Count - 1]);
    }

    public static bool AddCopyPressed() =>
        AccessibleKey.ADD_COPY.IsPressed() || AccessibleKey.ADD_COPY_PLUS.IsPressed() || AccessibleKey.ADD_COPY_NUMPAD.IsPressed();

    public static bool RemoveCopyPressed() =>
        AccessibleKey.REMOVE_COPY.IsPressed() || AccessibleKey.REMOVE_COPY_NUMPAD.IsPressed();

    public static bool ConfirmPressed() => AccessibleKey.CONFIRM.IsPressed();

    public static bool BackPressed() =>
        AccessibleKey.ESCAPE.IsPressed() || (AccessibleKey.BACK.IsPressed() && !KeyboardNavigator.IsTextInputFocused());
}
