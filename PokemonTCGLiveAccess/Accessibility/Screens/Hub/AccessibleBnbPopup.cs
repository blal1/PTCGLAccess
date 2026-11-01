using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using _Rainier.Features.CardDex.ResearchMissions;
using HarmonyLib;
using SharedLogicUtils.source.DataTypes;
using SharedLogicUtils.source.DataTypes.BattleBox;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using static Accessibility.GameReflect;

namespace Accessibility;

// The hub's text-and-buttons popups: all are title/body/rewards/buttons, so one component reads them instead of one per
// popup.
//   Build & Battle buy-in and event flow (BuildAndBattlePopupManager):
//   • BuildAndBattlePurchaseOverlay       — "CONFIRM EXCHANGE" buy-in (title/body/cost/BACK/EXCHANGE)
//   • BuildAndBattleGenericOverlay        — event closed / no active event / forfeit confirm
//   • BuildAndBattleEventAlmostOverPopup  — the event cutoff warning, with its timer
//   • BuildAndBattleBoxOpeningPopup       — box reveal, images only; its proceed button appears AFTER the animation
//   Build & Battle lobby:
//   • BuildAndBattleProspectiveRewardsPopup / BuildAndBattleMatchHistoryPopup — a map node's rewards
//   • BuildAndBattlePrizePotRewardPopup   — the prize pot, match by match
//   • BuildAndBattleTrainerTipsPopup, BuildAndBattleInfoPopup
//   Research missions (Card Dex):
//   • ResearchMissionsRewardsPopup, CollectorStatsPopup, GeneralMissionCardPickerPopup
//
// Opened by the base OverlayScreenGroup.Activate (none of them overrides it), filtered on the type; closed by the base
// OnDeActivate. We read the texts and rewards, then present every active button; a per-frame button check catches buttons
// that only appear after an animation, and the first announce waits one frame so values set right after Activate (the
// cost) are populated.
internal sealed class AccessibleBnbPopup : AccessibleUI
{
    private static readonly AccessibleBnbPopup s_instance = new();

    private static readonly FieldInfo? s_slotItem = AccessTools.Field(typeof(ProductItemSummarySlot), "currentItem");
    private static readonly FieldInfo? s_slotCount = AccessTools.Field(typeof(ProductItemSummarySlot), "numberOfItems");

    private readonly KeyboardNavigator m_nav;
    private OverlayScreenGroup? m_overlay;
    private bool m_announcePending;
    private string m_lastButtonSig = "";
    private string? m_cost;
    private string? m_boxCardId;

    private AccessibleBnbPopup()
    {
        m_nav = new KeyboardNavigator(this) { OnBack = Cancel };
    }

    private static bool IsHandled(OverlayScreenGroup o) =>
        o is BuildAndBattlePurchaseOverlay or BuildAndBattleGenericOverlay or BuildAndBattleBoxOpeningPopup
            or BuildAndBattleEventAlmostOverPopup or BuildAndBattleProspectiveRewardsPopup or BuildAndBattleMatchHistoryPopup
            or BuildAndBattlePrizePotRewardPopup or BuildAndBattleTrainerTipsPopup or BuildAndBattleInfoPopup
            or ResearchMissionsRewardsPopup or CollectorStatsPopup or GeneralMissionCardPickerPopup;

    [HarmonyPatch(typeof(OverlayScreenGroup), "Activate")]
    private static class ActivateHook
    {
        private static void Postfix(OverlayScreenGroup __instance)
        {
            if (!IsHandled(__instance)) return;
            var reopened = s_instance.m_overlay != null && AccessibilityMgr.IsFocused(s_instance);
            s_instance.m_overlay = __instance;
            s_instance.m_cost = null;
            if (reopened) s_instance.m_announcePending = true;   // another popup of the family replaced this one
            else AccessibilityMgr.ShowUI(s_instance);
        }
    }

    [HarmonyPatch(typeof(OverlayScreenGroup), "OnDeActivate")]
    private static class DeactivateHook
    {
        private static void Postfix(OverlayScreenGroup __instance)
        {
            if (s_instance.m_overlay != __instance) return;
            s_instance.Close();
        }
    }

