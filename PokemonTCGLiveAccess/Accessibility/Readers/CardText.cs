using System.Collections.Generic;
using System.Text.RegularExpressions;
using CardDatabase.DataAccess;

namespace Accessibility;

// Formats a CardDataRow — the printed card data, resolved out of battle via
// CardDatabaseManager.TryGetCardFromDatabase(clientId) — into a spoken name + detail lines.
// This is the out-of-battle counterpart to the battle board reader, which reads the SAME data off a
// battle Card3D (c.cardDataRow). Used by the reward / card-reveal screens so a card shown in a
// pack/grant reveal reads exactly like one inspected in a match.
internal static class CardText
{
    // "name, type" — e.g. "Carnivine, Basic Pokémon", "Ultra Ball, Trainer", "Fire Energy, Energy".
    public static string NameWithType(CardDataRow row)
    {
        if (row == null) return LocalizationUtils.Get(LocalizationKey.CARD_CARD);
        var name = row.LocalizedCardName;
        if (string.IsNullOrEmpty(name)) name = LocalizationUtils.Get(LocalizationKey.CARD_CARD);
        var type = TypeLabel(row);
        return type.Length > 0 ? $"{name}, {type}" : name;
    }

    // The card's rarity name (e.g. "Common", "Double Rare", "Reverse Holo Rare") from its loc code.
    // Ownership and deck legality are per-rarity (a card's variants are distinct CardIDs with separate
    // owned counts), so the deck editor speaks this after the type so a player can tell which printing
    // they're on. Null when the card has no rarity / the loc key is missing (we never speak a raw code).
    //
    // Rarity names live under the same "tcg_" loc family the Card Dex uses for set/series names
    // (CardDexSeriesSelector: SetTextByID("tcg_" + seriesId)). The key is "tcg_rarity_" + the card's
    // Loc Rarity Code — the bare code ("rr", "r_ph", "2r-c") is NOT a loc key on its own (that was the
    // "version 1, version 2…" bug). Some data may store the full id, so we try it raw as a fallback.
    public static string? RarityName(ICardDataRow? row)
    {
        try
        {
            var code = row?.Rarity?.LocRarityCode;
            if (string.IsNullOrWhiteSpace(code)) return null;
            code = code!.Trim();
            var keys = code.StartsWith("tcg_")
                ? new[] { code }
                : new[] { "tcg_rarity_" + code.ToLowerInvariant(), code };
            foreach (var k in keys)
            {
                var s = LocalizationUtils.TryGetGame(k);
                if (s == null) continue;
                var clean = StripFormatting(s);
                if (clean.Length > 0) return clean;
            }
        }
        catch { }
        return null;
    }

    // Some loc strings are format/decorated, e.g. the rarity name "{0} Common" (the {0} is the game's
    // rarity-symbol sprite slot, filled at render time). Drop format placeholders and any rich-text/
    // sprite tags and collapse whitespace so speech says the words only ("Common", not "{0} Common").
    private static string StripFormatting(string s)
    {
        s = Regex.Replace(s, @"\{\d+\}", " ");   // {0}, {1}, …
        s = Regex.Replace(s, @"<[^>]+>", " ");   // <sprite=…>, <color=…>, etc.
        return Regex.Replace(s, @"\s+", " ").Trim();
    }

    // The card's expansion/set name — what tells apart two printings that share a rarity AND treatment
    // but come from different sets (the same card reprinted). Friendly localized name via the same "tcg_"
    // loc family the Card Dex uses for series/set names ("tcg_" + set code, e.g. tcg_swsh1). Falls back
    // to the raw set code the game itself prints on the card stub (CardStubParts: CardSetID + number),
    // which is still a fine disambiguator. Null when the card has no set id.
    public static string? SetName(ICardDataRow? row)
    {
        try
        {
            var code = row?.CardSetID;
            if (string.IsNullOrWhiteSpace(code)) return null;
            code = code!.Trim();
            var s = LocalizationUtils.TryGetGame("tcg_" + code.ToLowerInvariant());
            if (s != null)
            {
                var clean = StripFormatting(s);
                if (clean.Length > 0) return clean;
            }
            return code;   // raw set code (e.g. "MEG") — what the game shows on the stub
        }
        catch { }
        return null;
    }

