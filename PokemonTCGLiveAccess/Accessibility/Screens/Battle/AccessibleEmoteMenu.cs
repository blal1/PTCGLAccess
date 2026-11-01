using System.Linq;
using TPCI.Rainier.Match.BattleInfoOverlay;
using UnityEngine;
using static Accessibility.GameReflect;

namespace Accessibility;

// Send an emote and mute the opponent's. The landscape wheel (LandscapeEmoteWheel) has icon buttons with no
// text, so we build our own list from its EmoteConfig and press through the same calls a click makes:
// BIO_HUD_AvatarDisplay.EmoteButtonClicked, then LandscapeEmoteWheel.EmoteSelected. The tutorial deactivates
// the avatar display, so the menu is refused there. AvatarManager silently drops an emote sent within 3.5 s
// of the previous one, exactly as it does for the mouse.
internal sealed class AccessibleEmoteMenu : AccessibleUI
{
    private static readonly AccessibleEmoteMenu s_instance = new();

    private readonly AccessibleMenu m_menu;
    private BIO_HUD_AvatarDisplay? m_display;
    private LandscapeEmoteWheel? m_wheel;

    private AccessibleEmoteMenu() => m_menu = new AccessibleMenu(this, LocalizationUtils.Get(LocalizationKey.BATTLE_EMOTE_MENU), Close);

    internal static void TryOpen() => s_instance.Open();

    private void Open()
    {
        m_display = Object.FindObjectsByType<BIO_HUD_AvatarDisplay>(FindObjectsSortMode.None).FirstOrDefault();
        m_wheel = Object.FindObjectsByType<LandscapeEmoteWheel>(FindObjectsInactive.Include, FindObjectsSortMode.None).FirstOrDefault();
        var emotes = ReadField<EmoteConfig>(m_wheel, "emoteData")?.availableEmotes;
        var menu = AccessibleBattleScreen.CurrentManager?.MenuNavigation?.CurrentMenu;
        if (m_display == null || m_wheel == null || emotes == null || (menu != null && !menu.AllowEmotePanelWhileOpen))
        {
            AccessibilityMgr.OutputNotification(LocalizationUtils.Get(LocalizationKey.BATTLE_EMOTE_UNAVAILABLE), interrupt: true);
            return;
        }

        m_menu.Clear();
        foreach (var emote in emotes)
        {
            var key = emote.playbackKey;
            m_menu.AddOption(MatchAnnouncements.EmoteName(key), () => Send(key));
        }
        m_menu.AddOption(MuteLabel, ToggleMute);
        AccessibilityMgr.ShowUI(this);
    }

    private static bool OpponentMuted => NetworkMatchController.currentMatchInfo?.isMyOpponentMuted ?? false;

    private static string MuteLabel() => LocalizationUtils.Get(OpponentMuted ? LocalizationKey.BATTLE_EMOTE_UNMUTE : LocalizationKey.BATTLE_EMOTE_MUTE);

    private void Send(string key)
    {
        m_display!.EmoteButtonClicked();
        m_wheel!.EmoteSelected(key);
        Close();
    }

    private void ToggleMute()
    {
        bool mute = !OpponentMuted;
        m_display!.MuteOpponent(mute);
        AccessibilityMgr.Output(this, LocalizationUtils.Get(mute ? LocalizationKey.BATTLE_EMOTE_MUTED : LocalizationKey.BATTLE_EMOTE_UNMUTED));
    }

    private void Close() => AccessibilityMgr.HideUI(this);

    public string GetHelp() => m_menu.GetHelp();

    public void OnGainedFocus() => m_menu.StartReading();

    public void HandleInput()
    {
        if (AccessibleBattleScreen.CurrentManager == null || m_display == null) { Close(); return; }
        m_menu.HandleInput();
    }
}