    // The cost is an icon plus a number: the currency word exists only here.
    [HarmonyPatch(typeof(BuildAndBattlePurchaseOverlay), "SetPurchaseCost")]
    private static class CostHook
    {
        private static void Postfix(object[] __args) =>
            s_instance.m_cost = LocalizationUtils.Format(LocalizationKey.HUB_AMOUNT_FORMAT, __args[1], ShopLabels.CurrencyWord((CurrencyType)__args[0]));
    }

    [HarmonyPatch(typeof(BuildAndBattleBoxOpeningPopup), "Initialize")]
    private static class BoxHook
    {
        private static void Prefix(string defaultCardId) => s_instance.m_boxCardId = defaultCardId;
    }

    public string GetHelp() => LocalizationUtils.Get(LocalizationKey.HUB_POPUP_HELP);

    public void OnGainedFocus()
    {
        Rebuild();
        m_lastButtonSig = ButtonSig();
        m_announcePending = true;
    }

    public void HandleInput()
    {
        if (m_overlay == null || !m_overlay.gameObject.activeInHierarchy)
        {
            Close();
            return;
        }
        // Deferred announce (so the cost — set right after Activate — is ready).
        if (m_announcePending)
        {
            m_announcePending = false;
            Rebuild();
            AnnounceAll();
            return;
        }
        // Buttons can appear after an animation (the box-opening proceed button), so re-scan; when an action first
        // becomes available, re-read the full content + that action.
        var sig = ButtonSig();
        if (sig != m_lastButtonSig)
        {
            var hadNone = m_nav.Count == 0;
            m_lastButtonSig = sig;
            Rebuild();
            if (hadNone && m_nav.Count > 0) AnnounceAll();
            return;
        }
        if (AccessibleKey.READ_AGAIN.IsPressed())
        {
            AccessibilityMgr.Output(this, Content(), interrupt: true);
            return;
        }
        m_nav.HandleInput();
    }

    private void Close()
    {
        m_overlay = null;
        AccessibilityMgr.HideUI(this);
    }

    // Escape, by each popup's real way out; never auto-proceed a buy-in. A side popup of the lobby hides through an
    // animation (the same two calls BuildAndBattleSettingsPopup.ShowForfeitConfirmPopup makes) and is gone only at its
    // end, so the lobby gets the focus back right away rather than waiting for it.
    private void Cancel()
    {
        switch (m_overlay)
        {
            case BuildAndBattleProspectiveRewardsPopup p:
                p.OnLandscapeCloseButton();
                p.AnimateHide();
                Close();
                break;
            case BuildAndBattleMatchHistoryPopup p:
                p.OnLandscapeCloseButton();
                p.AnimateHide();
                Close();
                break;
            case BuildAndBattlePrizePotRewardPopup p:
                p.OnLandscapeCloseButton();
                p.AnimateHide();
                Close();
                break;
            case BuildAndBattleGenericOverlay g: Press(ReadField<Button>(g, "leftCloseButton")); break;
            case BuildAndBattleEventAlmostOverPopup a: Press(ReadField<Button>(a, "leftCloseButton")); break;
            case BuildAndBattlePurchaseOverlay b: Press(ReadField<Button>(b, "cancelButton")); break;
            case BuildAndBattleInfoPopup i:
                if (ReadField<Button>(i, "exitButton") is { } exit && exit.isActiveAndEnabled) exit.onClick.Invoke();
                else i.Deactivate();
                break;
            case BuildAndBattleTrainerTipsPopup or ResearchMissionsRewardsPopup or CollectorStatsPopup or GeneralMissionCardPickerPopup:
                m_overlay.Deactivate();
                break;
        }
    }

    private static void Press(Button? b)
    {
        if (b != null && b.isActiveAndEnabled && b.IsInteractable()) b.onClick.Invoke();
    }

    private void Rebuild()
    {
        m_nav.SetItems(Buttons().Cast<Selectable>().OrderBy(s => HubUi.ReadingOrder(s.transform)), Label);
        // Dialogs vary (side-by-side or stacked).
        m_nav.AllowVertical = true;
        m_nav.AllowHorizontal = true;
    }

    private void AnnounceAll()
    {
        var text = Content();
        AccessibilityMgr.Output(this, string.IsNullOrWhiteSpace(text) ? LocalizationUtils.Get(LocalizationKey.HUB_BNB) : text, interrupt: true);
        m_nav.FocusFirst(announce: true, interruptPrior: false);
    }