    // The card's foil / print TREATMENT — what tells same-rarity printings of a card apart (e.g. two
    // "Common" Bulbasaur = normal vs reverse holo). Read from the variant flags (CardDataRow.CardVariant
    // = the "variant" column). The game has no loc key for these treatments, so they are ours.
    // Null for a standard (non-foil-variant) printing.
    public static string? VariantName(ICardDataRow? row)
    {
        if (row == null) return null;
        try
        {
            var v = row.CardVariant;
            if (v.HasFlag(CardVariant.MasterBallParallelHolo)) return LocalizationUtils.Get(LocalizationKey.CARD_VARIANT_MASTER_BALL);
            if (v.HasFlag(CardVariant.PokeBallParallelHolo)) return LocalizationUtils.Get(LocalizationKey.CARD_VARIANT_POKE_BALL);
            if (v.HasFlag(CardVariant.ParallelHolo)) return LocalizationUtils.Get(LocalizationKey.CARD_VARIANT_REVERSE_HOLO);
            if (v.HasFlag(CardVariant.Alternate)) return LocalizationUtils.Get(LocalizationKey.CARD_VARIANT_ALTERNATE_ART);
        }
        catch { }
        return null;
    }

    // "Common" / "Common, reverse holo" / "Illustration Rare, Poké Ball pattern" — rarity plus the
    // print treatment, so two printings that share a rarity name are still distinguishable. Falls back
    // to the treatment alone when the rarity loc is missing; null when neither resolves.
    public static string? RarityWithVariant(ICardDataRow? row)
    {
        var rarity = RarityName(row);
        var variant = VariantName(row);
        if (string.IsNullOrEmpty(rarity)) return variant;
        return string.IsNullOrEmpty(variant) ? rarity : $"{rarity}, {variant}";
    }

    // "Charizard ex, Stage 2 Pokémon, Double Rare" — name+type plus rarity/variant. Used where a card is
    // revealed (pack openings, reward grants) so the player hears exactly which printing they pulled.
    public static string NameTypeRarity(CardDataRow row)
    {
        var s = NameWithType(row);
        var rv = RarityWithVariant(row);
        return string.IsNullOrEmpty(rv) ? s : $"{s}, {rv}";
    }

    // Lines walked with Down / Up after the name: Pokémon stats first, then each attack / ability
    // (localized name + energy cost + damage + rules text). Mirrors the battle board reader's DetailLines.
    public static List<string> DetailLines(CardDataRow row)
    {
        var lines = new List<string>();
        if (row == null) return lines;

        if (row.IsPokemonCard())
        {
            var none = LocalizationUtils.Get(LocalizationKey.CARD_NONE);
            lines.Add(LocalizationUtils.Format(LocalizationKey.CARD_HP, row.HP));
            string types = string.IsNullOrEmpty(row.CardTyping) ? none : row.CardTyping;
            string weak = string.IsNullOrEmpty(row.WeaknessType) ? none : LocalizationUtils.Format(LocalizationKey.CARD_WEAKNESS_VALUE, row.WeaknessType, row.WeaknessAmount);
            string resist = string.IsNullOrEmpty(row.ResistanceType) ? none : LocalizationUtils.Format(LocalizationKey.CARD_RESISTANCE_VALUE, row.ResistanceType, row.ResistanceAmount);
            lines.Add(LocalizationUtils.Format(LocalizationKey.CARD_STATS, types, weak, resist, row.RetreatCost));
        }

        lines.AddRange(SpecialRuleLines(row));

        foreach (var a in row.GetActionInfos())
        {
            if (a == null) continue;
            var head = new List<string>();
            if (!string.IsNullOrEmpty(a.LocalizedName)) head.Add(a.LocalizedName);
            var cost = EnergyCostWords(a.Cost);
            if (cost.Length > 0) head.Add(cost);
            if (!string.IsNullOrEmpty(a.Damage)) head.Add(LocalizationUtils.Format(LocalizationKey.CARD_DAMAGE, a.Damage));
            var line = string.Join(", ", head);
            if (!string.IsNullOrEmpty(a.LocalizedDescription))
                line = line.Length > 0 ? $"{line}. {a.LocalizedDescription}" : a.LocalizedDescription;
            if (line.Length > 0) lines.Add(line);
        }
        return lines;
    }

