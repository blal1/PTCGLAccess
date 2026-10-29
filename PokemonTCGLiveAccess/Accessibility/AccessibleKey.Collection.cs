using UnityEngine;

namespace Accessibility;

// Deck editor, card details and the exchange popup.
public sealed partial class AccessibleKey
{
    #region Collection
    // Deck editor: open the game's card-detail overlay for the focused card.
    public static readonly AccessibleKey INSPECT_CARD = new(KeyCode.I);
    // Card details / exchange popup: one copy more or less. Any modifiers: "+" is Shift+= on most layouts.
    public static readonly AccessibleKey ADD_COPY = new(KeyCode.Equals, anyModifiers: true);
    public static readonly AccessibleKey ADD_COPY_PLUS = new(KeyCode.Plus, anyModifiers: true);
    public static readonly AccessibleKey ADD_COPY_NUMPAD = new(KeyCode.KeypadPlus, anyModifiers: true);
    public static readonly AccessibleKey REMOVE_COPY = new(KeyCode.Minus);
    public static readonly AccessibleKey REMOVE_COPY_NUMPAD = new(KeyCode.KeypadMinus, anyModifiers: true);
    #endregion
}