    // The reward slots are spoken with the text. Research rewards are the exception: there a slot is also a real action
    // (it opens the card or the booster), so it stays reachable, under the item's name.
    private List<Button> Buttons() =>
        m_overlay == null ? new List<Button>() : m_overlay.GetComponentsInChildren<Button>(includeInactive: false)
            .Where(b => b.isActiveAndEnabled && (m_overlay is ResearchMissionsRewardsPopup || b.GetComponentInParent<ProductItemSummarySlot>() == null))
            .ToList();

    private string ButtonSig() => string.Join(",", Buttons().Select(b => b.gameObject.name));

    private static string Label(Selectable s)
    {
        if (s.GetComponentInParent<ProductItemSummarySlot>() is { } slot && SlotName(slot) is { } name) return name;
        if (s is Button b)
        {
            if (UiLabels.HasHandler(b, "BoosterOddsButtonClicked"))
                return LocalizationUtils.Format(LocalizationKey.HUB_OPENS_BROWSER_FORMAT, KeyboardNavigator.DefaultLabel(s));
            if (!HubUi.HasLetterText(s) && (UiLabels.HasHandler(b, "OnLandscapeCloseButton") || UiLabels.HasHandler(b, "Deactivate") || UiLabels.HasHandler(b, "OnCloseButtonClick")))
                return LocalizationUtils.Get(LocalizationKey.HUB_CLOSE);
        }
        return HubUi.IconButtonLabel(s, LocalizationKey.HUB_INFORMATION);
    }

    private static string? SlotName(ProductItemSummarySlot slot)
    {
        if (s_slotItem?.GetValue(slot) is not ItemData item) return null;
        var n = s_slotCount?.GetValue(slot) is int i && i > 0 ? i : item.amount;
        var name = AccessibleReward.ItemName(item);
        return n > 1 ? LocalizationUtils.Format(LocalizationKey.HUB_TIMES_FORMAT, name, n) : name;
    }

    // ── Content ──

    private string Content()
    {
        var parts = new List<string>();
        switch (m_overlay)
        {
            case null: break;
            case BuildAndBattleProspectiveRewardsPopup p:
                AddText(parts, ReadField<TextMeshProUGUI>(p, "titleText"));
                AddText(parts, ReadField<TextMeshProUGUI>(p, "bodyText"));
                AddRewards(parts, ReadField<TextMeshProUGUI>(p, "victoryRewardText"), p.victoryRewardHolder);
                AddRewards(parts, ReadField<TextMeshProUGUI>(p, "defeatRewardText"), p.defeatRewardHolder);
                break;
            case BuildAndBattleMatchHistoryPopup p:
                Sweep(parts, p.transform);
                AddRewards(parts, null, ReadField<BuildAndBattlePrizePotMatchSlot>(p, "matchSlot")?.transform);
                break;
            case BuildAndBattlePrizePotRewardPopup p:
                var slots = ReadField<Transform>(p, "matchSlotContainer");
                Sweep(parts, p.transform, slots);
                if (slots != null)
                {
                    var n = 0;
                    foreach (var slot in slots.GetComponentsInChildren<BuildAndBattlePrizePotMatchSlot>(includeInactive: false))
                    {
                        n++;
                        var line = new List<string>();
                        Sweep(line, slot.transform);
                        if (AccessibleReward.SummarizeRewards(slot.transform) is { } r) line.Add(r);
                        parts.Add(LocalizationUtils.Format(LocalizationKey.HUB_BNB_MATCH_ROW_FORMAT, n, string.Join(", ", line)));
                    }
                }
                break;
            case BuildAndBattleTrainerTipsPopup p:
                AddText(parts, ReadField<TextMeshProUGUI>(p, "titleText"));
                var tips = ManagerSingleton<BnbDataManager>.instance?.ActiveTrainerTips;
                AddTip(parts, ReadField<TextMeshProUGUI>(p, "tipCaptionText1"), tips, 0);
                AddTip(parts, ReadField<TextMeshProUGUI>(p, "tipCaptionText2"), tips, 1);
                break;
            case BuildAndBattleBoxOpeningPopup p:
                Sweep(parts, p.transform);
                AddBoxContents(parts);
                break;
            case BuildAndBattlePurchaseOverlay p:
                Sweep(parts, p.transform);
                if (m_cost != null) parts.Add(m_cost);
                break;
            case ResearchMissionsRewardsPopup p:
                Sweep(parts, p.transform, p.rewardHolderSingle, p.rewardHolderMultiple);
                if (MissionStateText(p) is { } state) parts.Add(state);
                if (p.rewardHolderSingle != null && p.rewardHolderSingle.gameObject.activeInHierarchy) AddRewards(parts, null, p.rewardHolderSingle);
                if (p.rewardHolderMultiple != null && p.rewardHolderMultiple.gameObject.activeInHierarchy)
                    foreach (var set in p.rewardHolderMultiple.GetComponentsInChildren<ProductItemSummaryNamedSet>(includeInactive: false))
                    {
                        var line = new List<string>();
                        Sweep(line, set.transform);
                        if (AccessibleReward.SummarizeRewards(set.transform) is { } r) line.Add(r);
                        parts.Add(string.Join(", ", line));
                    }
                break;
            default:
                Sweep(parts, m_overlay.transform);
                break;
        }
        return string.Join(". ", parts);
    }

