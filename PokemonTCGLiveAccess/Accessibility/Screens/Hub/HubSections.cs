using System;
using System.Collections.Generic;

namespace Accessibility;

// The section-stack key handling shared by the newer hub components: Tab cycles sections, Up/Down at a section's edge
// crosses to the next one, Escape goes back. Sections keep their own position.
internal sealed class SectionStack
{
    public readonly List<INavSection> Sections = new();
    public int Active;
    public Action? OnBack;

    public INavSection? Current => Active >= 0 && Active < Sections.Count ? Sections[Active] : null;

    // Rebuild the list, staying on the same section object when it survived.
    public void Set(IEnumerable<INavSection> sections, INavSection? keep)
    {
        Sections.Clear();
        Sections.AddRange(sections);
        var i = keep != null ? Sections.IndexOf(keep) : -1;
        Active = i >= 0 ? i : Math.Min(Math.Max(Active, 0), Math.Max(Sections.Count - 1, 0));
    }

    public void Focus(INavSection? section, bool announce = true, bool interrupt = true)
    {
        var i = section != null ? Sections.IndexOf(section) : -1;
        if (i >= 0) Active = i;
        Current?.FocusFirstRow(-1, announce, interrupt);
    }

    public void HandleInput()
    {
        if (OnBack != null && (AccessibleKey.ESCAPE.IsPressed() || AccessibleKey.BACK.IsPressed()))
        {
            OnBack();
            return;
        }
        if (Current is not { } cur) return;
        // Shift+Tab first: check the exact-modifier keys separately, a section may not consume Tab.
        var prevSection = AccessibleKey.PREV_SECTION.IsPressed();
        if (prevSection || AccessibleKey.NEXT_SECTION.IsPressed())
        {
            if (Sections.Count < 2) return;
            Active = (Active + (prevSection ? -1 : 1) + Sections.Count) % Sections.Count;
            Sections[Active].FocusFirstRow(-1, announce: true, interruptPrior: true);
            return;
        }
        if (AccessibleKey.READ_PREV_MENU_OPTION.IsPressed() && Active > 0 && cur.IsAtTopRow)
        {
            Active--;
            Sections[Active].FocusLastRow(-1, announce: true, interruptPrior: true);
            return;
        }
        if (AccessibleKey.READ_NEXT_MENU_OPTION.IsPressed() && Active < Sections.Count - 1 && cur.IsAtBottomRow)
        {
            Active++;
            Sections[Active].FocusFirstRow(-1, announce: true, interruptPrior: true);
            return;
        }
        cur.HandleInput();
    }
}

// A section over plain data rather than Selectables: virtualized scrollers only spawn the cells on screen, and map nodes
// aren't Selectables at all. Rows are read live. No number jump: digits belong to the hub tab shortcuts (see HubUi).
internal sealed class DataListSection : AccessibleElement, INavSection
{
    private readonly LocalizationKey? m_name;   // read when spoken: pollers build these before localization is loaded
    private readonly Func<int> m_count;
    private readonly Func<int, string> m_label;
    private readonly Action<int>? m_activate;
    private readonly bool m_horizontal;
    private int m_index;

    // Set for a row the game owns (the current set of the carousel): the position comes from here, moves go to MoveTo.
    public Func<int>? LiveIndex;
    public Action<int>? MoveTo;

    public DataListSection(AccessibleComponent parent, LocalizationKey? name, Func<int> count, Func<int, string> label, Action<int>? activate, bool horizontal = false) : base(parent)
    {
        m_name = name;
        m_count = count;
        m_label = label;
        m_activate = activate;
        m_horizontal = horizontal;
    }

    public int Index => LiveIndex?.Invoke() ?? m_index;
    public int Count => m_count();
    public int CurrentColumn => 0;
    public bool IsAtTopRow => m_horizontal || Index <= 0;
    public bool IsAtBottomRow => m_horizontal || Index >= Count - 1;

    public void SetIndex(int index) => m_index = index;

    public void FocusFirst(bool announce = true, bool interruptPrior = true) => Enter(announce, interruptPrior);
    public void FocusFirstRow(int preferredColumn = -1, bool announce = true, bool interruptPrior = true) => Enter(announce, interruptPrior);
    public void FocusLastRow(int preferredColumn = -1, bool announce = true, bool interruptPrior = true) => Enter(announce, interruptPrior);

    public void RefocusCurrent(bool announce = true, bool interrupt = true)
    {
        if (announce) Speak(interrupt);
    }

    public bool HandleInput()
    {
        var next = m_horizontal ? AccessibleKey.READ_NEXT_ITEM.IsPressed() : AccessibleKey.READ_NEXT_MENU_OPTION.IsPressed();
        var prev = m_horizontal ? AccessibleKey.READ_PREV_ITEM.IsPressed() : AccessibleKey.READ_PREV_MENU_OPTION.IsPressed();
        if (next) Move(Index + 1);
        else if (prev) Move(Index - 1);
        else if (AccessibleKey.READ_FIRST_ITEM.IsPressed()) Move(0);
        else if (AccessibleKey.READ_LAST_ITEM.IsPressed()) Move(Count - 1);
        else if (AccessibleKey.CONFIRM.IsPressed() || AccessibleKey.SPACE.IsPressed())
        {
            if (Index >= 0 && Index < Count) m_activate?.Invoke(Index);
        }
        else return false;
        return true;
    }

    private void Enter(bool announce, bool interrupt)
    {
        if (!announce) return;
        if (m_name != null)
        {
            Output(LocalizationUtils.Get(m_name), interrupt);
            interrupt = false;
        }
        Speak(interrupt);
    }

    private void Move(int index)
    {
        if (index < 0 || index >= Count) return;
        if (MoveTo != null) MoveTo(index);
        else m_index = index;
        Speak(interrupt: true);
    }

    private void Speak(bool interrupt)
    {
        var count = Count;
        if (count == 0)
        {
            Output(LocalizationUtils.Get(LocalizationKey.HUB_EMPTY), interrupt);
            return;
        }
        m_index = Math.Max(0, Math.Min(m_index, count - 1));
        var i = Math.Max(0, Math.Min(Index, count - 1));
        Output(Announce.Item(m_label(i), null, i + 1, count), interrupt);
    }
}
