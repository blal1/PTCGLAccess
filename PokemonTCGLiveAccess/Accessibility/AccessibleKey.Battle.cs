using UnityEngine;

namespace Accessibility;

// Battle board (AccessibleBattleScreen). Zone keys look at your side; with Shift, the opponent's equivalent.
// The common keys do the rest: arrows (cards / lines), Home/End, numbers, Enter (act / drop), Backspace
// (cancel), Escape (battle menu), Tab (the message bar's Done).
public sealed partial class AccessibleKey
{
    #region Battle zones
    public static readonly AccessibleKey BATTLE_MY_ACTIVE = new(KeyCode.A);
    public static readonly AccessibleKey BATTLE_OPP_ACTIVE = new(KeyCode.A, shift: true);
    public static readonly AccessibleKey BATTLE_MY_BENCH = new(KeyCode.B);
    public static readonly AccessibleKey BATTLE_OPP_BENCH = new(KeyCode.B, shift: true);
    public static readonly AccessibleKey BATTLE_MY_HAND = new(KeyCode.C);
    public static readonly AccessibleKey BATTLE_OPP_HAND = new(KeyCode.C, shift: true);
    public static readonly AccessibleKey BATTLE_MY_DISCARD = new(KeyCode.X);
    public static readonly AccessibleKey BATTLE_OPP_DISCARD = new(KeyCode.X, shift: true);
    public static readonly AccessibleKey BATTLE_MY_DECK = new(KeyCode.D);
    public static readonly AccessibleKey BATTLE_OPP_DECK = new(KeyCode.D, shift: true);
    public static readonly AccessibleKey BATTLE_MY_PRIZES = new(KeyCode.P);
    public static readonly AccessibleKey BATTLE_OPP_PRIZES = new(KeyCode.P, shift: true);
    public static readonly AccessibleKey BATTLE_STADIUM = new(KeyCode.S);
    public static readonly AccessibleKey BATTLE_MY_LOST_ZONE = new(KeyCode.L);
    public static readonly AccessibleKey BATTLE_OPP_LOST_ZONE = new(KeyCode.L, shift: true);
    #endregion

    #region Battle actions
    public static readonly AccessibleKey BATTLE_STATUS = new(KeyCode.T);
    public static readonly AccessibleKey BATTLE_INSPECT = new(KeyCode.I);
    public static readonly AccessibleKey BATTLE_END_TURN = new(KeyCode.E);
    public static readonly AccessibleKey BATTLE_MY_EFFECTS = new(KeyCode.G);
    public static readonly AccessibleKey BATTLE_OPP_EFFECTS = new(KeyCode.G, shift: true);
    public static readonly AccessibleKey BATTLE_LOG = new(KeyCode.H);
    public static readonly AccessibleKey BATTLE_EMOTES = new(KeyCode.M);
    #endregion
}
