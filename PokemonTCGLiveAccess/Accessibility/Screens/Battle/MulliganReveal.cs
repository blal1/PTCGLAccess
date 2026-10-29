using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace Accessibility;

// Start-of-match MULLIGANS reveal popup. When a player's opening hand has no Basic Pokémon they must
// mulligan (reveal the hand, reshuffle, redraw); the resulting reveal — yours or the opponent's — is
// shown to the local player. Driver: MulliganController → MatchManager.mulliganOverlay
// (MulliganDisplayOverlay).Open(playerName, whichPlayer, mulligans, bottomSetup), once per owner.
//
// The popup is built through the generic ModalDrawer/ModalPopup system (a ModalElementContainer), so
// the mod's modal UI ALREADY owns its navigation + the DONE button. The only gap is reading the
// CONTENT: the revealed cards are CardUI graphics inside MulliganDrawGroups — NOT TMP text — so the
// generic modal scan can't speak them, and the player/count context isn't obvious. So this is a small
// ANNOUNCE-ONLY hook (no second component, so nothing to conflict with the modal UI): on Open we read the
// Mulligan data directly and speak a complete summary (who mulliganed, how many times, and the revealed
// card names). The announce runs as Open's postfix, i.e. AFTER the modal UI announced the underlying
// modal, and interrupts it so the user hears one clean, complete readout; DONE stays handled by the modal.
//
// The postfix takes only `playerName` + `mulligans` (both unambiguous) and omits `whichPlayer` — its
// PlayerID type exists in two namespaces (SharedSDKUtils / MatchLogic), and the player's display NAME
// already reads clearly without resolving "you" vs "opponent".
internal static class MulliganReveal
{
    // MulliganDisplayOverlay has TWO methods named "Open": its own Open(string, PlayerID, List, …) and the
    // inherited parameterless BattleMenu.Open(). A plain [HarmonyPatch(type, "Open")] is ambiguous, so pick
    // the multi-parameter one explicitly by arity.
    [HarmonyPatch]
    private static class OpenHook
    {
        private static MethodBase? TargetMethod() =>
            AccessTools.GetDeclaredMethods(typeof(MulliganDisplayOverlay)).FirstOrDefault(m => m.Name == "Open" && m.GetParameters().Length >= 3);

        private static void Postfix(string playerName, List<MulliganController.Mulligan> mulligans)
        {
            if (mulligans == null) return;
            try { Announce(playerName, mulligans); }
            catch (Exception ex) { Log.Error($"MulliganReveal threw: {ex.Message}"); }
        }
    }

    private static void Announce(string playerName, List<MulliganController.Mulligan> mulligans)
    {
        int n = mulligans.Count;
        string who = string.IsNullOrWhiteSpace(playerName) ? LocalizationUtils.Get(LocalizationKey.BATTLE_MULLIGAN_A_PLAYER) : Titleize(playerName);
        // Mirrors the popup's own line, e.g. "Professor had to take 1 mulligan before drawing a Basic Pokémon."
        var parts = new List<string>
        {
            LocalizationUtils.Get(LocalizationKey.BATTLE_MULLIGANS),
            n == 1 ? LocalizationUtils.Format(LocalizationKey.BATTLE_MULLIGAN_ONE, who)
                   : LocalizationUtils.Format(LocalizationKey.BATTLE_MULLIGAN_MANY, who, n),
        };

        for (int i = 0; i < mulligans.Count; i++)
        {
            var names = ResolveNames(mulligans[i]);
            string shown = names.Count > 0 ? string.Join(", ", names) : LocalizationUtils.Get(LocalizationKey.BATTLE_MULLIGAN_HIDDEN);
            parts.Add(n > 1 ? LocalizationUtils.Format(LocalizationKey.BATTLE_MULLIGAN_NTH, i + 1, shown) : shown);
        }

        AccessibilityMgr.OutputNotification(string.Join(". ", parts), interrupt: true);
        Log.Info($"Mulligan reveal: player='{playerName}' count={n}");
    }

    // The revealed cards' localized names — the game's own resolver, since the revealed cards are public.
    // Drops any that don't resolve rather than reading the "card" placeholder.
    private static List<string> ResolveNames(MulliganController.Mulligan m)
    {
        var names = new List<string>();
        if (m?.cardEntityIds == null) return names;
        foreach (var eid in m.cardEntityIds)
        {
            var name = BattleBoardReader.EntityName(eid);
            if (!string.IsNullOrWhiteSpace(name)) names.Add(name);
        }
        return names;
    }

    // The game passes the player's name uppercased ("PROFESSOR"); soften to "Professor" so it doesn't
    // read shouty. Leaves short all-caps tokens (<= 3 chars, likely acronyms) alone.
    private static string Titleize(string s)
    {
        s = s.Trim();
        if (s.Length <= 3) return s;
        if (s.ToUpperInvariant() != s) return s;   // not all-caps — leave as authored
        return char.ToUpperInvariant(s[0]) + s.Substring(1).ToLowerInvariant();
    }
}
