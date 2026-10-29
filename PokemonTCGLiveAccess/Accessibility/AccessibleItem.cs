using System.Collections.Generic;

namespace Accessibility;

// Something read line by line (a card, a reward, a quest), as in Hearthstone Access: Down/Up next and previous
// line, Shift+Up repeat, Shift+Down read to the end. Lines are rebuilt on every read, so they are always live.
public abstract class AccessibleItem : AccessibleElement
{
    private List<string> m_lines = new();
    private int m_curLine;

    protected AccessibleItem(AccessibleComponent parent) : base(parent) { }

    // The first line is the short form spoken when the item gets focus (usually the name).
    protected internal abstract List<string> GetLines();

    public string GetLine(int line)
    {
        Refresh();
        return line < m_lines.Count ? m_lines[line] : "";
    }

    public void ReadFirstLine()
    {
        m_curLine = 0;
        ReadCurrentLine();
    }

    public void Reset() => m_curLine = 0;

    // Returns true when the key was for this item.
    public virtual bool HandleInput()
    {
        if (AccessibleKey.READ_NEXT_LINE.IsPressed()) return Move(1);
        if (AccessibleKey.READ_PREV_LINE.IsPressed()) return Move(-1);
        if (AccessibleKey.READ_CUR_LINE.IsPressed())
        {
            ReadCurrentLine();
            return true;
        }
        if (AccessibleKey.READ_TO_END.IsPressed())
        {
            Refresh();
            for (var i = m_curLine; i < m_lines.Count; i++) Output(m_lines[i], interrupt: i == m_curLine);
            m_curLine = System.Math.Max(0, m_lines.Count - 1);
            return true;
        }
        return false;
    }

    private bool Move(int delta)
    {
        Refresh();
        var target = m_curLine + delta;
        if (target < 0 || target >= m_lines.Count) return true; // at an edge: swallow the key, stay put
        m_curLine = target;
        Output(m_lines[m_curLine]);
        return true;
    }

    private void ReadCurrentLine()
    {
        Refresh();
        if (m_curLine < m_lines.Count) Output(m_lines[m_curLine]);
    }

    private void Refresh()
    {
        m_lines = GetLines();
        m_lines.RemoveAll(string.IsNullOrWhiteSpace);
        if (m_curLine >= m_lines.Count) m_curLine = System.Math.Max(0, m_lines.Count - 1);
    }
}
