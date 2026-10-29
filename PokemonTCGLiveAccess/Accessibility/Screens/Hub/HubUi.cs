using System.Collections.Generic;
using System.Linq;
using TPCI.Localization;
using UnityEngine;
using UnityEngine.UI;
using static Accessibility.GameReflect;

namespace Accessibility;

// What every HUB screen shares: the persistent top strip (HOME / DECKS / PROFILE / SHOP / BATTLE PASS / CARD DEX)
// plus the overflow "⋮", all on HUBTopController_L, and the reading order sections are sorted in.
//
// Digit keys: the global HubTabShortcuts poller switches tab on 1-9 in the same frame the focused component reads
// its keys. HUB screens therefore never react to digits themselves: they navigate with KeyboardNavigator (which has
// no number jump) and their own data lists, never with AccessibleMenu / AccessibleListOfItems (whose number jump
// would fire together with the tab switch).
internal static class HubUi
{
    // Each PersistentNavButtonGroup.navButton is a public Button; filter on the button's own active/interactable
    // state (not the container's) because FTUE animates the strip's container while leaving HOME clickable.
    public static List<Selectable> TopStrip()
    {
        var items = new List<Selectable>();
        var top = Object.FindAnyObjectByType<HUBTopController_L>();
        if (top == null) return items;
        var navGroups = ReadField<List<PersistentNavButtonGroup>>(top, "persistentNavButtons");
        if (navGroups != null)
        {
            foreach (var pnb in navGroups)
            {
                var btn = pnb != null ? pnb.navButton : null;
                if (btn != null && btn.isActiveAndEnabled && btn.IsInteractable()) items.Add(btn);
            }
        }
        if (OverflowButton() is { } overflow) items.Add(overflow);
        return items.OrderBy(s => ReadingOrder(s.transform)).ToList();
    }

    // Escape on a HUB section opens the overflow (⋮) menu through its real top-bar button: the same affordance a
    // sighted player gets. The menu's own component takes over from there.
    public static void OpenOverflowMenu() => OverflowButton()?.onClick?.Invoke();

    // The overflow "⋮": settingsButton is a Transform that carries the Button.
    private static Button? OverflowButton()
    {
        var top = Object.FindAnyObjectByType<HUBTopController_L>();
        var t = top != null ? ReadField<Transform>(top, "settingsButton") : null;
        var btn = t != null ? t.GetComponent<Button>() : null;
        return btn != null && btn.isActiveAndEnabled && btn.IsInteractable() ? btn : null;
    }

    // Top-to-bottom, then left-to-right.
    public static float ReadingOrder(Transform t)
    {
        var p = t.position;
        return -p.y * 10000f + p.x;
    }

    // A text the game shows, if its object is live and it has something to say.
    public static string? Text(TMPro.TMP_Text? t) =>
        t != null && t.gameObject.activeInHierarchy && !string.IsNullOrWhiteSpace(t.text) ? t.text.Trim() : null;

    public static string? Text(LocText? t)
    {
        var s = t != null ? t.CurrentText : null;
        return string.IsNullOrWhiteSpace(s) ? null : ShopLabels.StripTags(CardText.SpeakIcons(s)).Trim();
    }

    // "3/10" as "3 of 10" in the player's language.
    public static string Fraction(string s)
    {
        var i = s.IndexOf('/');
        return i < 0 ? s : LocalizationUtils.Format(LocalizationKey.HUB_FRACTION_FORMAT, s.Substring(0, i).Trim(), s.Substring(i + 1).Trim());
    }

    public static bool HasLetterText(Selectable s) =>
        s.GetComponentsInChildren<TMPro.TMP_Text>(includeInactive: false).Any(t => UiLabels.HasLetter(t.text));

    // The game's own back / info buttons are icon-only: name them by their prefab, keep worded buttons' text.
    public static string IconButtonLabel(Selectable s, LocalizationKey info)
    {
        if (HasLetterText(s)) return KeyboardNavigator.DefaultLabel(s);
        var n = s.gameObject.name.ToLowerInvariant();
        if (n.Contains("back") || n.Contains("return") || n.Contains("exit")) return LocalizationUtils.Get(LocalizationKey.HUB_BACK);
        if (n.Contains("home")) return LocalizationUtils.Get(LocalizationKey.HUB_HOME);
        if (n.Contains("setting")) return LocalizationUtils.Get(LocalizationKey.UI_MENU);
        if (n.Contains("info")) return LocalizationUtils.Get(info);
        return KeyboardNavigator.DefaultLabel(s);
    }
}
