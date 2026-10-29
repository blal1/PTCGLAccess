using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using SharedLogicUtils.source.DataTypes;
using TPCI.Localization;
using UnityEngine;
using UnityEngine.UI;
using static Accessibility.GameReflect;

namespace Accessibility;

// BATTLE PASS screen — driver HUBExpansionChallengeScreenController (a HUBScreenController, so the lifecycle hook is
// the standard OnActivate / OnDeactivate, same as HOME / Shop / Profile).
//
// The screen has no sub-tabs; it's a single stacked layout presented as a section stack (Up/Down escapes between
// sections, Left/Right within, Tab cycles — the same model as HOME):
//   1. HUB top tab strip (HOME / DECKS / … / CARD DEX) + overflow "⋮".
//   2. Battle Pass header controls (the "(i)" info button / premium-pass button). The pass STATUS itself — name, time
//      left, level, experience — is pure read-out, so it's spoken as the screen intro, and the info button re-reads it.
//   3. Daily quests (the three quest cards). Each focuses the card's full-panel button and reads
//      "<description>, <progress>, reward …, <state>"; Enter does the card's native action (collect when ready,
//      replace/reroll otherwise — exactly what clicking the card does).
//   4. Rewards: the COLLECT ALL button (when there's more than one reward to claim) plus the tier reward buttons.
//      Each tier reads "Tier <n>, <reward>, <locked / ready to collect / claimed>"; Enter invokes the tier's real
//      reward button (collect or unlock).
//
// Everything is the game's own buttons + KeyboardNavigator; Enter fires each button's wired onClick, so there's no
// parallel "ops" path. All spoken text is read live from the game's LocTexts / battle-pass data.
//
// NOTE: the tier track is a virtualized EnhancedScroller, so only the cells the game currently has spawned (the
// screenful around the player's level) expose reward buttons — the rest of a long pass isn't keyboard-reachable yet
// (same limitation as the shop's "view all" overlay). COLLECT ALL claims every earned reward regardless, which
// covers the common case.
//
// Not ported yet: the previous mod's FTUE coordination (it depends on the FTUE hook, ported with the popups).
internal sealed class AccessibleExpansionChallenge : AccessibleScreen
{
    private static readonly AccessibleExpansionChallenge s_instance = new();

    // ProductItemSummarySlot keeps the quest's item reward in private value-type fields.
    private static readonly FieldInfo? s_slotItem = AccessTools.Field(typeof(ProductItemSummarySlot), "currentItem");
    private static readonly FieldInfo? s_slotCount = AccessTools.Field(typeof(ProductItemSummarySlot), "numberOfItems");

    private readonly List<KeyboardNavigator> m_groups = new();
    private int m_activeGroup = -1;
    private HUBExpansionChallengeScreenController? m_controller;
    private string m_lastContentSig = string.Empty;
    private int m_questsGroupIndex = -1;
    private int m_firstContentGroupIndex = -1;

    [HarmonyPatch(typeof(HUBExpansionChallengeScreenController), "OnActivate")]
    private static class ActivateHook
    {
        private static void Postfix(HUBExpansionChallengeScreenController __instance)
        {
            s_instance.m_controller = __instance;
            s_instance.m_activeGroup = -1;
            AccessibilityMgr.SetScreen(s_instance);
        }
    }

    [HarmonyPatch(typeof(HUBExpansionChallengeScreenController), "OnDeactivate")]
    private static class DeactivateHook
    {
        private static void Postfix(HUBExpansionChallengeScreenController __instance)
        {
            if (s_instance.m_controller != __instance) return;
            s_instance.m_controller = null;
            AccessibilityMgr.ClearScreen(s_instance);
        }
    }

    public string GetHelp() =>
        LocalizationUtils.Format(LocalizationKey.HUB_HELP_WITH_GLOBAL_FORMAT, LocalizationUtils.Get(LocalizationKey.HUB_BATTLE_PASS_HELP), LocalizationUtils.Get(LocalizationKey.HUB_GLOBAL_KEYS_HELP));

