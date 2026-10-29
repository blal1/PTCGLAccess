using System.Collections.Generic;
using System.Reflection;
using System.Text;
using HarmonyLib;
using TMPro;
using TPCI.Updater.Components;
using TPCI.Updater.Components.ReleaseNotes;
using UnityEngine;
using UnityEngine.UI;

namespace Accessibility;

// The standalone updater (Updater.exe), a separate Unity process from the game. It downloads and applies the patch
// on its own, driven by UpdaterComponent: a status line, a progress meter, an error popup, release notes, and the
// RELEASE NOTES / SUPPORT / RETRY / PLAY buttons. None of it speaks or takes the keyboard natively.
//
// Pure polling, no Harmony patches: nothing here needs to intercept the updater's logic, only read its visible state
// and drive its existing buttons. Each frame:
//   - speak the game's own localized status line when it changes (status_checking / downloading / applying / …),
//   - speak coarse progress milestones (25 / 50 / 75 %),
//   - speak the error popup when it appears and the release notes when that panel finishes opening,
//   - move focus to PLAY / RETRY the moment it becomes usable,
//   - Up/Down/Tab over the four buttons; Enter fires the button's real onClick (PLAY relaunches the game and quits
//     the updater — we never reimplement the launch).
// Field names verified against the updater's own Assembly-CSharp (Updater\1.5.0).
//
// Keys are read with the legacy UnityEngine.Input: the updater ships no Input System package, so the main mod's
// AccessibleKey (Keyboard.current) can't be linked here.
internal sealed class AccessibleUpdater
{
    private static readonly FieldInfo? s_statusLocText = AccessTools.Field(typeof(UpdaterComponent), "statusLocText");
    private static readonly FieldInfo? s_meterController = AccessTools.Field(typeof(UpdaterComponent), "meterController");
    private static readonly FieldInfo? s_progressLocText = AccessTools.Field(typeof(MeterController), "progressLocText");
    private static readonly FieldInfo? s_playButton = AccessTools.Field(typeof(UpdaterComponent), "playButton");
    private static readonly FieldInfo? s_retryButton = AccessTools.Field(typeof(UpdaterComponent), "retryButton");
    private static readonly FieldInfo? s_supportButton = AccessTools.Field(typeof(UpdaterComponent), "supportButton");
    private static readonly FieldInfo? s_errorPopup = AccessTools.Field(typeof(UpdaterComponent), "errorPopup");
    private static readonly FieldInfo? s_releaseNotesView = AccessTools.Field(typeof(UpdaterComponent), "releaseNotesView");
    private static readonly FieldInfo? s_releaseNotesButton = AccessTools.Field(typeof(ReleaseNotesView), "releaseNotesButton");
    private static readonly FieldInfo? s_contentTransform = AccessTools.Field(typeof(ReleaseNotesView), "contentTransform");
    private static readonly FieldInfo? s_versionText = AccessTools.Field(typeof(ReleaseNotesView), "versionText");
    private static readonly FieldInfo? s_dateText = AccessTools.Field(typeof(ReleaseNotesView), "dateText");
    private static readonly FieldInfo? s_isReleasePanelOpen = AccessTools.Field(typeof(ReleaseNotesView), "isReleasePanelOpen");

    private UpdaterComponent? m_comp;
    private TMP_Text? m_statusText;
    private TMP_Text? m_progressText;
    private Button? m_play;
    private Button? m_retry;
    private Button? m_support;
    private Button? m_notesButton;
    private ReleaseNotesView? m_notesView;
    private GameObject? m_errorPopup;

    private int m_index;
    private string m_lastStatus = "";
    private int m_lastMilestone;   // 0..4 (x25%); 0 counts as spoken so 0% is never announced
    private bool m_errorWasActive;
    private bool m_playWasUsable;
    private bool m_retryWasUsable;
    private bool m_notesWereOpen;

    public void Tick()
    {
        if (m_comp == null)
        {
            m_comp = Object.FindAnyObjectByType<UpdaterComponent>();
            if (m_comp == null) return;
            Wire(m_comp);
        }
        PollStatus();
        PollProgress();
        PollError();
        PollNotes();
        PollButtonReadiness();
        if (Application.isFocused) HandleInput();
    }

