using System.Collections.Generic;
using System.Linq;
using TMPro;
using TPCI.Localization;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using CardCategory = TPCI.Rainier.CardCategory;
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

    // Every active button under a panel, in reading order. Card stubs are content, not controls.
    public static List<Selectable> Buttons(Component root) =>
        InReadingOrder(root.GetComponentsInChildren<Button>(includeInactive: false).Where(b => b.isActiveAndEnabled && b.GetComponentInParent<CardStubParts>() == null).Cast<Selectable>());

    // A category toggle's only text is its count badge: name it from the filter tab's loc id.
    public static string CategoryLabel(GenericFilterGroup<CardCategory>? selector, Selectable s)
    {
        string? name = null;
        if (selector != null)
            foreach (var tab in selector.AllTabs)
                if (tab?.Toggle != null && tab.Toggle.MyToggle == s) name = LocalizationUtils.TryGetGame(tab.FilterName)?.Trim();
        var count = Text(s.GetComponentInChildren<TMP_Text>(includeInactive: false));
        var label = name ?? count ?? KeyboardNavigator.ResolveBaseLabel(s);
        if (name != null && count != null) label = LocalizationUtils.Format(LocalizationKey.COMMON_STATE_FORMAT, name, LocalizationUtils.Format(LocalizationKey.COLLECTION_CARDS, count));
        return TabState(label, s);
    }

    // The field an ExpandableSearchBar types into.
    public static TMP_InputField? SearchField(ExpandableSearchBar? bar) => ReadField<TMP_InputField>(bar, "searchInput");

    // Moving away from a text field must end its editing, or it keeps eating the keys meant for the next section.
    public static void ReleaseTextField(TMP_InputField? field)
    {
        if (field == null) return;
        field.DeactivateInputField();
        if (EventSystem.current != null && EventSystem.current.currentSelectedGameObject == field.gameObject) EventSystem.current.SetSelectedGameObject(null);
    }

    // The rule rows a validity drawer has spawned, read even while the drawer itself is collapsed.
    public static List<string> ValidityRows(Component? panel)
    {
        var rows = new List<string>();
        var drawer = panel != null ? panel.GetComponentInChildren<DeckValidityDrawer>(includeInactive: true) : null;
        if (drawer == null) return rows;
        foreach (var row in drawer.GetComponentsInChildren<ValidationInfo>(includeInactive: true))
            if (Text(ReadField<LocText>(row, "message")) is { } text) rows.Add(text);
        return rows;
    }

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