    // The pass status, then the focused item (the daily quests on a fresh entry), positions kept otherwise.
    public void OnGainedFocus()
    {
        var prevIndex = m_activeGroup >= 0 && m_activeGroup < m_groups.Count ? m_groups[m_activeGroup].Index : -1;
        RebuildGroups();
        AccessibilityMgr.Output(this, PassStatus(), interrupt: true);
        if (m_groups.Count == 0) return;
        if (m_activeGroup < 0 || m_activeGroup >= m_groups.Count)
        {
            m_activeGroup = PickDefaultGroup();
            m_groups[m_activeGroup].FocusFirst(announce: true, interruptPrior: false);
            return;
        }
        var g = m_groups[m_activeGroup];
        if (prevIndex >= 0 && prevIndex < g.Count) g.FocusIndex(prevIndex, announce: true, interrupt: false);
        else g.RefocusCurrent(announce: true, interrupt: false);
    }

    public void HandleInput()
    {
        if (m_controller == null)
        {
            AccessibilityMgr.ClearScreen(this);
            return;
        }
        // React to a content change (quests refreshed, a reward claimed, a level-up scrolling the tier track) by
        // rebuilding the section list, silently, keeping the place.
        var sig = ContentSig();
        if (sig != m_lastContentSig)
        {
            OnContentChanged();
            return;
        }
        if (m_groups.Count == 0) return;

        var nav = m_groups[m_activeGroup];
        if (AccessibleKey.NEXT_SECTION.IsPressed() || AccessibleKey.PREV_SECTION.IsPressed())
        {
            if (m_groups.Count > 1)
            {
                var dir = AccessibleKey.PREV_SECTION.IsPressed() ? -1 : 1;
                m_activeGroup = (m_activeGroup + dir + m_groups.Count) % m_groups.Count;
                m_groups[m_activeGroup].FocusFirst(announce: true, interruptPrior: true);
            }
            return;
        }
        if (AccessibleKey.READ_PREV_MENU_OPTION.IsPressed() && m_activeGroup > 0 && nav.IsAtTopRow)
        {
            var col = nav.CurrentColumn;
            m_activeGroup--;
            m_groups[m_activeGroup].FocusLastRow(preferredColumn: col, announce: true, interruptPrior: true);
            return;
        }
        if (AccessibleKey.READ_NEXT_MENU_OPTION.IsPressed() && m_activeGroup < m_groups.Count - 1 && nav.IsAtBottomRow)
        {
            var col = nav.CurrentColumn;
            m_activeGroup++;
            m_groups[m_activeGroup].FocusFirstRow(preferredColumn: col, announce: true, interruptPrior: true);
            return;
        }
        nav.HandleInput();
    }

    #region Group discovery

    private void RebuildGroups()
    {
        m_groups.Clear();
        m_questsGroupIndex = -1;
        m_firstContentGroupIndex = -1;
        m_lastContentSig = ContentSig();
        if (m_controller == null) return;

        var known = new HashSet<Selectable>();

        // 1. HUB top tab strip + overflow "⋮".
        var top = HubUi.TopStrip();
        if (top.Count > 0)
        {
            known.UnionWith(top);
            AddGroup(top, UiLabels.TopNavLabel, null);
        }

        // Identify the content pieces first so the header-controls scan can exclude them.
        var questButtons = QuestButtons();
        known.UnionWith(questButtons);
        var collectAll = CollectAllButton();
        var tierButtons = TierButtons();
        known.UnionWith(tierButtons);

        // 2. Header controls (the "(i)" info button, premium-pass button).
        var passItems = HeaderControls(known, collectAll);
        if (passItems.Count > 0)
        {
            AddGroup(passItems, PassLabel, LocalizationUtils.Get(LocalizationKey.UI_BATTLE_PASS));
            if (m_firstContentGroupIndex < 0) m_firstContentGroupIndex = m_groups.Count - 1;
        }

        // 3. Daily quests.
        if (questButtons.Count > 0)
        {
            AddGroup(questButtons, QuestLabel, LocalizationUtils.Get(LocalizationKey.HUB_DAILY_QUESTS));
            m_questsGroupIndex = m_groups.Count - 1;
            if (m_firstContentGroupIndex < 0) m_firstContentGroupIndex = m_groups.Count - 1;
        }

        // 4. Rewards — COLLECT ALL (when shown) then the visible tier reward buttons.
        var rewardItems = new List<Selectable>();
        if (collectAll != null && collectAll.isActiveAndEnabled) rewardItems.Add(collectAll);
        rewardItems.AddRange(tierButtons);
        if (rewardItems.Count > 0)
        {
            AddGroup(rewardItems, TierLabel, LocalizationUtils.Get(LocalizationKey.HUB_REWARDS));
            if (m_firstContentGroupIndex < 0) m_firstContentGroupIndex = m_groups.Count - 1;
        }
    }

