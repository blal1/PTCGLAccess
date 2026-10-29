using UnityEngine;

namespace Accessibility;

// Shortcuts that work on any screen showing what they read (Accessibility/Global/). The HUB tab shortcuts use
// the common number keys (TryGetPressedNumber).
public sealed partial class AccessibleKey
{
    #region Global shortcuts
    public static readonly AccessibleKey READ_CURRENCIES = new(KeyCode.P);
    public static readonly AccessibleKey READ_PLAYER_NAME = new(KeyCode.N);
    #endregion
}
