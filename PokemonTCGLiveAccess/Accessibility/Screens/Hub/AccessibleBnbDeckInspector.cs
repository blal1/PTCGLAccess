using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using SharedSDKUtils;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using static Accessibility.GameReflect;

namespace Accessibility;

// The starter deck, shown after the box opening (BuildAndBattleDeckInspectorController). Sighted players read it from
// card stubs and a big card image, so the deck is navigated from its data: Up/Down through the cards, Enter reads the
// focused card in full. The buttons (trainer tips, open the packs) are the other section. There's no way back here: the
// only exit is proceeding, so Escape does nothing.
internal sealed class AccessibleBnbDeckInspector : AccessibleUI
{
    private static readonly AccessibleBnbDeckInspector s_instance = new();

    private readonly KeyboardNavigator m_nav;
    private readonly DataListSection m_cards;
    private readonly SectionStack m_stack = new();
    private readonly List<Row> m_rows = new();
    private BuildAndBattleDeckInspectorController? m_overlay;

    private sealed class Row
    {
        public string Id = "";
        public int Count;
        public string Name = "";
        public System.Collections.Generic.IEnumerable<string>? Details;
    }

    private AccessibleBnbDeckInspector()
    {
        m_nav = new KeyboardNavigator(this);
        m_cards = new DataListSection(this, null, () => m_rows.Count, RowLabel, ReadCard);
    }

    [HarmonyPatch(typeof(OverlayScreenGroup), "Activate")]
    private static class ActivateHook
    {
        private static void Postfix(OverlayScreenGroup __instance)
        {
            if (__instance is not BuildAndBattleDeckInspectorController overlay) return;
            s_instance.m_overlay = overlay;
            s_instance.Build();
            AccessibilityMgr.ShowUI(s_instance);
        }
    }

    [HarmonyPatch(typeof(OverlayScreenGroup), "OnDeActivate")]
    private static class DeactivateHook
    {
        private static void Postfix(OverlayScreenGroup __instance)
        {
            if (s_instance.m_overlay != __instance) return;
            s_instance.m_overlay = null;
            AccessibilityMgr.HideUI(s_instance);
        }
    }

    public string GetHelp() => LocalizationUtils.Get(LocalizationKey.HUB_BNB_INSPECTOR_HELP);

    public void OnGainedFocus()
    {
        if (m_overlay == null) return;
        var parts = new List<string>();
        if (HubUi.Text(ReadField<TextMeshProUGUI>(m_overlay, "deckNameText")) is { } name) parts.Add(name);
        parts.Add(LocalizationUtils.Format(LocalizationKey.HUB_BNB_DECK_COUNT_FORMAT, m_rows.Sum(r => r.Count)));
        AccessibilityMgr.Output(this, string.Join(", ", parts), interrupt: true);
        m_stack.Current?.RefocusCurrent(announce: true, interrupt: false);
    }

    public void HandleInput()
    {
        if (m_overlay == null || !m_overlay.gameObject.activeInHierarchy)
        {
            m_overlay = null;
            AccessibilityMgr.HideUI(this);
            return;
        }
        m_stack.HandleInput();
    }

    private void Build()
    {
        m_rows.Clear();
        var deck = ReadField<DeckInfo>(m_overlay, "_seedDeck");
        var db = ManagerSingleton<CardDatabaseManager>.instance;
        if (deck?.cards != null)
            foreach (var pair in deck.cards)
            {
                var row = db != null ? db.TryGetCardFromDatabase(pair.Key) : null;
                m_rows.Add(new Row
                {
                    Id = pair.Key,
                    Count = pair.Value,
                    Name = row != null ? CardText.NameWithType(row) : pair.Key,
                    Details = row != null ? CardText.DetailLines(row) : null,
                });
            }
        m_rows.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));

        // The panel holds the card stubs, already covered by the list above.
        var panel = ReadField<Component>(m_overlay, "deckInspectorPanel");
        m_nav.SetItems(m_overlay!.GetComponentsInChildren<Button>(includeInactive: false)
            .Where(b => b.isActiveAndEnabled && (panel == null || !b.transform.IsChildOf(panel.transform)))
            .Cast<Selectable>().OrderBy(s => HubUi.ReadingOrder(s.transform)));
        m_nav.ForceFlatVertical();
        m_stack.Set(new INavSection[] { m_cards, m_nav }, m_cards);
        m_cards.SetIndex(0);
    }

    private string RowLabel(int i) => LocalizationUtils.Format(LocalizationKey.HUB_TIMES_FORMAT, m_rows[i].Name, m_rows[i].Count);

    private void ReadCard(int i)
    {
        if (m_rows[i].Details is { } details) AccessibilityMgr.Output(this, string.Join(". ", details), interrupt: true);
    }
}
