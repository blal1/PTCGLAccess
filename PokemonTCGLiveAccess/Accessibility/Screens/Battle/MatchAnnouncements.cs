using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using MatchLogic;
using TPCI.Avatar;
using TPCI.Rainier.Match.BattleInfoOverlay;
using UnityEngine;
using static Accessibility.GameReflect;

namespace Accessibility;

// What a sighted player reads off the match without pressing anything: message bars, revealed cards,
// rock-paper-scissors, emotes, the two clocks, single-use tokens, and the board-wide effect panels.
// Announce-only: nothing here drives the game. Every hook runs inside a game coroutine, so each is guarded.
internal static class MatchAnnouncements
{
    #region Info keys

    // T, G, H and M, wherever the board can be read (the board itself, a prompt, the board review).
    internal static bool TryHandleInfoKey()
    {
        if (AccessibleKey.BATTLE_STATUS.IsPressed()) { Speak(AccessibleBattleScreen.StatusText()); return true; }
        if (AccessibleKey.BATTLE_MY_EFFECTS.IsPressed()) { Speak(BoardEffects(PlayerID.LOCAL)); return true; }
        if (AccessibleKey.BATTLE_OPP_EFFECTS.IsPressed()) { Speak(BoardEffects(PlayerID.OPPONENT)); return true; }
        if (AccessibleKey.BATTLE_LOG.IsPressed())
        {
            AccessibleBattleLogHistory.Open(AccessibleBattleScreen.CurrentManager?.BattleLogState);
            return true;
        }
        return false;
    }

    private static void Speak(string text) => AccessibilityMgr.OutputNotification(text, interrupt: true);

    // The panel behind the sidebar's status indicator (GlobalStatusEffectsOverlay), with the same filter and
    // the same text generator.
    private static string BoardEffects(PlayerID who)
    {
        var match = NetworkMatchController.currentMatchInfo;
        var info = who == PlayerID.LOCAL ? match?.myPlayerInfo : match?.opPlayerInfo;
        var provider = AccessibleBattleScreen.CurrentManager?.attackOverlay?.DynamicStringProvider;
        var title = LocalizationUtils.Get(who == PlayerID.LOCAL ? LocalizationKey.BATTLE_EFFECTS_MINE : LocalizationKey.BATTLE_EFFECTS_OPP);
        if (info == null || provider == null) return LocalizationUtils.Format(LocalizationKey.BATTLE_EFFECTS_NONE, title);

        static bool Shown(AppliedStatusEffect e) => !e.statusEffect.hideVisualsFromClient && e.statusEffect.statusEffectType != StatusEffectKey.VFXEffect;
        var entries = EffectDescriptionGenerator.GetEntriesForEffects(provider, true, info.posStatusEffects.Where(Shown), info.isPlayer1, isGlobal: true)
            .Concat(EffectDescriptionGenerator.GetEntriesForEffects(provider, false, info.negsStatusEffects.Where(Shown), info.isPlayer1, isGlobal: true))
            .Select(e => LocalizationUtils.Format(LocalizationKey.BATTLE_ATTACHED_CARD_TEXT, CardText.SpeakIcons(e.titleText ?? ""), CardText.SpeakIcons((e.descriptionText ?? "").Replace("\n- ", ". ").TrimStart('-', ' '))))
            .ToList();
        return entries.Count == 0
            ? LocalizationUtils.Format(LocalizationKey.BATTLE_EFFECTS_NONE, title)
            : LocalizationUtils.Format(LocalizationKey.BATTLE_SENTENCES, title, string.Join(". ", entries));
    }

