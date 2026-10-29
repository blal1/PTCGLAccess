using UnityEngine;

namespace Accessibility;

// A screen or UI where the player's own name is relevant (Profile and the friends flow: friends panel, add
// friend, friend options, send-join-match). Implementing it turns on the N key there. Matched by type because a
// screen's spoken name is translated; ConfirmOverlay is generic / used elsewhere, so it must not implement it.
internal interface ISharesPlayerName : AccessibleComponent { }

// "Read / copy my screen name" key (N), scoped to the components marked ISharesPlayerName. The player's name
// sits on the Profile nameplate, but it's an info readout, not a focusable control — and a blind
// player needs it to give to others (it's exactly what someone types into Add Friend). N speaks it
// AND copies it to the OS clipboard, so it can be pasted straight into Discord/chat. Read from the
// game's own NetworkProfile.GetPlayerName() (the same source the nameplate uses).
// Skipped while a text field is focused so it never hijacks a typed "N" (e.g. a friend's name).
internal static class PlayerNameReader
{
    [AccessibilityPoller]
    private static void Poll()
    {
        if (!Application.isFocused || !AccessibleKey.READ_PLAYER_NAME.IsPressed()) return;
        if (AccessibilityMgr.IsTextInputAllowed() || KeyboardNavigator.IsTextInputFocused()) return;
        if (AccessibilityMgr.Focused is not ISharesPlayerName focused) return;

        string name;
        try { name = NetworkProfile.GetPlayerName(); }
        catch { return; }
        if (string.IsNullOrWhiteSpace(name)) return;   // not logged in yet → stay silent
        name = name.Trim();

        // Copy to the OS clipboard so the user can paste their name to share it.
        bool copied = false;
        try { GUIUtility.systemCopyBuffer = name; copied = true; } catch { }

        AccessibilityMgr.Output(focused, copied ? LocalizationUtils.Format(LocalizationKey.COMMON_NAME_COPIED, name) : name, interrupt: true);
    }
}
