namespace Accessibility;

// "name[, description], N of M" — the one shape every list item is spoken in, so a KeyboardNavigator item and an
// AccessibleMenu option sound the same. Returns the text; the caller speaks it through its component.
internal static class Announce
{
    public static string Item(string name, string? description, int index, int total)
    {
        var text = string.IsNullOrEmpty(description) ? name : LocalizationUtils.Format(LocalizationKey.COMMON_STATE_FORMAT, name, description);
        return LocalizationUtils.Format(LocalizationKey.MENU_OPTION_FORMAT, text, index, total);
    }

    // A setting: "name[, current value], N of M".
    public static string Option(string name, string? currentValue, int index, int total) => Item(name, currentValue, index, total);

    // "label, state" (checked / unchecked / selected / unavailable).
    public static string WithState(string label, LocalizationKey state) =>
        LocalizationUtils.Format(LocalizationKey.COMMON_STATE_FORMAT, label, LocalizationUtils.Get(state));
}