    // Names, turn number, clocks and tokens for the T key.
    internal static IEnumerable<string> StatusLines(MatchManager mgr)
    {
        var opponent = NetworkMatchController.GetPlayerName(PlayerID.OPPONENT);
        if (!string.IsNullOrWhiteSpace(opponent)) yield return LocalizationUtils.Format(LocalizationKey.BATTLE_STATUS_OPPONENT, opponent);

        var turn = (mgr.BattleLogState as IEnumerable<_Rainier.Scripts.BattleLog.IBattleLogData>)?
            .OfType<_Rainier.Scripts.BattleLog.IBattleLogPhaseData>().LastOrDefault()?.DisplayTurnNumber ?? 0;
        if (turn > 0) yield return LocalizationUtils.Format(LocalizationKey.BATTLE_STATUS_TURN, turn);

        if (ActionSecondsLeft() is { } act) yield return LocalizationUtils.Format(LocalizationKey.BATTLE_STATUS_ACTION_TIME, act);
        var mine = mgr.MatchInfoSidebar?.Local;
        var theirs = mgr.MatchInfoSidebar?.Oppo;
        if (mine?.PlayerClock != null) yield return LocalizationUtils.Format(LocalizationKey.BATTLE_STATUS_MY_CLOCK, Clock(mine.PlayerClock));
        if (theirs?.PlayerClock != null) yield return LocalizationUtils.Format(LocalizationKey.BATTLE_STATUS_OPP_CLOCK, Clock(theirs.PlayerClock));

        if (mine?.VStarPowerIndicator != null) yield return TokenLine(PlayerID.LOCAL, SidebarConfigHelper.VStarPowerTokenKey);
        if (mine?.GxIndicator != null) yield return TokenLine(PlayerID.LOCAL, SidebarConfigHelper.GXAttackTokenKey);
        if (theirs?.VStarPowerIndicator != null) yield return TokenLine(PlayerID.OPPONENT, SidebarConfigHelper.VStarPowerTokenKey);
        if (theirs?.GxIndicator != null) yield return TokenLine(PlayerID.OPPONENT, SidebarConfigHelper.GXAttackTokenKey);
    }

    private static string Clock(BIO_PlayerClock clock) => clock.RemainingTime.ToString(@"m\:ss");

    #endregion

    #region Message bar

    // The screen-space bar speaks once per distinct text. A prompt's question already reaches the player
    // through the prompt, and the turn banner through the battle screen's phase line.
    private static string s_lastBarText = "";
    private static float s_lastBarTime = -100f;

    internal static void MarkSpoken(string text)
    {
        s_lastBarText = text;
        s_lastBarTime = Time.unscaledTime;
    }

    internal static bool JustSpoken(string text) => text == s_lastBarText && Time.unscaledTime - s_lastBarTime < 10f;

    // All three Setup overloads funnel into FinishSetup, after they set the text.
    [HarmonyPatch(typeof(CentralMessageBar), "FinishSetup")]
    private static class MessageBarHook
    {
        private static void Postfix(CentralMessageBar __instance)
        {
            try
            {
                if (AccessibleBattleScreen.CurrentManager == null) return;
                var text = __instance.CurrText?.Trim();
                // RpsResultDisplay's bar holds the literal "VS" between the two choices: decoration.
                if (string.IsNullOrEmpty(text) || text!.Length <= 2 || JustSpoken(text)) return;
                if (AccessibleBattlePrompt.IsCurrentQuestion(text)) return;
                if (IsTurnBanner(text)) return;
                MarkSpoken(text);
                AccessibilityMgr.OutputNotification(CardText.SpeakIcons(text));
            }
            catch (Exception ex) { Log.Error($"MatchAnnouncements: message bar threw: {ex.Message}"); }
        }
    }

    private static bool IsTurnBanner(string text) =>
        text == LocalizationUtils.TryGetGame("match_turn_player_turn")
        || text == LocalizationUtils.TryGetGame("match_turn_opponent_turn")
        || text == LocalizationUtils.TryGetGame("match_turn_setup_phase");

    #endregion

    #region Revealed cards

    // Grid reveal: the modal UI owns the popup and its Close button, but the cards are CardUI graphics it
    // deliberately skips, so their names are spoken here. Queued after the modal's own title and button.
    [HarmonyPatch(typeof(CardRevealGridMenu), "Setup")]
    private static class RevealGridHook
    {
        private static void Postfix(RevealCardsModification revealOp)
        {
            try
            {
                if (RevealText(revealOp?.actionModificationData) is { Length: > 0 } text) AccessibilityMgr.OutputNotification(text);
            }
            catch (Exception ex) { Log.Error($"MatchAnnouncements: reveal threw: {ex.Message}"); }
        }
    }

    private static string RevealText(ActionModificationData? data)
    {
        if (data is RevealCardsModification.RevealCardsModData plain)
            return Names(plain.revealedCardsDelta.Select(d => d.cardAddress.entityID));
        if (data is not RevealCardsModification.MatchingRevealCardsModData match) return "";
        return string.Join(". ", match.revealGroups.Select(g =>
        {
            var labelId = NetworkMatchController.GetPlayerIdFromIsPlayer1(g.IsPlayer1) == PlayerID.LOCAL ? g.PlayerLabelLocID : g.OpponentLabelLocID;
            var names = Names(g.RevealedCardEntities);
            return LocalizationUtils.TryGetGame(labelId) is { } label ? LocalizationUtils.Format(LocalizationKey.BATTLE_ATTACHED_CARD_TEXT, label, names) : names;
        }));
    }