    // The card's special-rule box — the printed rule a sighted player reads off the bottom of the card
    // but which lives only on the card IMAGE, not in any loc table, so we supply it (game-fundamental
    // text, like "Basic Pokémon" / "Retreat cost" already are):
    //   • ACE SPEC — the deckbuilding limit (one per deck across the whole ACE SPEC pool).
    //   • Rule-box Pokémon — the opponent takes extra Prize cards when it's Knocked Out.
    //
    // The KO Prize count is AUTHORITATIVE when the caller passes it (prizeValueOverride): in battle that
    // is CardInfo.cardSource.prizeValue (= CardEntity.GetPrizeValueFromCardSource — the real printed
    // value the engine awards). Out of battle the card DATABASE row carries no prize field, so we infer
    // it from the rule-box format flags. The flag inference used to bucket Mega Evolution ex as 2; it
    // actually gives 3 (like VMAX / Tag Team / V-UNION) — which is exactly why the authoritative value is
    // preferred whenever it's available, and the flag table is corrected for the fallback.
    public static List<string> SpecialRuleLines(ICardDataRow? row, int? prizeValueOverride = null)
    {
        var lines = new List<string>();
        if (row == null) return lines;
        try
        {
            var ff = ManagerSingleton<CardFormatManager>.instance?.FormatFlagsProvider;
            var flags = row.CardFormatFlags;

            if (ff != null && flags.HasFlag(ff.ACESPEC))
                lines.Add(LocalizationUtils.Get(LocalizationKey.CARD_ACE_SPEC_RULE));

            int prizes = prizeValueOverride
                ?? (ff == null ? 1
                    : flags.HasFlag(ff.VMAX) || flags.HasFlag(ff.TagTeam) || flags.HasFlag(ff.VUNION) || flags.HasFlag(ff.MegaEvolutionEx) ? 3
                    : flags.HasFlag(ff.ex) || flags.HasFlag(ff.EX) || flags.HasFlag(ff.V) || flags.HasFlag(ff.VSTAR) || flags.HasFlag(ff.GX) ? 2 : 1);
            if (prizes >= 2)
                lines.Add(LocalizationUtils.Format(LocalizationKey.CARD_KO_PRIZES_RULE, prizes));
        }
        catch { }
        return lines;
    }

    private static string TypeLabel(CardDataRow row)
    {
        if (row.IsPokemonCard())
        {
            return LocalizationUtils.Get(row.CardStage.ToString() switch
            {
                "Basic" => LocalizationKey.CARD_BASIC_POKEMON,
                "Stage1" => LocalizationKey.CARD_STAGE1_POKEMON,
                "Stage2" => LocalizationKey.CARD_STAGE2_POKEMON,
                _ => LocalizationKey.CARD_POKEMON,
            });
        }
        return row.Category switch
        {
            CardCategory.Trainer => LocalizationUtils.Get(LocalizationKey.CARD_TRAINER),
            CardCategory.Energy => LocalizationUtils.Get(LocalizationKey.CARD_ENERGY),
            _ => "",
        };
    }

    // Translate an energy-cost letter string (e.g. "GGC") into words ("cost 2 Grass, 1 Colorless").
    private static string EnergyCostWords(string? cost)
    {
        if (string.IsNullOrEmpty(cost)) return "";
        const string letters = "RGWLPFDMYNC";
        var counts = new Dictionary<char, int>();
        foreach (var ch in cost!)
            if (letters.IndexOf(ch) >= 0) counts[ch] = counts.TryGetValue(ch, out var n) ? n + 1 : 1;
        if (counts.Count == 0) return "";
        var parts = new List<string>();
        foreach (var kv in counts) parts.Add($"{kv.Value} {EnergyName(kv.Key)}");
        return LocalizationUtils.Format(LocalizationKey.CARD_COST, string.Join(", ", parts));
    }

