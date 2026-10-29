using System.Reflection;
using HarmonyLib;
using UnityEngine.UI;
using static Accessibility.GameReflect;

namespace Accessibility;

// Professor speech-bubble dialogue, driven by DialogueManager. This is the actual driver of the "Welcome to
// Pokémon Trading Card Game Live!" FTUE bubble on the Profile screen — not ProfessorTooltip (that's a simpler
// click-an-icon-to-show-help tooltip).
//
// DialogueManager.StartDialogue(List<Dialogue>) kicks off a chain. Each Dialogue in the chain
// triggers SetSpeaker() (private) which formats the localised text into `currentText` and
// starts a typewriter animation coroutine. NextSpeaker() advances: first press cancels the
// animation and shows full text, second press moves to the next dialogue. After the last
// dialogue, NextSpeaker() calls HideDialogue() to tear everything down.
//
// We hook SetSpeaker to capture each dialogue's full text from `currentText` and speak it — works for every
// link in the chain — and HideDialogue to close. HideDialogue fires whenever the game likes, including while
// we sit beneath an overlay (measured: the tutorial closed its bubble with the reward popup on top of us);
// HideUI removes a buried UI too, so we never resume onto a dead bubble that swallows every key. The game's
// own "a dialogue is showing" flag (currentDialogue, nulled by HideDialogue) is checked as a backstop.
//
// For input we call NextSpeaker() twice per user keypress: that reliably skips the typewriter
// animation AND advances to the next dialogue in one user action. The double-call is safe on
// the final dialogue because HideDialogue sets canClick=false and NextSpeaker no-ops then.
internal sealed class AccessibleDialogue : AccessibleUI
{
    private static readonly AccessibleDialogue s_instance = new();
    private static readonly FieldInfo? s_hasExitCondition = AccessTools.Field(typeof(DialogueManager), "hasExitCondition");

    private string m_lastSpoken = string.Empty;
    private Selectable? m_lastFocusAnnounced;

    private static DialogueManager? Manager => DialogueManager.instance;

    private static bool IsOpen => Manager is { } m && ReadField<object>(m, "currentDialogue") != null;

    private static string? CurrentText => ReadField<string>(Manager, "currentText");

    public void HandleInput()
    {
        if (!IsOpen)
        {
            Close();
            return;
        }

        // Coaching-bubble case: an FTUE bubble shown ALONGSIDE a highlighted action button
        // (e.g. "you can redeem the first tier reward… let's see what you get!" → COLLECT ALL).
        // The game starts these with exitCondition=false, so DialogueManager.NextSpeaker() is a
        // no-op — the only way forward is to ACTIVATE the highlighted button. So when the FTUE
        // has a usable focused element, route Enter/Space to it (and announce it once) instead
        // of trying to advance the text.
        //
        // CRUCIAL: gate this on hasExitCondition == false. A normal *advanceable* welcome/info
        // bubble (hasExitCondition == true) often has an indicator already pre-focusing the NEXT
        // step's target; routing Enter to that target would activate it early and re-show the
        // bubble in a loop. Advanceable bubbles must always advance via NextSpeaker below.
        var focus = FtueTracker.FtueIsRunning && !DialogueAdvanceable() ? FtueTracker.FocusedElement : null;
        if (IsUsableAction(focus))
        {
            if (focus != m_lastFocusAnnounced)
            {
                m_lastFocusAnnounced = focus;
                AccessibilityMgr.Output(this, KeyboardNavigator.DefaultLabel(focus!), interrupt: false);
            }
            if (AccessibleKey.CONFIRM.IsPressed() || AccessibleKey.SPACE.IsPressed())
            {
                focus!.Select();
                // Fire what the tutorial listens on — see FtueTracker. A coached Toggle is a tab: select it,
                // never flip it back off.
                if (focus is Button btn) btn.onClick?.Invoke();
                else if (focus is Toggle tog && !tog.isOn) tog.isOn = true;
            }
            return;
        }
        m_lastFocusAnnounced = null;

        // Plain dialogue: Enter / Space / Esc / Backspace advances. Null-conditional both calls: the first
        // NextSpeaker can trigger HideDialogue at the end of a chain.
        if (AccessibleKey.CONFIRM.IsPressed() || AccessibleKey.SPACE.IsPressed() || AccessibleKey.ESCAPE.IsPressed() || AccessibleKey.BACK.IsPressed())
        {
            Manager?.NextSpeaker();
            Manager?.NextSpeaker();
        }
    }

    public string GetHelp() => LocalizationUtils.Get(LocalizationKey.POPUP_DIALOGUE_HELP);

    public void OnGainedFocus()
    {
        m_lastFocusAnnounced = null;
        var text = CurrentText;
        if (string.IsNullOrWhiteSpace(text)) return;
        m_lastSpoken = text!;
        AccessibilityMgr.Output(this, text!, interrupt: true);
    }

    private void OnSpeaker()
    {
        if (!IsOpen) return;
        if (!AccessibilityMgr.IsFocused(this))
        {
            AccessibilityMgr.ShowUI(this);
            return;
        }
        var text = CurrentText;
        if (string.IsNullOrWhiteSpace(text) || text == m_lastSpoken) return;
        m_lastSpoken = text!;
        AccessibilityMgr.Output(this, text!, interrupt: true);
    }

    private void Close()
    {
        m_lastSpoken = string.Empty;
        m_lastFocusAnnounced = null;
        AccessibilityMgr.HideUI(this);
    }

    // A focused element we can drive: active, interactable, and a Button/Toggle (so Enter has a
    // meaningful effect). focusOnly highlights and other non-interactive targets are excluded.
    private static bool IsUsableAction(Selectable? s)
        => s != null && s.isActiveAndEnabled && s.IsInteractable() && (s is Button || s is Toggle);

    // True when the current dialogue advances on click (started with exitCondition=true — the
    // common welcome/info bubbles, whose NextSpeaker fires the FTUE animator's NextState). False
    // for coaching bubbles (exitCondition=false) that stay until the player performs a highlighted
    // action. Read from the game's own flag. Defaults to true if unreadable, so we never hijack
    // Enter away from a normal advanceable dialogue.
    private static bool DialogueAdvanceable()
    {
        var m = Manager;
        if (m == null) return true;
        return s_hasExitCondition?.GetValue(m) is bool b ? b : true;
    }

    [HarmonyPatch(typeof(DialogueManager), "SetSpeaker")]
    private static class SetSpeakerHook
    {
        private static void Postfix() => s_instance.OnSpeaker();
    }

    [HarmonyPatch(typeof(DialogueManager), "HideDialogue")]
    private static class HideDialogueHook
    {
        private static void Postfix() => s_instance.Close();
    }
}