    private static string Names(IEnumerable<string> entityIds)
    {
        var names = entityIds.Select(BattleBoardReader.RevealedName).Where(n => n.Length > 0).ToList();
        return names.Count == 0 ? "" : LocalizationUtils.Format(LocalizationKey.BATTLE_REVEALED_CARDS, string.Join(", ", names));
    }

    // A prize flipped face up inside the prize menu (RevealOperation.TryConductingAPrizeRevealMenuFlip). An
    // unresolved name stays silent: the sighted player sees nothing of a card still hidden from them.
    [HarmonyPatch(typeof(PrizeCardSelectionMenu), "IE_AnimateSelection")]
    private static class PrizeFlipHook
    {
        private static void Postfix(IEnumerable<(string, BoardPos, int)> cardLocations)
        {
            try
            {
                if (Names(cardLocations.Select(c => c.Item1)) is { Length: > 0 } text) AccessibilityMgr.OutputNotification(text);
            }
            catch (Exception ex) { Log.Error($"MatchAnnouncements: prize flip threw: {ex.Message}"); }
        }
    }

    #endregion

    #region Rock-paper-scissors

    [HarmonyPatch(typeof(RpsResultDisplay), "SetupWithBothKnownSelections")]
    private static class RpsHook
    {
        private static void Postfix(string localSelection, string opponentSelection, RPSPlayerResult playerResult)
        {
            try
            {
                var outcome = playerResult == RPSPlayerResult.Draw ? RPSResult.Tie
                    : (playerResult == RPSPlayerResult.Player1_Win) == NetworkMatchController.isPlayer1 ? RPSResult.Win : RPSResult.Loss;
                var result = RpsResultDisplay.GetRpsResultString(outcome);
                MarkSpoken(result);   // UpdateResultText puts the same words on the message bar a moment later
                AccessibilityMgr.OutputNotification(LocalizationUtils.Format(LocalizationKey.BATTLE_RPS_RESULT, RpsName(localSelection), RpsName(opponentSelection), result));
            }
            catch (Exception ex) { Log.Error($"MatchAnnouncements: RPS threw: {ex.Message}"); }
        }
    }

    internal static string RpsName(string id) => id switch
    {
        "option_rock" => LocalizationUtils.GetGame(id, LocalizationKey.BATTLE_RPS_ROCK),
        "option_paper" => LocalizationUtils.GetGame(id, LocalizationKey.BATTLE_RPS_PAPER),
        "option_scissors" => LocalizationUtils.GetGame(id, LocalizationKey.BATTLE_RPS_SCISSORS),
        _ => id,
    };

    #endregion

    #region Emotes

    // The one-argument overload is the opponent's: NetworkMatchController.OnReceiveEmote drops it when muted.
    [HarmonyPatch(typeof(AvatarManager), "PlayEmote", new[] { typeof(string) })]
    private static class ReceiveEmoteHook
    {
        private static void Postfix(string emoteName)
        {
            try { AccessibilityMgr.OutputNotification(LocalizationUtils.Format(LocalizationKey.BATTLE_EMOTE_RECEIVED, EmoteName(emoteName))); }
            catch (Exception ex) { Log.Error($"MatchAnnouncements: emote threw: {ex.Message}"); }
        }
    }

    // Emotes are identified by an animation trigger set in the prefab; none of the decompiled code gives
    // them a display name. So try the game's table, then read the trigger itself as words.
    // ponytail: the trigger may read poorly; give each emote a key once in-game logs list the real values.
    internal static string EmoteName(string key)
    {
        Log.Info($"Emote key: '{key}'");
        return LocalizationUtils.TryGetGame(key)
            ?? LocalizationUtils.TryGetGame("emote_" + key.ToLowerInvariant())
            ?? key.Replace('_', ' ').Trim();
    }

    #endregion

    #region Single-use tokens

    // Available unless the game said otherwise this match: MatchManager keeps no state, only the sidebar's
    // animation does, so the last call of UpdateSingleUseActionAvailability is the answer.
    private static readonly Dictionary<(PlayerID, string), bool> s_tokens = new();

    internal static void ResetForNewMatch()
    {
        s_tokens.Clear();
        s_actionEnd = 0f;
        s_actionWarned = 0;
        s_lastClock = double.MaxValue;
        s_nextClockCheck = 0f;
    }

