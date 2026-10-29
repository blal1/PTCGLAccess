using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using RainierClientSDK.source.Commerce;
using SharedLogicUtils.source.DataTypes;
using TPCI.Localization;
using UnityEngine.UI;
using static Accessibility.GameReflect;

namespace Accessibility;

// Spoken labels for shop product tiles and currency amounts. A shop tile (ShopProductController)
// is a composite widget — image + name + price + "AVAILABLE: N" + buy button — so the focused
// control's own text is just a price/icon. We read the OWNING controller and speak
// "name, price, N available", mapping the currency icon (a TMP sprite, not a word) to a spoken
// currency name via the game's CurrencyType.
//
// UiLabels.Describe delegates product labelling here, so both free-browsing nav groups and an
// FTUE-reparented product focus get the same rich label.
internal static class ShopLabels
{
    private static readonly Regex s_tags = new("<[^>]*>", RegexOptions.Compiled);

    // Rich label for a shop product, given any Selectable inside its tile. Null when the
    // Selectable isn't part of a ShopProductController (caller falls back to the default label).
    public static string? ProductLabel(Selectable s)
    {
        if (s == null) return null;
        var product = s.GetComponentInParent<ShopProductController>();
        return product != null ? Product(product) : null;
    }

    public static string Product(ShopProductController product)
    {
        var parts = new List<string>(3);
        var name = ProductName(product);
        if (!string.IsNullOrWhiteSpace(name)) parts.Add(name!);
        var price = ProductPrice(product);
        if (!string.IsNullOrWhiteSpace(price)) parts.Add(price!);
        var avail = ProductAvailability(product);
        if (!string.IsNullOrWhiteSpace(avail)) parts.Add(avail);
        return parts.Count > 0 ? string.Join(", ", parts) : LocalizationUtils.Get(LocalizationKey.SHOP_PRODUCT);
    }

    // Localized name for a shop expansion tile (e.g. "Mega Evolution Chaos Rising"). The on-screen
    // name is only a logo asset, but the game locs every set/series name under the key "tcg_<setCode>"
    // (the Card Dex resolves series headers the same way: SetTextByID("tcg_" + seriesId)). So we loc
    // the expansionId under that key — no hardcoded map. Returns null if it doesn't resolve (caller
    // then speaks the collection count alone).
    public static string? ExpansionName(ExpansionSelection expansion)
    {
        string id;
        try { id = expansion.expansionId; } catch { return null; }
        if (string.IsNullOrEmpty(id)) return null;
        var lower = id.ToLowerInvariant();
        foreach (var key in new[] { "tcg_" + lower, "tcg_" + id })
        {
            var loc = GameText(key);
            if (loc != null && UiLabels.HasLetter(loc) && !loc.Equals(id, System.StringComparison.OrdinalIgnoreCase))
                return loc;
        }
        return null;
    }

    // Collection / section header title ("RECOMMENDED", "NEW ARRIVALS"). The collection's
    // private titleText LocText carries the on-screen header; fall back to a generic word.
    public static string CollectionTitle(ShopProductCollection collection)
    {
        var t = Clean(ReadField<LocText>(collection, "titleText"));
        return UiLabels.HasLetter(t) ? t : LocalizationUtils.Get(LocalizationKey.SHOP_PRODUCTS);
    }

    public static string CurrencyWord(CurrencyType type) => type switch
    {
        CurrencyType.HardCurrency => GameText("currency_crystals") ?? LocalizationUtils.Get(LocalizationKey.SHOP_CRYSTALS),
        CurrencyType.TradeCurrency => GameText("currency_credits") ?? LocalizationUtils.Get(LocalizationKey.SHOP_CREDITS),
        CurrencyType.TokenCurrency => GameText("currency_tokens") ?? LocalizationUtils.Get(LocalizationKey.SHOP_TOKENS),
        CurrencyType.Key => GameText("currency_keys") ?? LocalizationUtils.Get(LocalizationKey.SHOP_KEYS),
        CurrencyType.SoftCurrency => GameText("currency_coins") ?? LocalizationUtils.Get(LocalizationKey.SHOP_COINS),
        _ => "",
    };