    private void AddGroup(List<Selectable> items, KeyboardNavigator.LabelResolver resolver, string? groupName)
    {
        var g = new KeyboardNavigator(this) { GroupName = groupName, OnBack = HubUi.OpenOverflowMenu };
        g.SetItems(items, resolver);
        m_groups.Add(g);
    }

    private int PickDefaultGroup()
    {
        if (m_questsGroupIndex >= 0) return m_questsGroupIndex;
        if (m_firstContentGroupIndex >= 0) return m_firstContentGroupIndex;
        return 0;
    }

    // Signature of the meaningful screen state: each quest's claim/ready state, the unlocked/redeemed tier marks, the
    // visible tier-cell count, and whether COLLECT ALL shows. Changes on a quest refresh / claim, a level-up, or the
    // tier track scrolling — the events that alter the section list.
    private string ContentSig()
    {
        if (m_controller == null) return "none";
        var sd = m_controller.serverData;
        var unlocked = sd != null ? sd.highestUnlockedRank : -1;
        var claimed = sd?.highestRedeemedRank != null ? sd.highestRedeemedRank.GetHighestClaimedRewardsTier() : -1;
        var tiles = m_controller.tiers != null && m_controller.tiers.Scroller != null ? m_controller.tiers.Scroller.ActiveCellViews.Count : 0;
        var collectAll = CollectAllButton() is { } ca && ca.isActiveAndEnabled;
        var quests = string.Concat(QuestPanels().Select(p => !p.gameObject.activeInHierarchy ? "-"
            : Active(ReadField<GameObject>(p, "readyToCollectHolder")) ? "R" : Active(ReadField<GameObject>(p, "comeBackLaterHolder")) ? "C" : "P"));
        return quests + "|" + unlocked + "|" + claimed + "|" + tiles + "|" + collectAll;
    }

    private void OnContentChanged()
    {
        var prevGroup = m_activeGroup;
        var prevIndex = prevGroup >= 0 && prevGroup < m_groups.Count ? m_groups[prevGroup].Index : -1;
        RebuildGroups();
        if (m_groups.Count == 0)
        {
            m_activeGroup = -1;
            return;
        }
        m_activeGroup = Mathf.Clamp(prevGroup < 0 ? PickDefaultGroup() : prevGroup, 0, m_groups.Count - 1);
        var g = m_groups[m_activeGroup];
        if (prevIndex >= 0 && prevIndex < g.Count) g.FocusIndex(prevIndex, announce: false);
        else g.RefocusCurrent(announce: false);
    }

    private List<DailyQuestPanelParts> QuestPanels()
    {
        var q = m_controller != null ? m_controller.quests : null;
        if (q == null) return new List<DailyQuestPanelParts>();
        return new[] { q.questPanelA, q.questPanelB, q.questPanelC }.Where(p => p != null).ToList();
    }

    // Each quest card's full-panel button (its wired onClick = collect when ready / replace otherwise). Kept even when
    // non-interactable (a claimed card) so its state is still readable.
    private List<Selectable> QuestButtons() =>
        QuestPanels().Where(p => p.gameObject.activeInHierarchy)
            .Select(p => ReadField<Button>(p, "_fullPanelButton"))
            .Where(b => b != null && b.isActiveAndEnabled)
            .Cast<Selectable>()
            .ToList();

