using System;
using HarmonyLib;
using TMPro;
using TPCI.Localization;
using TPCI.UI.Screens.ShopScreen;
using static Accessibility.GameReflect;

namespace Accessibility;

// Surfaces that only need a sentence: nothing to press, and they close by themselves.
internal static class OverlayAnnouncers
{
    private static bool s_poorConnection;
    private static string? s_lastMaintenance;

    // LoadingPopupOverlay is an empty class with a spinner: a silent wait otherwise.
    [HarmonyPatch(typeof(OverlayScreenGroup), "OnActivate", new Type[0])]
    private static class LoadingPopupHook
    {
        private static void Postfix(OverlayScreenGroup __instance)
        {
            if (__instance is LoadingPopupOverlay) AccessibilityMgr.OutputNotification(LocalizationUtils.Get(LocalizationKey.GLOBAL_PLEASE_WAIT));
        }
    }

    // "Level x achieved" before the rewards overlay. The prestige path reveals the message a little later on
    // screen; the text is already set here.
    [HarmonyPatch(typeof(LevelUpTransitionOverlay), "OnActivate", new Type[0])]
    private static class LevelUpHook
    {
        private static void Postfix(LevelUpTransitionOverlay __instance)
        {
            var text = SocialUi.TextOf(ReadField<LocText>(__instance, "levelUpMessage"));
            if (!string.IsNullOrWhiteSpace(text)) AccessibilityMgr.OutputNotification(text);
        }
    }

    // Scheduled maintenance banner: a text and an animator trigger. Show runs again from the feature flag coroutine.
    [HarmonyPatch(typeof(MaintenanceNotificationBinding), "Show")]
    private static class MaintenanceHook
    {
        private static void Postfix(MaintenanceNotificationBinding __instance)
        {
            var text = ReadField<TextMeshProUGUI>(__instance, "_maintenanceBodyText")?.text?.Trim();
            if (string.IsNullOrEmpty(text) || text == s_lastMaintenance) return;
            s_lastMaintenance = text;
            AccessibilityMgr.OutputNotification(text!);
        }
    }

    // The victor's catchphrase on the match results. Runs after AccessibleMatchResult's own Show hook, whose outcome
    // line interrupts, so the phrase is queued behind it.
    [HarmonyPatch(typeof(MatchResultsScreenOverlay), "Show")]
    private static class CatchphraseHook
    {
        [HarmonyPriority(Priority.Last)]
        private static void Postfix(MatchResultsScreenOverlay __instance)
        {
            var text = SocialUi.TextOf(__instance.catchphraseTxt);
            if (!string.IsNullOrWhiteSpace(text)) AccessibilityMgr.OutputNotification(text);
        }
    }

    // NetworkStatus.Reconnecting only enables this canvas, from a queued coroutine: state is the simple thing to watch.
    // A failed reconnection ends in DisplayRestartMessage or RainierManager.DisplayMessage, both of which reach a popup
    // component.
    [AccessibilityPoller]
    private static void PollConnection()
    {
        var canvas = ManagerSingleton<RainierManager>.instance?.poorConnection;
        var poor = canvas != null && canvas.enabled;
        if (poor == s_poorConnection) return;
        s_poorConnection = poor;
        AccessibilityMgr.OutputNotification(LocalizationUtils.Get(poor ? LocalizationKey.POPUP_CONNECTION_POOR : LocalizationKey.POPUP_CONNECTION_RESTORED));
    }
}
