using System;
using System.Collections.Generic;
using TPCI.Rainier.Match.Cards;

namespace Accessibility;

// Read-only look at the board while a decision prompt waits. The sighted player has the game's "inspect
// board" button (BoardInspectionMenu) on selection drawers; this gives the same information without opening
// that menu, whose OnLeave hides the drawer behind it. Nothing here touches the game.
internal sealed class AccessibleBoardReview : AccessibleUI
{
    private static readonly AccessibleBoardReview s_instance = new();

    private Func<bool> m_stillWanted = () => false;
    private BattleBoardReader.Zone m_zone;
    private List<Card3D>? m_cards;
    private int m_card;
    private int m_line = -1;

    internal static void Open(BattleBoardReader.Zone zone, Func<bool> stillWanted)
    {
        s_instance.m_stillWanted = stillWanted;
        s_instance.m_zone = zone;
        AccessibilityMgr.ShowUI(s_instance);
    }

    public string GetHelp() => LocalizationUtils.Get(LocalizationKey.BATTLE_REVIEW_HELP);

    public void OnGainedFocus() => ShowZone(m_zone);

    public void HandleInput()
    {
        if (!m_stillWanted()) { AccessibilityMgr.HideUI(this); return; }
        if (AccessibleKey.BACK.IsPressed() || AccessibleKey.ESCAPE.IsPressed()) { AccessibilityMgr.HideUI(this); return; }
        if (BattleBoardReader.PressedZone() is { } z) { ShowZone(z); return; }
        if (MatchAnnouncements.TryHandleInfoKey()) return;
        if (AccessibleKey.READ_NEXT_ITEM.IsPressed()) { MoveCard(m_card + 1); return; }
        if (AccessibleKey.READ_PREV_ITEM.IsPressed()) { MoveCard(m_card - 1); return; }
        if (AccessibleKey.READ_FIRST_ITEM.IsPressed()) { MoveCard(0); return; }
        if (AccessibleKey.READ_LAST_ITEM.IsPressed()) { MoveCard((m_cards?.Count ?? 1) - 1); return; }
        if (AccessibleKey.READ_NEXT_LINE.IsPressed()) { ReadLine(1); return; }
        if (AccessibleKey.READ_PREV_LINE.IsPressed()) { ReadLine(-1); return; }
        if (AccessibleKey.TryGetPressedNumber() is { } n) MoveCard(n - 1);
    }

    private void ShowZone(BattleBoardReader.Zone zone)
    {
        var mgr = AccessibleBattleScreen.CurrentManager;
        if (mgr == null) { AccessibilityMgr.HideUI(this); return; }
        m_zone = zone;
        m_card = 0;
        m_line = -1;
        var name = BattleBoardReader.ZoneName(zone);
        if (BattleBoardReader.IsCountOnly(zone))
        {
            m_cards = null;
            int n = BattleBoardReader.GetCount(zone);
            Say(n == 1 ? LocalizationUtils.Format(LocalizationKey.BATTLE_ZONE_ONE_CARD, name) : LocalizationUtils.Format(LocalizationKey.BATTLE_ZONE_CARDS, name, n));
            return;
        }
        m_cards = BattleBoardReader.GetCards(mgr, zone);
        Say(m_cards.Count == 0
            ? LocalizationUtils.Format(LocalizationKey.BATTLE_ZONE_EMPTY, name)
            : LocalizationUtils.Format(LocalizationKey.BATTLE_ZONE_AND_CARD, name, Label(0)));
    }

    private string Label(int i) => Announce.Item(BattleBoardReader.CardNameWithType(m_cards![i]), null, i + 1, m_cards.Count);

    private void MoveCard(int index)
    {
        if (m_cards == null || index < 0 || index >= m_cards.Count) return;
        m_card = index;
        m_line = -1;
        Say(Label(index));
    }

    private void ReadLine(int dir)
    {
        if (m_cards == null || m_card >= m_cards.Count) return;
        var lines = BattleBoardReader.DetailLines(m_cards[m_card]);
        m_line = Math.Max(-1, Math.Min(m_line + dir, lines.Count - 1));
        Say(m_line < 0 ? Label(m_card) : lines[m_line]);
    }

    private void Say(string text) => AccessibilityMgr.Output(this, text);
}