    private Button? CollectAllButton()
    {
        var tiers = m_controller != null ? m_controller.tiers : null;
        var holder = tiers != null ? ReadField<GameObject>(tiers, "collectAllHolder") : null;
        return holder != null ? holder.GetComponentInChildren<Button>(includeInactive: false) : null;
    }

    // The reward buttons of the tier cells the scroller currently has spawned (the screenful around the player's
    // level), left-to-right.
    private List<Selectable> TierButtons()
    {
        var items = new List<Selectable>();
        var sc = m_controller != null && m_controller.tiers != null ? m_controller.tiers.Scroller : null;
        if (sc == null) return items;
        var cells = sc.ActiveCellViews;
        for (var i = 0; i < cells.Count; i++)
        {
            if (cells.data[i] is ExpansionChallengeTierView view && view.tierRewardController != null)
            {
                var b = view.tierRewardController.RewardButton;
                if (b != null && b.isActiveAndEnabled) items.Add(b);
            }
        }
        return items.OrderBy(s => HubUi.ReadingOrder(s.transform)).ToList();
    }

    // The header region's interactable buttons (info "(i)", premium pass), discovered by exclusion: anything under the
    // controller that isn't the top strip, a quest card, a tier reward, or COLLECT ALL.
    private List<Selectable> HeaderControls(HashSet<Selectable> known, Button? collectAll)
    {
        var scrollerT = m_controller!.tiers != null && m_controller.tiers.Scroller != null ? m_controller.tiers.Scroller.transform : null;
        var questTfs = QuestPanels().Select(p => p.transform).ToList();
        // The level / XP meter (ExpansionChallengeExpController) carries a Button on its group object but isn't an
        // action — its level + experience are already read in the pass status — so leave it out. Identify it
        // structurally as the button wrapping the exp controller's progress bar; that's never the "(i)" info button.
        var expBarT = m_controller.exp != null ? m_controller.exp.progressBar : null;
        return m_controller.GetComponentsInChildren<Button>(includeInactive: false)
            .Where(b => b.isActiveAndEnabled && b.IsInteractable() && !known.Contains(b) && b != collectAll)
            .Where(b => scrollerT == null || !b.transform.IsChildOf(scrollerT))
            .Where(b => !questTfs.Any(t => b.transform.IsChildOf(t)))
            .Where(b => expBarT == null || !expBarT.IsChildOf(b.transform))
            .Cast<Selectable>()
            .OrderBy(s => HubUi.ReadingOrder(s.transform))
            .ToList();
    }

    #endregion

    #region Labels

    // "Battle Pass, <name>, ends in <time>, level <n>, <exp> experience" — all read live.
    private string PassStatus()
    {
        var parts = new List<string> { LocalizationUtils.Get(LocalizationKey.UI_BATTLE_PASS) };
        if (m_controller != null)
        {
            var bp = m_controller.builder != null ? m_controller.builder.LoadedBattlePassData : null;
            if (bp != null && Loc("tcg_" + (bp.ExpansionId ?? "").ToLowerInvariant()) is { } name) parts.Add(name);
            var timeLeft = m_controller.header != null ? m_controller.header.timeLeft : null;
            if (timeLeft != null && HubUi.Text(ReadField<LocText>(timeLeft, "timeLeftText")) is { } t)
                parts.Add(LocalizationUtils.Format(LocalizationKey.HUB_ENDS_IN_FORMAT, t));
            if (m_controller.exp != null)
            {
                if (HubUi.Text(m_controller.exp.rankText) is { } level) parts.Add(LocalizationUtils.Format(LocalizationKey.HUB_LEVEL_FORMAT, level));
                if (HubUi.Text(m_controller.exp.expText) is { } exp) parts.Add(LocalizationUtils.Format(LocalizationKey.HUB_EXPERIENCE_FORMAT, HubUi.Fraction(exp)));
            }
        }
        return string.Join(", ", parts);
    }

