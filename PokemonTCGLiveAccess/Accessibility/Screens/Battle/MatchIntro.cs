using System;
using System.Collections.Generic;
using HarmonyLib;
using MatchLogic;
using TPCI.Localization;
using static Accessibility.GameReflect;

namespace Accessibility;

// The pre-match "VS" intro splash (VersusScreenController): it names both players, shows their ranked
// standing + deck cosmetics, plays each avatar's intro pose, and auto-advances into the match. There
// are no interactive controls (it starts the match itself), so this is a pure announcement — a postfix
// on LoadMatchInfo, which populates every text field synchronously from NetworkMatchController.
// playerDetails before the avatars finish loading (so we read it immediately rather than waiting on
// the visual). This is the richer follow-up to the matchmaking "Opponent found" stinger.
//
// We voice who you're facing + their rank vs yours (the on-screen "226 / 40" — rank points, derived to
// league+tier names since the screen shows only the emblem, not text) + both intro catchphrases (the
// banter a sighted player reads, e.g. "You better keep your 'garde up!"). Rank is Ranked-only — the
// game hides the rank panels in casual/friend matches.
internal static class MatchIntro
{
    [HarmonyPatch(typeof(VersusScreenController), "LoadMatchInfo")]
    private static class LoadMatchInfoHook
    {
        private static void Postfix(VersusScreenController __instance)
        {
            try { Announce(__instance); }
            catch (Exception ex) { Log.Error($"MatchIntro.Announce threw: {ex.Message}"); }
        }
    }

    private static void Announce(VersusScreenController vs)
    {
        var details = NetworkMatchController.playerDetails;
        if (details == null || details.Length < 2) return;

        // Replicate the controller's own local/opponent split (VersusScreenController.LoadMatchInfo).
        bool p1 = NetworkMatchController.isPlayer1;
        var me = p1 ? details[0] : details[1];
        var opp = p1 ? details[1] : details[0];
        bool ranked = NetworkMatchController.gameplayType == GameplayType.Ranked;

        var parts = new List<string>(4);

        var oppName = CleanName(opp?.playerName) ?? LocalizationUtils.Get(LocalizationKey.BATTLE_INTRO_OPPONENT);
        var oppRank = (ranked && opp != null) ? RankReader.StatusFor(opp.playerExp, opp.competitiveElo) : null;
        parts.Add(string.IsNullOrWhiteSpace(oppRank)
            ? LocalizationUtils.Format(LocalizationKey.BATTLE_INTRO_VERSUS, oppName)
            : LocalizationUtils.Format(LocalizationKey.BATTLE_INTRO_VERSUS_RANK, oppName, oppRank));

        var oppPhrase = LocOf(ReadField<LocText>(vs, "opponentCatchphrase"));
        if (oppPhrase != null) parts.Add(oppPhrase);

        var myRank = (ranked && me != null) ? RankReader.StatusFor(me.playerExp, me.competitiveElo) : null;
        if (!string.IsNullOrWhiteSpace(myRank)) parts.Add(LocalizationUtils.Format(LocalizationKey.BATTLE_INTRO_YOU_RANK, myRank));

        var myPhrase = LocOf(ReadField<LocText>(vs, "playerCatchphrase"));
        if (myPhrase != null) parts.Add(myPhrase);

        var text = string.Join(". ", parts);
        // Queue, don't interrupt: this fires ~50ms after the matchmaking "Opponent found, starting
        // match" line, so interrupting would guillotine it. Queuing lets that play, then this follows.
        AccessibilityMgr.OutputNotification(text);
        Log.Info($"Versus screen: {text}");
    }

    // PlayerDetails.playerName carries a "{...}" suffix the game strips before display.
    private static string? CleanName(string? raw)
        => string.IsNullOrWhiteSpace(raw) ? null : raw!.Split('{')[0].Trim();

    private static string? LocOf(LocText? t)
    {
        var s = t != null ? t.CurrentText : null;
        return string.IsNullOrWhiteSpace(s) ? null : s!.Trim();
    }
}
