using System;
using System.Linq;
using HarmonyLib;
using TMPro;
using TPCI.Localization;
using TPCI.UI.Screens.ShopScreen;
using static Accessibility.GameReflect;

namespace Accessibility;

// Hub moments with nothing to do but hear them: short animations and notices that take no input.
//   • LevelUpTransitionOverlay: the reward overlay that follows is read, the level itself wasn't.
//   • ExpansionChallengeAnimationsOverlay: Battle Pass welcome / tier unlocked / pass bought / completed.
//   • CurrentlyOpenNPCPopup: a helper bubble that closes by itself (its opener is prefab-only).
//   • LoadingPopupOverlay: the wait before item grants.
internal static class HubAnnouncements
{
    private static void Say(string? text)
    {
        if (!string.IsNullOrWhiteSpace(text)) AccessibilityMgr.OutputNotification(text!, interrupt: false);
    }

    // The text is set before SetupForNewLevel's closing Activate.
    [HarmonyPatch(typeof(LevelUpTransitionOverlay), "SetupForNewLevel")]
    private static class LevelUpHook
    {
        private static void Postfix(LevelUpTransitionOverlay __instance, int newLevel) =>
            Say(HubUi.Text(ReadField<LocText>(__instance, "levelUpMessage")) ?? LocalizationUtils.Format(LocalizationKey.HUB_LEVEL_FORMAT, newLevel));
    }

    // Iterators: a prefix runs when the game creates the coroutine, and gets the arguments.
    [HarmonyPatch(typeof(ExpansionChallengeAnimationsOverlay), "ActivateUnlockTier")]
    private static class TierHook
    {
        private static void Prefix(ExpansionChallengeAnimationsOverlay __instance, int tierLabel, bool completed)
        {
            Say(TierText(__instance, tierLabel));
            if (completed) Say(LocalizationUtils.Get(LocalizationKey.HUB_BP_COMPLETE));
        }
    }

    private static string TierText(ExpansionChallengeAnimationsOverlay overlay, int tierLabel)
    {
        var format = overlay.tierUnlockedTxt != null ? LocalizationUtils.TryGetGame(overlay.tierUnlockedTxt.startResourceId) : null;
        try { if (format != null) return string.Format(format, tierLabel); }
        catch (FormatException) { }
        return LocalizationUtils.Format(LocalizationKey.HUB_BP_TIER_UNLOCKED_FORMAT, tierLabel);
    }

    [HarmonyPatch(typeof(ExpansionChallengeAnimationsOverlay), "ActivateWelcome")]
    private static class WelcomeHook
    {
        private static void Prefix() => Say(LocalizationUtils.Get(LocalizationKey.HUB_BP_WELCOME));
    }

    [HarmonyPatch(typeof(ExpansionChallengeAnimationsOverlay), "ActivatePurchasePass")]
    private static class PurchaseHook
    {
        private static void Prefix() => Say(LocalizationUtils.Get(LocalizationKey.HUB_BP_PASS_PURCHASED));
    }

    [HarmonyPatch(typeof(CurrentlyOpenNPCPopup), "SetCurrentlyOpen")]
    private static class NpcHook
    {
        private static void Postfix(CurrentlyOpenNPCPopup __instance) =>
            Say(string.Join(". ", __instance.GetComponentsInChildren<TMP_Text>(includeInactive: false).Select(t => HubUi.Text(t)).Where(s => s != null)));
    }

    [HarmonyPatch(typeof(OverlayScreenGroup), "Activate")]
    private static class LoadingHook
    {
        private static void Postfix(OverlayScreenGroup __instance)
        {
            if (__instance is LoadingPopupOverlay) Say(LocalizationUtils.Get(LocalizationKey.HUB_LOADING));
        }
    }
}
