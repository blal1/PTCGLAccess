using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;

namespace Accessibility;

// Every key the mod reacts to, declared once with its exact modifiers (as in Hearthstone Access): Shift+Up is
// never also Up. Feature-specific keys live in their own partial file (AccessibleKey.Battle.cs, ...), so the
// whole key map stays greppable and two features can't silently grab the same combination.
//
// The game uses the Input System package only (legacy UnityEngine.Input throws since game 1.43), so keys are
// read from Keyboard.current. Letters follow the active layout: A is the key that types "a", even on AZERTY.
public sealed partial class AccessibleKey
{
    #region Global
    public static readonly AccessibleKey HELP = new(KeyCode.F1, anyModifiers: true);
    public static readonly AccessibleKey TOGGLE_ACCESSIBILITY = new(KeyCode.F8);
    #endregion

    #region Common
    public static readonly AccessibleKey CONFIRM = new(KeyCode.Return);
    public static readonly AccessibleKey SPACE = new(KeyCode.Space);
    public static readonly AccessibleKey BACK = new(KeyCode.Backspace);
    public static readonly AccessibleKey ESCAPE = new(KeyCode.Escape);
    #endregion

    #region Menus and lists
    public static readonly AccessibleKey READ_NEXT_MENU_OPTION = new(KeyCode.DownArrow);
    public static readonly AccessibleKey READ_PREV_MENU_OPTION = new(KeyCode.UpArrow);
    public static readonly AccessibleKey READ_NEXT_ITEM = new(KeyCode.RightArrow);
    public static readonly AccessibleKey READ_PREV_ITEM = new(KeyCode.LeftArrow);
    public static readonly AccessibleKey READ_FIRST_ITEM = new(KeyCode.Home);
    public static readonly AccessibleKey READ_LAST_ITEM = new(KeyCode.End);
    public static readonly AccessibleKey NEXT_SECTION = new(KeyCode.Tab);
    public static readonly AccessibleKey PREV_SECTION = new(KeyCode.Tab, shift: true);
    #endregion

    #region Reading an item line by line
    public static readonly AccessibleKey READ_NEXT_LINE = new(KeyCode.DownArrow);
    public static readonly AccessibleKey READ_PREV_LINE = new(KeyCode.UpArrow);
    public static readonly AccessibleKey READ_CUR_LINE = new(KeyCode.UpArrow, shift: true);
    public static readonly AccessibleKey READ_TO_END = new(KeyCode.DownArrow, shift: true);
    #endregion

    private readonly KeyCode m_keyCode;
    private readonly bool m_shift;
    private readonly bool m_ctrl;
    private readonly bool m_alt;
    private readonly bool m_anyModifiers;

    private AccessibleKey(KeyCode keyCode, bool shift = false, bool ctrl = false, bool alt = false, bool anyModifiers = false)
    {
        m_keyCode = keyCode;
        m_shift = shift;
        m_ctrl = ctrl;
        m_alt = alt;
        m_anyModifiers = anyModifiers;
    }

    // True on the frame the key goes down with exactly the declared modifiers.
    public bool IsPressed()
    {
        if (!IsKeyDown(m_keyCode)) return false;
        return m_anyModifiers || (IsHoldingShift() == m_shift && IsHoldingCtrl() == m_ctrl && IsHoldingAlt() == m_alt);
    }

    // Number row or numpad 1..9 then 0 (=10), for "jump to the Nth item". Null when none was pressed this frame.
    public static int? TryGetPressedNumber()
    {
        for (var i = 0; i <= 9; i++)
        {
            if (IsKeyDown(KeyCode.Alpha0 + i) || IsKeyDown(KeyCode.Keypad0 + i)) return i == 0 ? 10 : i;
        }
        return null;
    }

    public static bool IsHoldingShift() => IsKeyHeld(KeyCode.LeftShift) || IsKeyHeld(KeyCode.RightShift);

    public static bool IsHoldingCtrl() => IsKeyHeld(KeyCode.LeftControl) || IsKeyHeld(KeyCode.RightControl);

    public static bool IsHoldingAlt() => IsKeyHeld(KeyCode.LeftAlt) || IsKeyHeld(KeyCode.RightAlt);

    private static bool IsKeyDown(KeyCode code) =>
        Control(code)?.wasPressedThisFrame == true || (code == KeyCode.Return && Control(KeyCode.KeypadEnter)?.wasPressedThisFrame == true);

    private static bool IsKeyHeld(KeyCode code) => Control(code)?.isPressed == true;

    private static KeyControl? Control(KeyCode code)
    {
        var kb = Keyboard.current;
        if (kb == null) return null;
        // Key.A is the physical US position (Q on AZERTY): look letters up by the character they type.
        if (code >= KeyCode.A && code <= KeyCode.Z) return kb.FindKeyOnCurrentKeyboardLayout(((char)code).ToString());
        switch (code)
        {
            case KeyCode.Minus: return kb.FindKeyOnCurrentKeyboardLayout("-");
            case KeyCode.Equals: return kb.FindKeyOnCurrentKeyboardLayout("=");
            case KeyCode.Plus: return kb.FindKeyOnCurrentKeyboardLayout("+");
        }
        var key = Map(code);
        return key == Key.None ? null : kb[key];
    }

    private static Key Map(KeyCode code)
    {
        // Input System orders the digit row 1..9 then 0, unlike KeyCode's 0..9.
        if (code == KeyCode.Alpha0) return Key.Digit0;
        if (code >= KeyCode.Alpha1 && code <= KeyCode.Alpha9) return Key.Digit1 + (code - KeyCode.Alpha1);
        if (code >= KeyCode.Keypad0 && code <= KeyCode.Keypad9) return Key.Numpad0 + (code - KeyCode.Keypad0);
        if (code >= KeyCode.F1 && code <= KeyCode.F12) return Key.F1 + (code - KeyCode.F1);
        switch (code)
        {
            case KeyCode.Escape: return Key.Escape;
            case KeyCode.Backspace: return Key.Backspace;
            case KeyCode.Delete: return Key.Delete;
            case KeyCode.Tab: return Key.Tab;
            case KeyCode.Return: return Key.Enter;
            case KeyCode.KeypadEnter: return Key.NumpadEnter;
            case KeyCode.Space: return Key.Space;
            case KeyCode.UpArrow: return Key.UpArrow;
            case KeyCode.DownArrow: return Key.DownArrow;
            case KeyCode.LeftArrow: return Key.LeftArrow;
            case KeyCode.RightArrow: return Key.RightArrow;
            case KeyCode.Home: return Key.Home;
            case KeyCode.End: return Key.End;
            case KeyCode.PageUp: return Key.PageUp;
            case KeyCode.PageDown: return Key.PageDown;
            case KeyCode.LeftShift: return Key.LeftShift;
            case KeyCode.RightShift: return Key.RightShift;
            case KeyCode.LeftControl: return Key.LeftCtrl;
            case KeyCode.RightControl: return Key.RightCtrl;
            case KeyCode.LeftAlt: return Key.LeftAlt;
            case KeyCode.RightAlt: return Key.RightAlt;
            case KeyCode.KeypadPlus: return Key.NumpadPlus;
            case KeyCode.KeypadMinus: return Key.NumpadMinus;
            default: return Key.None;
        }
    }
}
