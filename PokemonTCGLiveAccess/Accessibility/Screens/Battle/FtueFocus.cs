using TPCI.Rainier.Match.Cards;

namespace Accessibility;

// The tutorial's spotlighted battle card (FTUEMatchManager.focusedCard), or null. Read-only; the FTUE state
// itself (permit, arming indicators, drag coach) lives in FtueTracker (Popups area) — never hook it twice.
internal static class FtueFocus
{
    public static Card3D? Card
    {
        get
        {
            var m = FTUEMatchManager.Instance;
            return m != null ? m.focusedCard : null;
        }
    }
}
