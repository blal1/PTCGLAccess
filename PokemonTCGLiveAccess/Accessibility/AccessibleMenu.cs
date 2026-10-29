using System;
using System.Collections.Generic;

namespace Accessibility;

// A vertical list of options, as in Hearthstone Access: Up/Down read, Home/End jump, Enter or Space select,
// Backspace/Escape go back, numbers jump to an option. Options are text or live text plus an action; the
// game's buttons are wrapped by the screen that builds the menu, never mirrored by a parallel state.
public sealed class AccessibleMenu : AccessibleElement
{
    private sealed class MenuOption
    {
        internal readonly Func<string> m_getText;
        internal readonly Action m_onSelect;

        internal MenuOption(Func<string> getText, Action onSelect)
        {
            m_getText = getText;
            m_onSelect = onSelect;
        }
    }

    private readonly string? m_menuName;
    private readonly Action? m_goBackAction;
    private readonly List<MenuOption> m_options = new();
    private int m_curOptionIdx;

    public AccessibleMenu(AccessibleComponent parent, string? menuName, Action? goBackAction) : base(parent)
    {
        m_menuName = menuName;
        m_goBackAction = goBackAction;
    }

    public int Count => m_options.Count;

    public int Index => m_curOptionIdx;

    public void AddOption(string text, Action onSelect) => m_options.Add(new MenuOption(() => text, onSelect));

    // Text read at the moment it is spoken (a toggle's state, a live counter).
    public void AddOption(Func<string> getText, Action onSelect) => m_options.Add(new MenuOption(getText, onSelect));

    public void Clear()
    {
        m_options.Clear();
        m_curOptionIdx = 0;
    }

    // Returns true when the key was for this menu.
    public bool HandleInput()
    {
        if (AccessibleKey.READ_NEXT_MENU_OPTION.IsPressed()) return ReadOption(m_curOptionIdx + 1);
        if (AccessibleKey.READ_PREV_MENU_OPTION.IsPressed()) return ReadOption(m_curOptionIdx - 1);
        if (AccessibleKey.READ_FIRST_ITEM.IsPressed()) return ReadOption(0);
        if (AccessibleKey.READ_LAST_ITEM.IsPressed()) return ReadOption(m_options.Count - 1);
        if (AccessibleKey.CONFIRM.IsPressed() || AccessibleKey.SPACE.IsPressed()) return SelectOption();
        if (AccessibleKey.BACK.IsPressed() || AccessibleKey.ESCAPE.IsPressed()) return GoBack();
        if (AccessibleKey.TryGetPressedNumber() is { } n) return ReadOption(n - 1);
        return false;
    }

    // Menu name (if any), then the current option.
    public void StartReading(bool readMenuName = true)
    {
        if (readMenuName && !string.IsNullOrEmpty(m_menuName)) Output(m_menuName!);
        if (m_options.Count == 0)
        {
            Output(LocalizationUtils.Get(LocalizationKey.MENU_NO_ITEMS), interrupt: false);
            return;
        }
        m_curOptionIdx = Math.Min(m_curOptionIdx, m_options.Count - 1);
        Output(OptionSpeech(m_curOptionIdx), interrupt: !readMenuName || string.IsNullOrEmpty(m_menuName));
    }

    public void SetIndex(int index) => m_curOptionIdx = Math.Max(0, Math.Min(index, m_options.Count - 1));

    public string GetHelp() => LocalizationUtils.Get(m_goBackAction != null ? LocalizationKey.MENU_HELP_WITH_BACK : LocalizationKey.MENU_HELP);

    public bool GoBack()
    {
        if (m_goBackAction == null) return false;
        m_goBackAction();
        return true;
    }

    private bool ReadOption(int index)
    {
        if (index < 0 || index >= m_options.Count) return false;
        m_curOptionIdx = index;
        Output(OptionSpeech(index));
        return true;
    }

    private bool SelectOption()
    {
        if (m_options.Count == 0) return false;
        m_options[m_curOptionIdx].m_onSelect();
        return true;
    }

    private string OptionSpeech(int index) =>
        LocalizationUtils.Format(LocalizationKey.MENU_OPTION_FORMAT, m_options[index].m_getText(), index + 1, m_options.Count);
}
