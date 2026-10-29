using System;
using System.Collections.Generic;

namespace Accessibility;

// A horizontal row of readable items (a hand, a bench, a reward list), as in Hearthstone Access: Left/Right move,
// Home/End jump, numbers jump to the Nth item, and the focused item handles Up/Down line reading itself.
public sealed class AccessibleListOfItems<T> : AccessibleElement where T : AccessibleItem
{
    private readonly Func<List<T>> m_getItems;
    private readonly string m_emptyText;
    private List<T> m_items = new();
    private int m_curItemIdx;

    // Items are fetched live on every key, so the list follows the game (cards drawn, Pokémon knocked out).
    public AccessibleListOfItems(AccessibleComponent parent, Func<List<T>> getItems, string emptyText) : base(parent)
    {
        m_getItems = getItems;
        m_emptyText = emptyText;
    }

    public T? Current
    {
        get
        {
            Refresh();
            return m_curItemIdx < m_items.Count ? m_items[m_curItemIdx] : null;
        }
    }

    public int Index => m_curItemIdx;

    public int Count
    {
        get
        {
            Refresh();
            return m_items.Count;
        }
    }

    public void StartReading(int index = 0)
    {
        Refresh();
        if (m_items.Count == 0)
        {
            Output(m_emptyText);
            return;
        }
        ReadItem(Math.Max(0, Math.Min(index, m_items.Count - 1)));
    }

    // Returns true when the key was for this list or its focused item.
    public bool HandleInput()
    {
        if (AccessibleKey.READ_NEXT_ITEM.IsPressed()) return Move(1);
        if (AccessibleKey.READ_PREV_ITEM.IsPressed()) return Move(-1);
        if (AccessibleKey.READ_FIRST_ITEM.IsPressed()) return Jump(0);
        if (AccessibleKey.READ_LAST_ITEM.IsPressed()) return Jump(int.MaxValue);
        if (AccessibleKey.TryGetPressedNumber() is { } n) return Jump(n - 1);
        return Current?.HandleInput() ?? false;
    }

    private bool Move(int delta)
    {
        Refresh();
        var target = m_curItemIdx + delta;
        if (target >= 0 && target < m_items.Count) ReadItem(target);
        return true;
    }

    private bool Jump(int index)
    {
        Refresh();
        if (m_items.Count > 0) ReadItem(Math.Min(index, m_items.Count - 1));
        return true;
    }

    private void ReadItem(int index)
    {
        m_curItemIdx = index;
        var item = m_items[index];
        item.Reset();
        Output(LocalizationUtils.Format(LocalizationKey.MENU_OPTION_FORMAT, item.GetLine(0), index + 1, m_items.Count));
    }

    private void Refresh()
    {
        m_items = m_getItems();
        if (m_curItemIdx >= m_items.Count) m_curItemIdx = Math.Max(0, m_items.Count - 1);
    }
}
