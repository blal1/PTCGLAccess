using System.Collections.Generic;
using RainierClientSDK.source.SeasonRank;

namespace Accessibility;

// The player's ranked status for the HOME rank hex — the same data the game shows there: the
// league/tier emblem, the "rank points" count, and the progress bar toward the next rank. Computed
// from the live season config (HUBLeagueTowerController.seasonConfigContent, a static) + SeasonRank
// statics, so it mirrors exactly what the game would display. league/rank names are loc IDs (the game
// sets them via LocText.SetTextByID), so we resolve them. Returns null until the season data has
// loaded (caller then falls back to a plain "Rank" label).
internal static class RankReader
{
    public static string? Status()
    {
        try
        {
            var cfg = HUBLeagueTowerController.seasonConfigContent?.leagueConfigContent;
            if (cfg == null) return null;

            uint exp = SeasonRank.exp;
            var league = cfg.GetLeagueDataByEXP(exp);
            var rank = cfg.GetRankByEXP(exp);

            var parts = Head(LocalizationUtils.TryGetGame(league?.leagueName), LocalizationUtils.TryGetGame(rank?.rankName));
            parts.Add(Points(exp));

            // rank.expLimit is the cumulative exp to complete the current rank → the gap is "points to
            // the next rank" (the progress bar's remaining fill). Omitted at the top rank.
            if (rank != null && rank.expLimit > exp)
            {
                uint toNext = rank.expLimit - exp;
                parts.Add(toNext == 1 ? LocalizationUtils.Get(LocalizationKey.RANK_ONE_POINT_TO_NEXT) : LocalizationUtils.Format(LocalizationKey.RANK_POINTS_TO_NEXT, toNext));
            }
            return string.Join(", ", parts);
        }
        catch { return null; }
    }

    // Same readout for an ARBITRARY player (the VS screen shows both players' standing, not just our
    // own SeasonRank static): pass the exp + Elo snapshot from PlayerDetails. Mirrors what the game
    // renders on SeasonRankDetails — the top "competitive" league shows Elo, every other shows the
    // cumulative rank-points count. Omits the "points to next rank" tail (clutter for a matchup line).
    public static string? StatusFor(uint exp, uint competitiveElo)
    {
        try
        {
            var cfg = HUBLeagueTowerController.seasonConfigContent?.leagueConfigContent;
            if (cfg == null) return null;

            var league = cfg.GetLeagueDataByEXP(exp);
            var rank = cfg.GetRankByEXP(exp);

            // The competitive league is the last one in the config; it displays Elo, not rank points.
            var leagues = cfg.leagueData;
            var top = (leagues != null && leagues.Count > 0) ? leagues[leagues.Count - 1] : null;
            bool isCompetitive = top != null && league != null &&
                (ReferenceEquals(league, top) || league.leagueName == top.leagueName);

            var parts = Head(LocalizationUtils.TryGetGame(league?.leagueName), LocalizationUtils.TryGetGame(rank?.rankName));
            parts.Add(isCompetitive ? LocalizationUtils.Format(LocalizationKey.RANK_ELO, competitiveElo) : Points(exp));
            return string.Join(", ", parts);
        }
        catch { return null; }
    }

    // "Great Ball, Tier 3" — league, then the rank tier within it (skip the tier if it just
    // repeats the league name). "Rank" when neither resolves.
    private static List<string> Head(string? leagueName, string? rankName)
    {
        var head = new List<string>(3);
        if (!string.IsNullOrWhiteSpace(leagueName)) head.Add(leagueName!);
        if (!string.IsNullOrWhiteSpace(rankName) && rankName != leagueName) head.Add(rankName!);
        if (head.Count == 0) head.Add(LocalizationUtils.Get(LocalizationKey.UI_RANK));
        return head;
    }

    private static string Points(uint exp) =>
        exp == 1 ? LocalizationUtils.Get(LocalizationKey.RANK_ONE_POINT) : LocalizationUtils.Format(LocalizationKey.RANK_POINTS, exp);
}