    // A text-bearing header button (e.g. the premium "BATTLE PASS" button) keeps its own label; the icon-only "(i)"
    // reads the live pass status instead of a bare "info".
    private string PassLabel(Selectable s) =>
        HubUi.HasLetterText(s) ? KeyboardNavigator.DefaultLabel(s) : LocalizationUtils.Format(LocalizationKey.UI_INFORMATION_FORMAT, PassStatus());

    private static string QuestLabel(Selectable s)
    {
        var panel = s.GetComponentInParent<DailyQuestPanelParts>();
        if (panel == null) return KeyboardNavigator.DefaultLabel(s);
        var parts = new List<string>();
        if (HubUi.Text(ReadField<LocText>(panel, "questDescriptionText")) is { } desc) parts.Add(desc);
        if (HubUi.Text(ReadField<LocText>(panel, "questProgressText")) is { } prog) parts.Add(HubUi.Fraction(prog));
        if (QuestReward(panel) is { Length: > 0 } reward) parts.Add(LocalizationUtils.Format(LocalizationKey.HUB_REWARD_FORMAT, reward));
        if (QuestState(panel) is { } state) parts.Add(state);
        return parts.Count > 0 ? string.Join(", ", parts) : LocalizationUtils.Get(LocalizationKey.HUB_QUEST);
    }

    // The card's two rewards: battle-pass experience (the star) plus the item set (the token/currency).
    private static string QuestReward(DailyQuestPanelParts panel)
    {
        var bits = new List<string>();
        if (HubUi.Text(ReadField<LocText>(panel, "xpRewardAmountText")) is { } xp) bits.Add(LocalizationUtils.Format(LocalizationKey.HUB_EXPERIENCE_FORMAT, xp));
        var slot = ReadField<ProductItemSummarySlot>(panel, "itemSetSummarySlot");
        if (slot != null && s_slotItem?.GetValue(slot) is ItemData item && item.itemType != ItemData.ItemType.None)
        {
            var n = s_slotCount?.GetValue(slot) is int i && i > 0 ? i : (item.amount > 0 ? item.amount : 1);
            bits.Add(Times(ItemName(item), n));
        }
        return bits.Count switch
        {
            0 => "",
            1 => bits[0],
            _ => LocalizationUtils.Format(LocalizationKey.HUB_AND_FORMAT, bits[0], bits[1]),
        };
    }

    private static string? QuestState(DailyQuestPanelParts panel)
    {
        if (Active(ReadField<GameObject>(panel, "comeBackLaterHolder")))
        {
            return HubUi.Text(ReadField<LocText>(panel, "comeBackLaterTimerLocText")) is { } t
                ? LocalizationUtils.Format(LocalizationKey.HUB_CLAIMED_AVAILABLE_IN_FORMAT, t)
                : LocalizationUtils.Get(LocalizationKey.HUB_CLAIMED);
        }
        if (Active(ReadField<GameObject>(panel, "readyToCollectHolder"))) return LocalizationUtils.Get(LocalizationKey.HUB_READY_TO_COLLECT);
        if (Active(ReadField<GameObject>(panel, "canRerollIndicator"))) return LocalizationUtils.Get(LocalizationKey.HUB_CAN_BE_REPLACED);
        return null;
    }

    private string TierLabel(Selectable s)
    {
        if (s == CollectAllButton()) return LocalizationUtils.Get(LocalizationKey.HUB_COLLECT_ALL);
        var view = s.GetComponentInParent<ExpansionChallengeTierView>();
        if (view == null) return KeyboardNavigator.DefaultLabel(s);

        var parts = new List<string>();
        var num = HubUi.Text(view.indexTxt_Unlocked) ?? HubUi.Text(view.indexTxt_Locked);
        parts.Add(num == null ? LocalizationUtils.Get(LocalizationKey.HUB_TIER) : LocalizationUtils.Format(LocalizationKey.HUB_TIER_FORMAT, num));
        var rewards = m_controller != null && m_controller.tiers != null ? DescribeRewards(m_controller.tiers.GetRewards(view.rank, view.rank)) : "";
        if (rewards.Length > 0) parts.Add(rewards);
        if (TierState(view.rank) is { } state) parts.Add(state);
        return string.Join(", ", parts);
    }

