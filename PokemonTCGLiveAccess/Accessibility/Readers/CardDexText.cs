using System.Collections.Generic;
using CardDatabase.DataAccess;

namespace Accessibility;

// Shared text helpers for the CARD DEX screen + its overlays: localized set/series names and the
// one-line label for a card stack (name + type, owned count, new flag, collector number). The card
// label mirrors the deck editor's proven card label — ownership is read per the exact displayed
// CardID, never the archetype-wide total (a card's rarity variants are distinct CardIDs).
internal static class CardDexText
{
    // A game loc id (set / series name), trimmed; null when the game has no text for it.
    public static string? Loc(string? id) => LocalizationUtils.TryGetGame(id)?.Trim();

    // "<name>, <type>, N owned / not owned[, new][, number 001]" for one collection tile.
    public static string CardStackLabel(ArchetypeDBCards stack)
    {
        ICardDataRow? card = null;
        try { card = stack.GetPreferredDisplayCard(); } catch { }

        CardDataRow? row = null;
        try { if (card != null) row = ManagerSingleton<CardDatabaseManager>.instance?.TryGetCardFromDatabase(card.CardID); }
        catch { }

        string name;
        try { name = row != null ? CardText.NameWithType(row) : stack.LocalizedCardName(); }
        catch { name = card?.CardID ?? LocalizationUtils.Get(LocalizationKey.CARD_CARD); }

        var parts = new List<string> { name };

        int owned = 0;
        try { if (card != null) owned = stack.TotalOwnedQuantityForSpecificCard(card.CardID); } catch { }
        parts.Add(owned > 0 ? LocalizationUtils.Format(LocalizationKey.CARD_OWNED, owned) : LocalizationUtils.Get(LocalizationKey.CARD_NOT_OWNED));

        try { if (stack.NewFlagStatus()) parts.Add(LocalizationUtils.Get(LocalizationKey.CARD_NEW)); } catch { }

        try
        {
            var num = card?.DisplayCardNumber;
            if (!string.IsNullOrWhiteSpace(num)) parts.Add(LocalizationUtils.Format(LocalizationKey.CARD_NUMBER, num));
        }
        catch { }

        return string.Join(", ", parts);
    }
}
