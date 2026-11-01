using UnityEngine;

namespace Accessibility;

// Hub popups and sections.
public sealed partial class AccessibleKey
{
    #region Hub
    // A popup's text is spoken once when it opens: this reads it again.
    public static readonly AccessibleKey READ_AGAIN = new(KeyCode.R);
    #endregion
}