    private string? TierState(int rank)
    {
        var sd = m_controller != null ? m_controller.serverData : null;
        if (sd == null) return null;
        if (sd.highestRedeemedRank != null && sd.highestRedeemedRank.GetHighestClaimedRewardsTier() >= rank) return LocalizationUtils.Get(LocalizationKey.HUB_CLAIMED);
        if (sd.highestUnlockedRank >= rank) return LocalizationUtils.Get(LocalizationKey.HUB_READY_TO_COLLECT);
        return LocalizationUtils.Get(LocalizationKey.HUB_LOCKED);
    }

    #endregion

    #region Reward / item description

    private static string DescribeRewards(ItemData[]? items)
    {
        if (items == null || items.Length == 0) return "";
        int hard = 0, trade = 0, token = 0;
        var others = new List<string>();
        foreach (var item in items)
        {
            switch (item.itemType)
            {
                case ItemData.ItemType.HardCurrency: hard += item.amount; break;
                case ItemData.ItemType.TraderCurrency: trade += item.amount; break;
                case ItemData.ItemType.TokenCurrency: token += item.amount; break;
                default: others.Add(Times(ItemName(item), item.amount)); break;
            }
        }
        var parts = new List<string>();
        if (hard > 0) parts.Add(Amount(hard, CurrencyType.HardCurrency));
        if (trade > 0) parts.Add(Amount(trade, CurrencyType.TradeCurrency));
        if (token > 0) parts.Add(Amount(token, CurrencyType.TokenCurrency));
        parts.AddRange(others);
        return string.Join(", ", parts);
    }

    private static string Amount(int amount, CurrencyType type) => LocalizationUtils.Format(LocalizationKey.HUB_AMOUNT_FORMAT, amount, ShopLabels.CurrencyWord(type));

    private static string Times(string name, int n) => n > 1 ? LocalizationUtils.Format(LocalizationKey.HUB_TIMES_FORMAT, name, n) : name;

    private static string ItemName(ItemData item)
    {
        if (item.itemType == ItemData.ItemType.Card && !string.IsNullOrEmpty(item.clientId)
            && ManagerSingleton<CardDatabaseManager>.instance?.TryGetCardFromDatabase(item.clientId) is { } row)
            return CardText.NameWithType(row);
        switch (item.itemType)
        {
            case ItemData.ItemType.HardCurrency: return ShopLabels.CurrencyWord(CurrencyType.HardCurrency);
            case ItemData.ItemType.TraderCurrency: return ShopLabels.CurrencyWord(CurrencyType.TradeCurrency);
            case ItemData.ItemType.TokenCurrency: return ShopLabels.CurrencyWord(CurrencyType.TokenCurrency);
            case ItemData.ItemType.SoftCurrency: return ShopLabels.CurrencyWord(CurrencyType.SoftCurrency);
        }
        if (Loc(item.contentLocId) is { } loc) return loc;
        return LocalizationUtils.Get(item.itemType switch
        {
            ItemData.ItemType.Booster => LocalizationKey.HUB_ITEM_BOOSTER,
            ItemData.ItemType.Coin => LocalizationKey.HUB_ITEM_COIN,
            ItemData.ItemType.Sleeve => LocalizationKey.HUB_ITEM_SLEEVE,
            ItemData.ItemType.DeckBox => LocalizationKey.HUB_ITEM_DECK_BOX,
            ItemData.ItemType.Deck => LocalizationKey.HUB_ITEM_DECK,
            ItemData.ItemType.Avatar => LocalizationKey.HUB_ITEM_AVATAR,
            _ => LocalizationKey.HUB_ITEM,
        });
    }

    #endregion

    private static bool Active(GameObject? go) => go != null && go.activeInHierarchy;

    private static string? Loc(string? id)
    {
        var s = LocalizationUtils.TryGetGame(id);
        return s == null ? null : ShopLabels.StripTags(CardText.SpeakIcons(s)).Trim();
    }
}
