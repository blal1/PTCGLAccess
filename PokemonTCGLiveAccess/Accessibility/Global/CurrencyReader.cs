using System.Collections.Generic;
using System.Linq;
using SharedLogicUtils.source.DataTypes;
using TPCI.Localization;
using UnityEngine;
using static Accessibility.GameReflect;

namespace Accessibility;

// Global "read my balances" key (P). The HUB top bar shows the player's currency totals
// (Crystals / Credits / a token), needed to know what you can afford in the shop — but those are
// info readouts, not focusable controls. P speaks them on demand from the live CurrencyDisplay
// group, ordered left-to-right to match the bar. Gated to screens that actually show the bar (at
// least one mainHub display active + visible), so it stays silent in battle / on the title screen.
// Skipped while a text field is focused so it doesn't hijack a typed "P" (e.g. a redeem code).
internal static class CurrencyReader
{
    private const string GroupName = "mainHub";   // CurrencyDisplay.GROUP_NAME_MAIN_HUB

    [AccessibilityPoller]
    private static void Poll()
    {
        if (!Application.isFocused || !AccessibleKey.READ_CURRENCIES.IsPressed()) return;
        if (AccessibilityMgr.IsTextInputAllowed() || KeyboardNavigator.IsTextInputFocused()) return;

        // No visible currency bar → stay silent: P only acts on screens that show it.
        if (ReadBalances() is { } msg) AccessibilityMgr.OutputNotification(msg, interrupt: true);
    }

    private static string? ReadBalances()
    {
        List<CurrencyDisplay> displays;
        try { displays = CurrencyDisplay.GetCurrencyDisplaysByGroup(GroupName); }
        catch { return null; }
        if (displays == null || displays.Count == 0) return null;

        var parts = new List<(float x, string text)>();
        var seenTypes = new HashSet<CurrencyType>();
        foreach (var d in displays)
        {
            if (d == null || !d.isActiveAndEnabled || !d.IsVisible) continue;
            if (!seenTypes.Add(d.currencyType)) continue;   // one readout per currency type
            var amount = AmountOf(d);
            if (string.IsNullOrWhiteSpace(amount)) continue;
            var word = ShopLabels.CurrencyWord(d.currencyType);
            parts.Add((d.transform.position.x, string.IsNullOrEmpty(word) ? amount! : $"{amount} {word}"));
        }
        if (parts.Count == 0) return null;
        return string.Join(", ", parts.OrderBy(p => p.x).Select(p => p.text));
    }

    private static string? AmountOf(CurrencyDisplay d)
    {
        var s = ReadField<LocText>(d, "currencyText")?.CurrentText;
        return string.IsNullOrWhiteSpace(s) ? null : ShopLabels.StripTags(s!).Trim();
    }
}