    // Rich-text tags (<sprite>, <color>…) removed: what the game draws as icons or colours is not words.
    public static string StripTags(string s) => s.IndexOf('<') < 0 ? s : s_tags.Replace(s, "");

    // ---- product fields ----

    private static string? ProductName(ShopProductController product)
    {
        // The tile's own title is usually the localized product name; some templates (amount-
        // showing collections) blank it, so fall back to the ItemSet's name loc / id.
        var t = Clean(product.titleText);
        if (UiLabels.HasLetter(t)) return t;

        var id = SafeItemSetId(product);
        if (string.IsNullOrEmpty(id)) return null;
        try
        {
            var db = ManagerSingleton<ItemSetDatabaseManager>.instance;
            if (db != null && db.ItemSets.TryGetValue(id, out var itemSet))
            {
                var byName = GameText(itemSet.nameLocId);
                if (byName != null) return byName;
            }
        }
        catch { }
        return GameText(id) ?? id;
    }

    private static string? ProductPrice(ShopProductController product)
    {
        var displayed = Clean(product.costTxt);
        if (string.IsNullOrEmpty(displayed)) return null;
        // Non-numeric → real-money IAP ("$4.99") or a status string ("MISSING"/"NO PRICE"): speak as-is.
        if (!LooksLikePlainNumber(displayed)) return displayed;
        // Plain number → a sprite-currency price; the currency itself is an icon, so name it
        // from the cached offering's price entry.
        var word = CurrencyWordForOffering(SafeItemSetId(product));
        return string.IsNullOrEmpty(word) ? displayed : $"{displayed} {word}";
    }

    private static string ProductAvailability(ShopProductController product)
    {
        try
        {
            var isr = ReadField<ItemSetReference>(product, "_itemSetReference");
            if (isr == null || isr.IsUnlimited()) return "";
            int n = isr.GetAvailableCount();
            return n <= 0 ? LocalizationUtils.Get(LocalizationKey.SHOP_SOLD_OUT) : LocalizationUtils.Format(LocalizationKey.SHOP_AVAILABLE_FORMAT, n);
        }
        catch { return ""; }
    }

    private static string CurrencyWordForOffering(string itemSetId)
    {
        if (string.IsNullOrEmpty(itemSetId)) return "";
        try
        {
            if (!Commerce.TryGetCachedShopOffering(itemSetId, out var offering)) return "";
            var price = offering.PriceEntry;
            if (price == null || price.Count == 0) return "";
            var kv = price.First();
            // The game skips a Key price when a real currency price is also present.
            if (kv.Key == CurrencyType.Key && price.Count > 1) kv = price.ElementAt(1);
            return CurrencyWord(kv.Key);
        }
        catch { return ""; }
    }

    // ---- text helpers ----

    private static string SafeItemSetId(ShopProductController p)
    {
        try { return p.GetItemSetID() ?? ""; } catch { return ""; }
    }

    private static string? GameText(string? id) => LocalizationUtils.TryGetGame(id) is { } s ? StripTags(s).Trim() : null;

    private static string Clean(LocText? t)
    {
        if (t == null) return "";
        var s = t.CurrentText;
        return string.IsNullOrWhiteSpace(s) ? "" : StripTags(s!).Trim();
    }

    // True when the string is only digits and number formatting (comma / dot / spaces) — i.e. a
    // bare currency amount whose icon was stripped, not a real-money price or status word.
    private static bool LooksLikePlainNumber(string s)
    {
        bool digit = false;
        foreach (var c in s)
        {
            if (char.IsDigit(c)) { digit = true; continue; }
            if (c == ',' || c == '.' || c == ' ' || c == ' ' || c == ' ') continue;   // (narrow) no-break space: French thousands separator
            return false;
        }
        return digit;
    }
}