    private static void AddText(List<string> parts, TMP_Text? t)
    {
        if (HubUi.Text(t) is { } s) parts.Add(s);
    }

    private static void AddRewards(List<string> parts, TMP_Text? heading, Transform? holder)
    {
        if (holder == null || AccessibleReward.SummarizeRewards(holder) is not { } rewards) return;
        parts.Add(HubUi.Text(heading) is { } h
            ? h + ": " + rewards
            : LocalizationUtils.Format(LocalizationKey.HUB_REWARD_FORMAT, rewards));
    }

    // The tip's card is only an image on screen.
    private static void AddTip(List<string> parts, TMP_Text? caption, IReadOnlyList<TrainerTipIds>? tips, int i)
    {
        if (HubUi.Text(caption) is not { } tip) return;
        parts.Add(tips != null && i < tips.Count && CardName(tips[i].cardSourceId) is { } card ? card + ": " + tip : tip);
    }

    // The box shows its contents as images only (top card, boosters): named from the same data its Initialize loaded.
    private void AddBoxContents(List<string> parts)
    {
        var mgr = ManagerSingleton<BnbDataManager>.instance;
        if (mgr == null) return;
        var items = new List<string>();
        var top = string.IsNullOrEmpty(m_boxCardId) ? mgr.GetDefaultSeedDeckTopCard() : m_boxCardId;
        if (CardName(top) is { } card) items.Add(card);
        var rewards = AccessibleBnbLobby.RewardNames(mgr.GetStartingRewardItems() ?? new List<ItemData>());
        if (rewards.Length > 0) items.Add(rewards);
        if (items.Count > 0) parts.Add(LocalizationUtils.Format(LocalizationKey.HUB_BNB_BOX_CONTENTS_FORMAT, string.Join(", ", items)));
    }

    private static string? CardName(string? cardId)
    {
        if (string.IsNullOrEmpty(cardId)) return null;
        var row = ManagerSingleton<CardDatabaseManager>.instance?.TryGetCardFromDatabase(cardId);
        return row != null ? CardText.NameWithType(row) : null;
    }

    // The state icon of a single mission's rewards has no text.
    private static string? MissionStateText(ResearchMissionsRewardsPopup p)
    {
        var ids = ReadField<IReadOnlyList<string>>(p, "_missionIds");
        if (ids == null || ids.Count != 1) return null;
        var mgr = ManagerSingleton<ResearchMissionsDataManager>.instance;
        return mgr != null && mgr.missionMap.TryGetValue(ids[0], out var m) && m.progress != null ? AccessibleResearchMissions.StateWord(m.progress.State) : null;
    }

    // Every readable text that isn't a button label or a reward slot's "x1", in reading order. Skipping text under a
    // Selectable keeps button labels out (they're presented as buttons) so they aren't read twice.
    private static void Sweep(List<string> parts, Transform root, params Transform?[] skip)
    {
        foreach (var t in root.GetComponentsInChildren<TMP_Text>(includeInactive: false).OrderBy(t => HubUi.ReadingOrder(t.transform)))
        {
            if (t.GetComponentInParent<Selectable>() != null || t.GetComponentInParent<ProductItemSummarySlot>() != null) continue;
            if (skip.Any(s => s != null && t.transform.IsChildOf(s))) continue;
            var s = HubUi.Text(t);
            if (s != null && s.Any(char.IsLetterOrDigit) && !parts.Contains(s)) parts.Add(s);
        }
    }
}