    private static string EnergyName(char c) => LocalizationUtils.Get(c switch
    {
        'R' => LocalizationKey.ENERGY_FIRE, 'G' => LocalizationKey.ENERGY_GRASS, 'W' => LocalizationKey.ENERGY_WATER,
        'L' => LocalizationKey.ENERGY_LIGHTNING, 'P' => LocalizationKey.ENERGY_PSYCHIC, 'F' => LocalizationKey.ENERGY_FIGHTING,
        'D' => LocalizationKey.ENERGY_DARKNESS, 'M' => LocalizationKey.ENERGY_METAL, 'Y' => LocalizationKey.ENERGY_FAIRY,
        'N' => LocalizationKey.ENERGY_DRAGON, 'C' => LocalizationKey.ENERGY_COLORLESS, _ => LocalizationKey.CARD_ENERGY,
    });

    // Energy and card-property symbols live in localized text as TMP sprite tags (the game's IconReplacer
    // turns "{G}" → <sprite name="grass" tint=1>, "{EX}" → <sprite name="ex" tint=1>, etc.). A plain
    // tag-stripper deletes them, silently dropping what a sighted player sees — e.g. a daily quest's
    // "{G} Energy" shows a Grass symbol that would otherwise vanish. Convert every KNOWN icon to its
    // spoken word — handling BOTH the rendered <sprite name="…"> form that LocText.CurrentText returns
    // AND the raw {G} token (in case some text bypassed IconReplacer) — so callers run this BEFORE their
    // tag stripper. Unknown sprites are left untouched for that stripper to remove.
    // A method, not a static dictionary, so the words follow the current game language.
    private static string? IconWord(string n)
    {
        LocalizationKey? key = n switch
        {
            "colorless" => LocalizationKey.ENERGY_COLORLESS, "fire" => LocalizationKey.ENERGY_FIRE, "grass" => LocalizationKey.ENERGY_GRASS,
            "water" => LocalizationKey.ENERGY_WATER, "fighting" => LocalizationKey.ENERGY_FIGHTING, "fairy" => LocalizationKey.ENERGY_FAIRY,
            "darkness" => LocalizationKey.ENERGY_DARKNESS, "lightning" => LocalizationKey.ENERGY_LIGHTNING, "dragon" => LocalizationKey.ENERGY_DRAGON,
            "metal" => LocalizationKey.ENERGY_METAL, "psychic" => LocalizationKey.ENERGY_PSYCHIC,
            "vunion" => LocalizationKey.CARD_MARKER_V_UNION, "mega" => LocalizationKey.MARKER_MEGA, "prism_star" => LocalizationKey.MARKER_PRISM_STAR,
            _ => null,
        };
        if (key != null) return LocalizationUtils.Get(key);
        // Rule-box marks are printed the same in every language.
        return n switch
        {
            "ex" => "ex", "gx0" or "gx1" or "gx2" or "gx3" => "GX",
            "v" => "V", "vmax" => "VMAX", "vstar" => "VSTAR",
            _ => null,
        };
    }

    private static readonly Regex s_spriteIcon =
        new(@"<sprite[^>]*\bname=""(?<n>[^""]+)""[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex s_energyToken =
        new(@"\{(?<e>[GRWLPFDMYNC])\}", RegexOptions.Compiled);

    public static string SpeakIcons(string? s)
    {
        if (string.IsNullOrEmpty(s)) return s ?? "";
        if (s!.IndexOf('{') >= 0)
            s = s_energyToken.Replace(s, m => $" {EnergyName(m.Groups["e"].Value[0])} ");
        if (s.IndexOf("<sprite", System.StringComparison.OrdinalIgnoreCase) >= 0)
            s = s_spriteIcon.Replace(s, m =>
                IconWord(m.Groups["n"].Value.ToLowerInvariant()) is { } w ? $" {w} " : " ");
        return s;
    }
}