    private void Wire(UpdaterComponent comp)
    {
        m_statusText = TextOf(s_statusLocText?.GetValue(comp) as Component);
        var meter = s_meterController?.GetValue(comp) as MeterController;
        m_progressText = meter != null ? TextOf(s_progressLocText?.GetValue(meter) as Component) : null;
        m_play = s_playButton?.GetValue(comp) as Button;
        m_retry = s_retryButton?.GetValue(comp) as Button;
        m_support = s_supportButton?.GetValue(comp) as Button;
        m_errorPopup = (s_errorPopup?.GetValue(comp) as Component)?.gameObject;
        m_notesView = s_releaseNotesView?.GetValue(comp) as ReleaseNotesView;
        m_notesButton = m_notesView != null ? s_releaseNotesButton?.GetValue(m_notesView) as Button : null;
        // No announcement on entry: the status line is empty at Start() and the first real phase lands via PollStatus.
        Log.Info($"Updater wired: status={m_statusText != null} progress={m_progressText != null} play={m_play != null} " +
                 $"retry={m_retry != null} support={m_support != null} notes={m_notesButton != null} error={m_errorPopup != null}");
    }

    private static TMP_Text? TextOf(Component? c) => c != null ? c.GetComponent<TMP_Text>() : null;

    // Reading order, top to bottom: RELEASE NOTES (top left), SUPPORT, RETRY, PLAY (bottom right). PLAY and RETRY start
    // active but non-interactable (UpdaterComponent.Start), so they stay in the list and read "unavailable".
    private List<Button> Buttons()
    {
        var items = new List<Button>();
        foreach (var b in new[] { m_notesButton, m_support, m_retry, m_play })
            if (b != null && b.gameObject.activeInHierarchy) items.Add(b);
        return items;
    }

    #region Keys