    [HarmonyPatch(typeof(MatchManager), "UpdateSingleUseActionAvailability")]
    private static class TokenHook
    {
        private static void Postfix(PlayerID whichPlayer, string singleUseActionKey, bool isAvailable)
        {
            try
            {
                s_tokens[(whichPlayer, singleUseActionKey)] = isAvailable;
                AccessibilityMgr.OutputNotification(TokenLine(whichPlayer, singleUseActionKey));
            }
            catch (Exception ex) { Log.Error($"MatchAnnouncements: token threw: {ex.Message}"); }
        }
    }

    private static string TokenLine(PlayerID who, string key)
    {
        var name = LocalizationUtils.Get(key == SidebarConfigHelper.GXAttackTokenKey ? LocalizationKey.BATTLE_TOKEN_GX : LocalizationKey.BATTLE_TOKEN_VSTAR);
        bool available = !s_tokens.TryGetValue((who, key), out var state) || state;
        return who == PlayerID.LOCAL
            ? LocalizationUtils.Format(available ? LocalizationKey.BATTLE_TOKEN_MINE_AVAILABLE : LocalizationKey.BATTLE_TOKEN_MINE_USED, name)
            : LocalizationUtils.Format(available ? LocalizationKey.BATTLE_TOKEN_OPP_AVAILABLE : LocalizationKey.BATTLE_TOKEN_OPP_USED, name);
    }

    #endregion

    #region Clocks

    // When the running action timer ends, in unscaled seconds. ActionTimer counts inside a coroutine and
    // exposes only a flag, so the end is worked out from the budget StartTimer reads. On expiry selections
    // resolve at random (TextSelectionOverlay.OnTimeExpired), so the warning matters.
    private static float s_actionEnd;
    private static int s_actionWarned;
    private static double s_lastClock = double.MaxValue;
    private static float s_nextClockCheck;

    [HarmonyPatch(typeof(ActionTimer), "StartTimer")]
    private static class StartTimerHook
    {
        private static void Postfix(ActionTimer __instance)
        {
            if (ReadField<Coroutine>(__instance, "actionTimerCoroutine") == null) return;   // a zero budget starts nothing
            var budget = NetworkMatchController.currentMatchInfo?.myPlayerInfo?.operationRemainingTime ?? 0f;
            s_actionEnd = budget > 0f ? Time.unscaledTime + budget : 0f;
            s_actionWarned = 0;
        }
    }

    [HarmonyPatch(typeof(ActionTimer), "StopTimer")]
    private static class StopTimerHook
    {
        private static void Postfix() => s_actionEnd = 0f;
    }

    private static int? ActionSecondsLeft()
    {
        if (s_actionEnd <= 0f) return null;
        var left = s_actionEnd - Time.unscaledTime;
        return left > 0f ? Mathf.CeilToInt(left) : null;
    }

    [AccessibilityPoller]
    private static void ClockPoller()
    {
        if (s_actionEnd > 0f)
        {
            var left = s_actionEnd - Time.unscaledTime;
            if (left <= 0f) s_actionEnd = 0f;
            // 15 is the threshold at which the game itself shows the timer (ActionTimer.lowTimeThreshold_s).
            else if (left <= 15f && s_actionWarned == 0) { s_actionWarned = 1; WarnAction(15); }
            else if (left <= 5f && s_actionWarned == 1) { s_actionWarned = 2; WarnAction(5); }
        }

        if (Time.unscaledTime < s_nextClockCheck) return;
        s_nextClockCheck = Time.unscaledTime + 1f;
        var clock = AccessibleBattleScreen.CurrentManager?.MatchInfoSidebar?.Local?.PlayerClock;
        if (clock == null) return;
        var seconds = clock.RemainingTime.TotalSeconds;
        // The two warnings BIO_PlayerClock's own music changes mark.
        if (s_lastClock > 120 && seconds <= 120 && seconds > 30) WarnClock("2:00");
        else if (s_lastClock > 30 && seconds <= 30 && seconds > 0) WarnClock("0:30");
        s_lastClock = seconds;
    }

    private static void WarnAction(int seconds) => AccessibilityMgr.OutputNotification(LocalizationUtils.Format(LocalizationKey.BATTLE_TIME_ACTION_WARNING, seconds), interrupt: true);

    private static void WarnClock(string time) => AccessibilityMgr.OutputNotification(LocalizationUtils.Format(LocalizationKey.BATTLE_TIME_CLOCK_WARNING, time));

    #endregion
}
