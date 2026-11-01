using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using _Rainier.Features.CardDex.ResearchMissions;
using HarmonyLib;
using TMPro;
using UnityEngine.UI;
using static Accessibility.GameReflect;

namespace Accessibility;

// A research mission's details (MissionDetailsPopup): title, description, progress, the buttons, and its objective cards.
// The cards come from the popup's list rather than the virtualized scroller's cells; Enter does what a tile click does
// (OpenPurchaseCardOverlay: private, and the tile's only path to the card detail overlay).
internal sealed class AccessibleResearchMissionDetails : AccessibleUI
{
    private static readonly AccessibleResearchMissionDetails s_instance = new();
    private static readonly MethodInfo? s_openCard = AccessTools.Method(typeof(MissionDetailsPopup), "OpenPurchaseCardOverlay");

    private readonly KeyboardNavigator m_nav;
    private readonly DataListSection m_cards;
    private readonly SectionStack m_stack = new();
    private MissionDetailsPopup? m_popup;

    private AccessibleResearchMissionDetails()
    {
        m_nav = new KeyboardNavigator(this) { AllowTab = false };
        m_cards = new DataListSection(this, LocalizationKey.HUB_RM_CARDS, () => Cards().Count, CardLabel, OpenCard);
        m_stack.OnBack = () => m_popup?.Deactivate();
    }

    [HarmonyPatch(typeof(OverlayScreenGroup), "Activate")]
    private static class ActivateHook
    {
        private static void Postfix(OverlayScreenGroup __instance)
        {
            if (__instance is not MissionDetailsPopup popup) return;
            s_instance.m_popup = popup;
            AccessibilityMgr.ShowUI(s_instance);
        }
    }

    [HarmonyPatch(typeof(OverlayScreenGroup), "OnDeActivate")]
    private static class DeactivateHook
    {
        private static void Postfix(OverlayScreenGroup __instance)
        {
            if (s_instance.m_popup != __instance) return;
            s_instance.m_popup = null;
            AccessibilityMgr.HideUI(s_instance);
        }
    }

    public string GetHelp() => LocalizationUtils.Get(LocalizationKey.HUB_RM_DETAILS_HELP);

    public void OnGainedFocus()
    {
        if (m_popup == null) return;
        var window = ReadField<UnityEngine.Component>(m_popup, "contentWindow");
        m_nav.SetItems(m_popup.GetComponentsInChildren<Button>(includeInactive: false)
            .Where(b => b.isActiveAndEnabled && (window == null || !b.transform.IsChildOf(window.transform)))
            .Cast<Selectable>().OrderBy(s => HubUi.ReadingOrder(s.transform)), s => HubUi.IconButtonLabel(s, LocalizationKey.HUB_INFORMATION));
        m_nav.ForceFlatVertical();
        m_stack.Set(new INavSection[] { m_cards, m_nav }, m_stack.Current);
        AccessibilityMgr.Output(this, Content(), interrupt: true);
        m_stack.Current?.RefocusCurrent(announce: true, interrupt: false);
    }

    public void HandleInput()
    {
        if (m_popup == null || !m_popup.gameObject.activeInHierarchy)
        {
            m_popup = null;
            AccessibilityMgr.HideUI(this);
            return;
        }
        if (AccessibleKey.READ_AGAIN.IsPressed())
        {
            AccessibilityMgr.Output(this, Content(), interrupt: true);
            return;
        }
        m_stack.HandleInput();
    }

    private string Content()
    {
        var parts = new List<string>();
        foreach (var field in new[] { "missionTitleTextField", "missionDescriptionTextField", "progressAmountTextField" })
            if (HubUi.Text(ReadField<TMP_Text>(m_popup, field)) is { } s) parts.Add(s);
        return string.Join(". ", parts);
    }

    private List<ArchetypeDBCards> Cards() => ReadField<List<ArchetypeDBCards>>(m_popup, "_curCollection") ?? new List<ArchetypeDBCards>();

    private string CardLabel(int i)
    {
        var cards = Cards();
        return i < cards.Count ? CardDexText.CardStackLabel(cards[i]) : LocalizationUtils.Get(LocalizationKey.HUB_EMPTY);
    }

    private void OpenCard(int i)
    {
        var cards = Cards();
        if (i < cards.Count) s_openCard?.Invoke(m_popup, new object?[] { cards[i], null });
    }
}
