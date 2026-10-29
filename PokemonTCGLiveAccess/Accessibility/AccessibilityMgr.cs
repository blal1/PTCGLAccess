using System;
using System.Collections.Generic;
using UnityEngine;

namespace Accessibility;

// The hub of the mod, as in Hearthstone Access: one current screen, a stack of UIs over it, and the rule
// that only the focused one (top UI, else the screen) receives keys and may speak.
//
// Hooks (Harmony patches on the game) are the only callers of SetScreen / ClearScreen / ShowUI / HideUI.
// They run inside the game's own call stack, so every call into a component goes through Safe(): an
// exception here once escaped a modal's Open, killed the coroutine driving an online match and hard-froze
// it. A broken component is logged and skipped; the game always keeps running.
internal static class AccessibilityMgr
{
    private static readonly List<AccessibleUI> s_uis = new();
    private static AccessibleScreen? s_screen;

    private static bool s_enabled = true;
    private static bool s_textInputAllowed;

    // Speech asked for while the window was in the background is dropped (the player is reading another
    // window); if anything was dropped, the focused component re-announces itself when the window comes back.
    private static bool s_wasFocused = true;
    private static bool s_missedSpeech;

    internal static AccessibleComponent? Focused => s_uis.Count > 0 ? s_uis[s_uis.Count - 1] : s_screen;

    internal static AccessibleScreen? CurrentScreen => s_screen;

    // Static methods marked [AccessibilityPoller]: watchers for game surfaces that have no open/close method to
    // hook (detected by state), run every frame whatever has focus. Discovered once, so no feature edits Main.
    private static readonly List<Action> s_pollers = new();

    internal static void Initialize(System.Reflection.Assembly asm)
    {
        ScreenReader.Load();
        foreach (var type in asm.GetTypes())
        {
            foreach (var m in type.GetMethods(System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic))
            {
                if (m.IsDefined(typeof(AccessibilityPollerAttribute), false)) s_pollers.Add((Action)Delegate.CreateDelegate(typeof(Action), m));
            }
        }
        Output(LocalizationUtils.Format(LocalizationKey.GLOBAL_MOD_LOADED, BuildInfo.Name), interrupt: true);
    }

    internal static void Shutdown() => ScreenReader.Unload();

    #region Screens and UIs

    // The game moved to another base screen. If a UI is showing, the screen waits silently underneath it
    // and announces itself when the last UI closes.
    internal static void SetScreen(AccessibleScreen screen)
    {
        if (s_screen == screen) return;
        Log.Info($"Screen -> {screen.GetType().Name}");
        s_screen = screen;
        if (s_uis.Count == 0) Safe(screen, "OnGainedFocus", screen.OnGainedFocus);
    }

    // The game tore this screen down. Ignored if another screen already replaced it.
    internal static void ClearScreen(AccessibleScreen screen)
    {
        if (s_screen != screen) return;
        Log.Info($"Screen <- {screen.GetType().Name}");
        s_screen = null;
    }

    internal static void ShowUI(AccessibleUI ui)
    {
        if (Focused == ui) return;
        // Re-shown while buried under another UI: move it to the top instead of stacking a duplicate.
        s_uis.Remove(ui);
        s_uis.Add(ui);
        Log.Info($"UI -> {ui.GetType().Name}");
        Safe(ui, "OnGainedFocus", ui.OnGainedFocus);
    }

    // Works from anywhere in the stack: the game closes things when it pleases, including under another UI.
    // A dead UI left buried would later surface owning the keyboard with nothing to drive.
    internal static void HideUI(AccessibleUI ui)
    {
        var wasFocused = Focused == ui;
        if (!s_uis.Remove(ui)) return;
        Log.Info($"UI <- {ui.GetType().Name}");
        if (wasFocused && Focused is { } next) Safe(next, "OnGainedFocus", next.OnGainedFocus);
    }

    internal static bool IsFocused(AccessibleComponent component) => Focused == component;

    #endregion

    #region Speech

    // Speech from a component is only heard while it has focus: a paused screen or a buried UI stays quiet.
    internal static void Output(AccessibleComponent speaker, string text, bool interrupt = true)
    {
        if (speaker == Focused) Output(text, interrupt);
    }

    // Game events that matter wherever the player is (opponent's moves, match found, errors).
    internal static void OutputNotification(string text, bool interrupt = false) => Output(text, interrupt);

    private static void Output(string text, bool interrupt)
    {
        if (!s_enabled) return;
        if (!Application.isFocused)
        {
            s_missedSpeech = true;
            return;
        }
        ScreenReader.Output(text, interrupt);
    }

    #endregion

    #region Input

    // Text fields need every key; the component that owns the field turns this on while the player types.
    internal static void AllowTextInput() => s_textInputAllowed = true;

    internal static void DisallowTextInput() => s_textInputAllowed = false;

    internal static bool IsTextInputAllowed() => s_textInputAllowed;

    // Called once per frame by the MelonMod.
    internal static void Update()
    {
        LocalizationUtils.Update();
        HandleWindowFocus();
        foreach (var poll in s_pollers)
        {
            try { poll(); }
            catch (Exception e)
            {
                var where = $"{poll.Method.DeclaringType?.Name}.{poll.Method.Name}";
                if (s_loggedErrors.Add($"{where}|{e.GetType().Name}|{e.Message}")) Log.Error($"{where} threw: {e}");
            }
        }
        if (!Application.isFocused) return;

        if (AccessibleKey.TOGGLE_ACCESSIBILITY.IsPressed())
        {
            ToggleEnabled();
            return;
        }
        if (!s_enabled) return;

        if (AccessibleKey.HELP.IsPressed())
        {
            Output(GetHelp(), interrupt: true);
            return;
        }
        if (s_textInputAllowed) return;

        if (Focused is { } focused) Safe(focused, "HandleInput", focused.HandleInput);
    }

    private static string GetHelp()
    {
        string help;
        if (Focused is { } focused)
        {
            help = "";
            Safe(focused, "GetHelp", () => help = focused.GetHelp());
        }
        else
        {
            help = LocalizationUtils.Get(LocalizationKey.GLOBAL_PLEASE_WAIT);
        }
        var global = LocalizationUtils.Get(LocalizationKey.GLOBAL_HELP);
        return Compat.Summary() is { } compat ? $"{help} {global} {compat}" : $"{help} {global}";
    }

    private static void HandleWindowFocus()
    {
        var focused = Application.isFocused;
        if (focused == s_wasFocused) return;
        s_wasFocused = focused;
        if (!focused)
        {
            ScreenReader.Stop();
            return;
        }
        if (s_missedSpeech && Focused is { } component)
        {
            s_missedSpeech = false;
            Safe(component, "OnGainedFocus", component.OnGainedFocus);
        }
    }

    private static void ToggleEnabled()
    {
        s_enabled = !s_enabled;
        ScreenReader.Output(LocalizationUtils.Get(s_enabled ? LocalizationKey.GLOBAL_ACCESSIBILITY_ON : LocalizationKey.GLOBAL_ACCESSIBILITY_OFF), true);
    }

    #endregion

    // HandleInput runs every frame: a component stuck throwing is logged once per distinct error, not 60 times a second.
    private static readonly HashSet<string> s_loggedErrors = new();

    private static void Safe(AccessibleComponent component, string what, Action call)
    {
        try { call(); }
        catch (Exception e)
        {
            var where = $"{component.GetType().Name}.{what}";
            if (s_loggedErrors.Add($"{where}|{e.GetType().Name}|{e.Message}")) Log.Error($"{where} threw: {e}");
        }
    }
}
