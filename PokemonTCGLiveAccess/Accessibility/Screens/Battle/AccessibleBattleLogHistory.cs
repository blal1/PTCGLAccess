using System.Collections.Generic;
using System.Linq;
using _Rainier.Scripts.BattleLog;
using UnityEngine;

namespace Accessibility;

// History of the match: the game's log panels (BattleLogModalContainerMenu, FullScreenBattleLogMenu) draw
// recycled cells, so we read the BattleLog data they draw from instead, walked as BattleLogExporter walks it.
// The list is a snapshot taken on open. Read-only: nothing is opened in the game.
internal sealed class AccessibleBattleLogHistory : AccessibleUI
{
    private static readonly AccessibleBattleLogHistory s_instance = new();

    private readonly List<string> m_lines = new();
    private readonly List<int> m_phaseStarts = new();
    private int m_line;

    // The log is the game's IBattleLogStateProvider, implemented by BattleLog, which also enumerates its data.
    internal static void Open(object? log)
    {
        var s = s_instance;
        s.m_lines.Clear();
        s.m_phaseStarts.Clear();
        if (log is IEnumerable<IBattleLogData> data)
        {
            foreach (var phase in data.OfType<IBattleLogPhaseData>())
            {
                s.m_phaseStarts.Add(s.m_lines.Count);
                var title = phase.BattlePhase is PhaseType.Player or PhaseType.Opponent ? phase.PlainTextPhaseTitle : phase.PhaseTitle;
                if (!string.IsNullOrWhiteSpace(title)) s.m_lines.Add(title);
                foreach (var entry in phase.LogEntries) if (entry != null) s.m_lines.AddRange(BattleLogNarrator.LinesOf(entry));
            }
        }
        s.m_line = Mathf.Max(0, s.m_lines.Count - 1);   // the newest line: what the player most likely wants
        AccessibilityMgr.ShowUI(s);
    }

    public string GetHelp() => LocalizationUtils.Get(LocalizationKey.BATTLE_LOG_HELP);

    public void OnGainedFocus()
    {
        Say(LocalizationUtils.Get(m_lines.Count == 0 ? LocalizationKey.BATTLE_LOG_EMPTY : LocalizationKey.BATTLE_LOG_TITLE));
        if (m_lines.Count > 0) AccessibilityMgr.Output(this, Line(m_line), interrupt: false);
    }

    public void HandleInput()
    {
        if (AccessibleKey.BACK.IsPressed() || AccessibleKey.ESCAPE.IsPressed() || AccessibleKey.BATTLE_LOG.IsPressed())
        {
            AccessibilityMgr.HideUI(this);
            return;
        }
        if (m_lines.Count == 0) return;
        if (AccessibleKey.READ_NEXT_LINE.IsPressed()) { Go(m_line + 1); return; }
        if (AccessibleKey.READ_PREV_LINE.IsPressed()) { Go(m_line - 1); return; }
        if (AccessibleKey.READ_FIRST_ITEM.IsPressed()) { Go(0); return; }
        if (AccessibleKey.READ_LAST_ITEM.IsPressed()) { Go(m_lines.Count - 1); return; }
        // Left/Right: the previous / next turn, by its heading.
        if (AccessibleKey.READ_NEXT_ITEM.IsPressed())
        {
            int next = m_phaseStarts.FindIndex(p => p > m_line);
            if (next >= 0) Go(m_phaseStarts[next]);
            return;
        }
        if (AccessibleKey.READ_PREV_ITEM.IsPressed())
        {
            int prev = m_phaseStarts.FindLastIndex(p => p < m_line);
            if (prev >= 0) Go(m_phaseStarts[prev]);
        }
    }

    private void Go(int line)
    {
        if (line < 0 || line >= m_lines.Count) return;
        m_line = line;
        Say(Line(line));
    }

    private string Line(int i) => LocalizationUtils.Format(LocalizationKey.MENU_OPTION_FORMAT, m_lines[i], i + 1, m_lines.Count);

    private void Say(string text) => AccessibilityMgr.Output(this, text);
}