    private static bool Shift => Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);

    private void HandleInput()
    {
        var buttons = Buttons();
        if (Input.GetKeyDown(KeyCode.F1)) { Output(LocalizationUtils.Get(LocalizationKey.UPDATER_HELP), interrupt: true); return; }
        if (buttons.Count == 0) return;
        m_index = Mathf.Clamp(m_index, 0, buttons.Count - 1);

        if (Input.GetKeyDown(KeyCode.DownArrow) || (Input.GetKeyDown(KeyCode.Tab) && !Shift)) Move(buttons, 1);
        else if (Input.GetKeyDown(KeyCode.UpArrow) || (Input.GetKeyDown(KeyCode.Tab) && Shift)) Move(buttons, -1);
        else if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter) || Input.GetKeyDown(KeyCode.Space))
        {
            var b = buttons[m_index];
            if (b.IsInteractable()) b.onClick.Invoke();
            else Speak(buttons, interrupt: true);
        }
        // Escape closes the release-notes panel when it's open; the updater itself has no cancel / quit affordance.
        else if (Input.GetKeyDown(KeyCode.Escape) && m_notesWereOpen && m_notesButton != null) m_notesButton.onClick.Invoke();
    }

    private void Move(List<Button> buttons, int dir)
    {
        m_index = (m_index + dir + buttons.Count) % buttons.Count;
        buttons[m_index].Select();
        Speak(buttons, interrupt: true);
    }

    private void Speak(List<Button> buttons, bool interrupt)
    {
        var b = buttons[m_index];
        var label = Label(b);
        if (!b.IsInteractable()) label = Announce.WithState(label, LocalizationKey.GLOBAL_DISABLED);
        Output(LocalizationUtils.Format(LocalizationKey.MENU_OPTION_FORMAT, label, m_index + 1, buttons.Count), interrupt);
    }

    // The button's own worded label (all four carry one).
    private static string Label(Button b)
    {
        foreach (var t in b.GetComponentsInChildren<TMP_Text>(includeInactive: false))
            if (!string.IsNullOrWhiteSpace(t.text)) return t.text.Trim();
        return b.gameObject.name;
    }

    #endregion

    #region Polling

    private void PollStatus()
    {
        var s = m_statusText != null ? m_statusText.text : null;
        if (string.IsNullOrWhiteSpace(s) || s == m_lastStatus) return;
        m_lastStatus = s!;
        // The game's own localized phase text. A new phase supersedes the previous one.
        Output(s!, interrupt: true);
    }

    // Milestones at 25 / 50 / 75 % so a long download isn't silent without chattering every frame. 100% is left to the
    // "Update complete!" status line, which is clearer.
    private void PollProgress()
    {
        var updater = TPCI.Updater.Updater.Instance;
        if (updater == null) return;
        var milestone = Mathf.Clamp(Mathf.FloorToInt(updater.Progress * 4f), 0, 4);
        if (milestone <= m_lastMilestone || milestone >= 4)
        {
            m_lastMilestone = Mathf.Max(m_lastMilestone, milestone);
            return;
        }
        m_lastMilestone = milestone;
        // Prefer the meter's own text: it's the game's and already localized.
        var pct = m_progressText != null && !string.IsNullOrWhiteSpace(m_progressText.text)
            ? m_progressText.text.Trim()
            : LocalizationUtils.Format(LocalizationKey.UPDATER_PROGRESS_FORMAT, milestone * 25);
        Output(pct, interrupt: false);   // queued after the phase status, never cutting it off
    }

    private void PollError()
    {
        var active = m_errorPopup != null && m_errorPopup.activeInHierarchy;
        if (active && !m_errorWasActive && ReadAllText(m_errorPopup!.transform) is { } msg) Output(msg, interrupt: true);
        m_errorWasActive = active;
    }

    // isReleasePanelOpen flips at the end of the open/close animation, so a false→true edge means the panel has finished
    // opening and its content (filled once by Initialize) is readable.
    private void PollNotes()
    {
        if (m_notesView == null) return;
        var open = s_isReleasePanelOpen?.GetValue(m_notesView) is true;
        if (open && !m_notesWereOpen && ReadNotes() is { } notes) Output(notes, interrupt: true);
        m_notesWereOpen = open;
    }

    // When PLAY or RETRY becomes usable (success / failure resolved), move focus onto it, queued after the status line
    // that just changed ("Update complete!" then "Play, 4 of 4"), so the actionable button is right there.
    private void PollButtonReadiness()
    {
        var playUsable = m_play != null && m_play.isActiveAndEnabled && m_play.interactable;
        if (playUsable && !m_playWasUsable) FocusButton(m_play!);
        m_playWasUsable = playUsable;

        var retryUsable = m_retry != null && m_retry.isActiveAndEnabled && m_retry.interactable;
        if (retryUsable && !m_retryWasUsable) FocusButton(m_retry!);
        m_retryWasUsable = retryUsable;
    }

    private void FocusButton(Button target)
    {
        var buttons = Buttons();
        var i = buttons.IndexOf(target);
        if (i < 0) return;
        m_index = i;
        target.Select();
        Speak(buttons, interrupt: false);
    }

    private string? ReadNotes()
    {
        var sb = new StringBuilder();
        Append(sb, (s_versionText?.GetValue(m_notesView) as TMP_Text)?.text);
        Append(sb, (s_dateText?.GetValue(m_notesView) as TMP_Text)?.text);
        if (s_contentTransform?.GetValue(m_notesView) is Transform content) Append(sb, ReadAllText(content));
        return sb.Length == 0 ? null : sb.ToString();
    }

    // Every non-empty visible TMP label under a transform, in hierarchy order.
    private static string? ReadAllText(Transform root)
    {
        var sb = new StringBuilder();
        foreach (var t in root.GetComponentsInChildren<TMP_Text>(includeInactive: false)) Append(sb, t.text);
        return sb.Length == 0 ? null : sb.ToString();
    }

    private static void Append(StringBuilder sb, string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return;
        if (sb.Length > 0) sb.Append(". ");
        sb.Append(s!.Trim());
    }

    #endregion

    private static void Output(string text, bool interrupt) => ScreenReader.Output(text, interrupt);
}
