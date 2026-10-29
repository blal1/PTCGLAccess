namespace Accessibility;

// Something that can own the keyboard and the voice. AccessibilityMgr gives both to exactly one component
// at a time — the top UI if any, else the current screen — and silences every other component's speech.
// All three calls run inside the manager's exception guard, so a bug here never unwinds into the game.
public interface AccessibleComponent
{
    // Called every frame while this component has focus: read keys with AccessibleKey.X.IsPressed().
    void HandleInput();

    // F1. The keys that work right now, in the player's language.
    string GetHelp();

    // This component just got focus (shown, a UI above it closed, the window came back): say where the player is.
    void OnGainedFocus();
}

// A base screen of the game (loading, hub, battle). Only one at a time: AccessibilityMgr.SetScreen.
public interface AccessibleScreen : AccessibleComponent { }

// A popup, overlay or modal shown over the current screen. Stacked: AccessibilityMgr.ShowUI / HideUI.
public interface AccessibleUI : AccessibleComponent { }

// Marks a static parameterless method AccessibilityMgr runs every frame, for game surfaces with no method to
// hook (detected by state). Prefer a [HarmonyPatch] hook whenever the game has one: polling costs every frame.
[System.AttributeUsage(System.AttributeTargets.Method)]
public sealed class AccessibilityPollerAttribute : System.Attribute { }
