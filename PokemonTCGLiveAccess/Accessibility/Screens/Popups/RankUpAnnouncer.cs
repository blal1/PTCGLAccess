using HarmonyLib;
using SharedLogicUtils.source.DataTypes.RankProgression.DataObjects;
using TPCI.Localization;
using static Accessibility.GameReflect;

namespace Accessibility;

// Post-match ranked progression — the rank-points bar fill + the rank-up / league-up stinger you hear
// returning to the HUB after a ranked match. Driven by RankedAnimController (the play-screen rank hex),
// fired once per match by HUBLeagueTowerController.RankUpSetLeagueProgress() (gated on
// SeasonRank.previousMatchExpDelta > 0, then immediately cleared, so it won't repeat on later visits).
// Sighted players watch the number tick up and, on a threshold cross, see a "New Rank!" /
// "Welcome to <league>!" banner with a music stinger (STG_RankUp / STG_LeagueUp); we voice the same.
// Not a component: these are notifications, heard wherever the player is.
//
//  - AnimateToExp(cfg, startExp, endExp): the bar sweep. Announce the points gained + new total. Ladder
//    points below the top league are gain-only, so a loss simply yields no sweep (nothing to say).
//  - Play(Mode): the milestone banner, fired mid-sweep when a rank/league threshold is crossed.
//    messageTxt already holds the localized headline ("Welcome to <league>!" is pre-formatted); for a
//    new rank the headline alone is just "New Rank!", so we append the new tier's name. Season-end
//    promotions reuse the same controller (SeasonEndRewardsSetter) — also a real league-up worth voicing.
internal static class RankUpAnnouncer
{
    [HarmonyPatch(typeof(RankedAnimController), "AnimateToExp")]
    private static class AnimateToExpHook
    {
        private static void Postfix(uint startExp, uint endExp)
        {
            if (endExp <= startExp) return;
            var gained = endExp - startExp;
            AccessibilityMgr.OutputNotification(gained == 1
                ? LocalizationUtils.Format(LocalizationKey.POPUP_RANK_ONE_POINT_GAINED, endExp)
                : LocalizationUtils.Format(LocalizationKey.POPUP_RANK_POINTS_GAINED, gained, endExp), interrupt: true);
        }
    }

    [HarmonyPatch(typeof(RankedAnimController), "Play")]
    private static class PlayHook
    {
        private static void Postfix(RankedAnimController __instance, RankedAnimController.Mode animMode)
        {
            var headline = PopupUtils.Loc(ReadField<LocText>(__instance, "messageTxt"));
            var text = headline;

            // NEW_LEAGUE's headline is already "Welcome to <league>!"; NEW_RANK's is just "New Rank!",
            // so append the freshly-reached tier name (the screen shows it only on the emblem).
            if (animMode == RankedAnimController.Mode.NEW_RANK
                && LocalizationUtils.TryGetGame(ReadField<RankData>(__instance, "_newRankData")?.rankName) is { } rankName)
                text = headline == null ? rankName.Trim() : LocalizationUtils.Format(LocalizationKey.COMMON_STATE_FORMAT, headline, rankName.Trim());

            // Queued after the points line (Play fires mid-sweep).
            if (text != null) AccessibilityMgr.OutputNotification(text, interrupt: false);
        }
    }
}
