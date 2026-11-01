using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RainierClientSDK.source.Commerce;
using SharedLogicUtils.source.DataTypes;
using TMPro;
using TPCI.Localization;
using UnityEngine;
using UnityEngine.UI;
using static Accessibility.GameReflect;

namespace Accessibility;

// Reward overlays — the season-start "NEW LADDER" popup, plus league / tier / level-up / Build-and-
// Battle reward overlays. Driver: RewardOverlaysController activates a generic ContentItemRewardOverlay
// (an OverlayScreenGroup: title `titleTxt`, reward items in `rewardHolder`, a call-to-action button
// whose text is `buttonTxt`, e.g. "COLLECT REWARDS") plus a per-type custom view. COLLECT REWARDS
// routes through RewardOverlaysController.CloseScreen(true) → activeCustomView.OnCallToAction.
//
// These pop OVER the HUB without pausing it (they're overlays, not screen transitions), so without
// this the underlying screen (Home) keeps keyboard focus *behind* the modal and the button is
// unreachable — observed right after the FTUE as a season-start ladder reward you couldn't collect.
//
// We hook ActivateCustomView (present) / CloseScreen (dismiss), read the title + rewards, and
// present the overlay's real buttons (COLLECT REWARDS, close, booster odds) for Enter.
internal sealed class AccessibleReward : AccessibleUI
{
    private static readonly AccessibleReward s_instance = new();
    private static readonly FieldInfo? s_slotItem = AccessTools.Field(typeof(ProductItemSummarySlot), "currentItem");
    private static readonly FieldInfo? s_slotCount = AccessTools.Field(typeof(ProductItemSummarySlot), "numberOfItems");

    private readonly KeyboardNavigator m_nav;
    private RewardOverlaysController? m_controller;
    private Selectable? m_lastFtueTarget;
    private bool m_speakTitle = true;

    // True while this overlay is the one being presented here (see AccessibleGenericOverlay).
    internal static bool IsPresenting(OverlayScreenGroup overlay) => s_instance.m_controller != null && s_instance.m_controller.activeOverlay == overlay;

    private AccessibleReward()
    {
        m_nav = new KeyboardNavigator(this) { AllowTab = false };
        // The tutorial coaches this overlay via FTUERewardsPopupIndicator → RewardsPopupButtonController,
        // whose Focus() starts a coroutine that waits for OverlayManager.currentOverlay to BE the reward
        // overlay before it highlights COLLECT REWARDS. So the focus always lands after we've opened and
        // announced (measured: 180 ms later) — checking for it on open would always miss it.
        // (The singleton is built at startup: Compat.ScanStatics runs every static constructor.)
        FtueTracker.FocusedElementChanged += OnFtueFocusChanged;
    }

    public void HandleInput()
    {
        // Backstop: let go so the HUB underneath regains focus once the reward overlay is no longer
        // showing. CRITICAL: a HUB overlay closed via OverlayManager (OnDeActivate) only disables its
        // CANVAS — the GameObject stays activeInHierarchy — so that flag alone is unreliable (a nested
        // reward, e.g. claiming on Casual → an item-grant reveal pops back to here, left us STUCK).
        // The authoritative signal is the overlay's own navigation: lobby popups (B&B rewards) use the
        // lobby's, not OverlayManager, so asking OverlayManager let go of them at once.
        var ov = m_controller != null ? m_controller.activeOverlay : null;
        if (ov == null || !ov.gameObject.activeInHierarchy || !PopupUtils.IsOverlayOpen(ov)
            // SECOND stranding mode (post-match reward freeze, from a real log): collecting a reward opens a
            // nested item-grant reveal OVER this overlay; when that reveal finishes the controller tears down
            // THIS overlay's reward content (its COLLECT button + slots) but does NOT fire CloseScreen or
            // deactivate the GameObject — so we resume onto an overlay that's still active + the current
            // overlay yet has nothing left to read or press. A live reward overlay always rebuilt at least one
            // button (COLLECT / close); zero after a rebuild means it's spent — let go so Home resumes.
            || m_nav.Count == 0)
        {
            Close();
            return;
        }
        m_nav.HandleInput();
    }

    public string GetHelp() => LocalizationUtils.Get(LocalizationKey.POPUP_BUTTONS_HELP);

    public void OnGainedFocus()
    {
        if (m_controller == null) return;
        var ftue = FtueTarget();
        m_lastFtueTarget = ftue;
        var items = ftue != null ? new List<Selectable> { ftue } : CollectButtons();
        m_nav.SetItems(items, RewardButtonLabel);
        m_nav.AllowHorizontal = ftue == null;   // reward buttons sit in a row; a coached one is alone

        var title = m_speakTitle ? OverlayTitle() : null;
        m_speakTitle = true;
        if (title != null) AccessibilityMgr.Output(this, title, interrupt: true);
        if (items.Count > 0) m_nav.FocusFirst(announce: true, interruptPrior: title == null);
        else AccessibilityMgr.Output(this, LocalizationUtils.Get(LocalizationKey.POPUP_REWARDS), interrupt: title == null);
    }

    private void Close()
    {
        m_controller = null;
        AccessibilityMgr.HideUI(this);
    }

    // The one button the tutorial wants pressed on THIS overlay, or null when it isn't coaching us.
    //
    // MenuButtonController.Focus REPARENTS the coached Selectable under an input blocker and leaves every
    // other button behind it — unclickable. Two consequences: CollectButtons() can no longer even find
    // COLLECT REWARDS (it's no longer a child of the controller or the overlay), and focusing anything
    // else hands the player a button the game will ignore. So while we're coached, we present exactly the
    // coached button and nothing else.
    //
    // isOverlay is what tells us the focus is OURS. When this overlay opens, the FTUE is still pointing at
    // Btn_TierReward on the Battle Pass screen behind us (reparented under the MAIN blocker); following
    // that blindly would focus a button on another screen. The overlay's coached button goes under the
    // OVERLAY blocker instead, and FocusedElement.isOverlay records which.
    private static Selectable? FtueTarget()
    {
        if (!FtueTracker.FtueIsRunning || !FtueTracker.FocusedElementIsOverlay) return null;
        var target = FtueTracker.FocusedElement;
        return target != null && target.isActiveAndEnabled ? target : null;
    }

    private void OnFtueFocusChanged()
    {
        if (m_controller == null || !AccessibilityMgr.IsFocused(this)) return;
        var target = FtueTarget();
        if (ReferenceEquals(target, m_lastFtueTarget)) return;
        // Focus released — the coached button was pressed and the flow is moving on (an item-grant reveal
        // opens over us). Restore the full button set silently rather than talking over what comes next.
        if (target == null)
        {
            m_lastFtueTarget = null;
            m_nav.SetItems(CollectButtons(), RewardButtonLabel);
            m_nav.AllowHorizontal = true;
            return;
        }
        // The title + reward contents were spoken when the overlay opened; only the new focus is news.
        m_speakTitle = false;
        OnGainedFocus();
    }

    // "<header>, <rewards>, <status>". The header + status come from the active custom view when it's
    // a single-tier reward detail (its tierTxt "TIER 1" + bottom message "already collected"); other
    // reward overlays (generic COLLECT, league, level-up, …) fall back to the generic overlay's title.
    // The rewards themselves are read here by name instead of being navigated as bare "x1" slots.
    private string? OverlayTitle()
    {
        var parts = new List<string>();
        var tier = m_controller?.activeCustomView as SingleTierCustomView;
        if (tier != null)
        {
            AddTier(parts, tier.tierTxt);
        }
        else if (m_controller?.activeOverlay is ContentItemRewardOverlay generic)
        {
            if (PopupUtils.Text(generic.titleTxt) is { } title) parts.Add(title);
            if (PopupUtils.Text(generic.contentCountTxt) is { } count) parts.Add(count);
        }
        AddPopupHeader(parts);
        AddViewDetails(parts);

        if ((m_controller?.activeOverlay as ContentItemRewardOverlay)?.rewardHolder is { } holder && SummarizeRewards(holder) is { } rewards)
        {
            // Frame a bare item list. These tiles ARE visible to a sighted player before collecting,
            // but without a leading word the list read as if the mod were leaking the pack's hidden
            // contents (reported 2026-07-23 — the loose reward cards were mistaken for pack cards).
            if (parts.Count == 0) parts.Add(LocalizationUtils.Get(LocalizationKey.POPUP_REWARDS));
            parts.Add(rewards);
        }

        if (tier != null && PopupUtils.Text(tier.bottomMessageTxt) is { } msg) parts.Add(msg);
        AddViewFooter(parts);
        return parts.Count > 0 ? string.Join(", ", parts) : null;
    }

    private static void AddTier(List<string> parts, Component? tierText)
    {
        if (PopupUtils.Text(tierText) is { } t) parts.Add(t.Any(char.IsLetter) ? t : LocalizationUtils.Format(LocalizationKey.POPUP_TIER_FORMAT, t));
    }

    // Build & Battle popups carry their own header texts on the controller's object.
    private void AddPopupHeader(List<string> parts)
    {
        if (m_controller == null) return;
        if (m_controller.GetComponent<BuildAndBattleRewardCollectPopup>() is { } collect)
            foreach (var field in new[] { "headerText", "notificationText", "bodyText", "modeText" })
                AddText(parts, ReadField<TextMeshProUGUI>(collect, field));
        else if (m_controller.GetComponent<BuildAndBattleForfeitRewardsPopup>() is { } forfeit)
            AddText(parts, ReadField<TextMeshProUGUI>(forfeit, "bnbTitleText"));
    }

    private static void AddText(List<string> parts, Component? text)
    {
        if (PopupUtils.Text(text) is { } s && !parts.Contains(s)) parts.Add(s);
    }

    // What each custom view shows before the reward list.
    private void AddViewDetails(List<string> parts)
    {
        switch (m_controller?.activeCustomView)
        {
            case LevelUpCustomView level:
                var hexText = ReadField<LocText>(ReadField<LevelHex>(level, "levelHex"), "levelDisplayText");
                if (PopupUtils.Text(hexText) is { } n) parts.Add(n.Any(char.IsLetter) ? n : LocalizationUtils.Format(LocalizationKey.POPUP_LEVEL_FORMAT, n));
                break;
            case LeagueRewardsCustomView league:
                AddText(parts, ReadField<LocText>(league, "leagueNameText"));
                break;
            case SeasonRewardsCustomView season when season.matchChecks is { Length: > 0 } checks:
                parts.Add(LocalizationUtils.Format(LocalizationKey.POPUP_SEASON_MATCHES_FORMAT, checks.Count(c => c != null && c.activeSelf), checks.Length));
                break;
            case MultiTierCustomView multi:
                AddText(parts, multi.tierRangeTxt);
                break;
            case UnlockTierCustomView unlock:
                AddTier(parts, unlock.tierTxt);
                AddText(parts, unlock.bottomTierCountTxt);
                break;
            case ExpansionChallengeRewardsCustomView expansion:
                // The view only shows a logo image. Same key as the Battle Pass screen.
                var id = ReadField<RewardOverlaysController.ExpansionChallengeRewardsOptions>(expansion, "options")?.expansionId;
                if (LocalizationUtils.TryGetGame("tcg_" + (id ?? "").ToLowerInvariant()) is { } name) parts.Add(name);
                break;
        }
    }

    // ... and after it: status, price, balance.
    private void AddViewFooter(List<string> parts)
    {
        switch (m_controller?.activeCustomView)
        {
            case LeagueRewardsCustomView league:
                AddText(parts, ReadField<LocText>(league, "statusText"));
                break;
            case UnlockTierCustomView unlock:
                if (PopupUtils.Text(unlock.bottomCostTxt) is { } cost) parts.Add(LocalizationUtils.Format(LocalizationKey.POPUP_UNLOCK_COST_FORMAT, cost));
                if (PopupUtils.Text(unlock.currencyTxt) is { } balance) parts.Add(LocalizationUtils.Format(LocalizationKey.POPUP_UNLOCK_BALANCE_FORMAT, balance));
                break;
        }
    }

    // Read a reward holder's spawned ProductItemSummarySlots by name + count. Shared with the season
    // ladder overlay (AccessibleSeasonReset), which builds the same slots from its own rewardHolder.
    internal static string? SummarizeRewards(Transform? rewardHolder)
    {
        if (rewardHolder == null) return null;
        var parts = new List<string>();
        foreach (var slot in rewardHolder.GetComponentsInChildren<ProductItemSummarySlot>(includeInactive: false))
        {
            var item = s_slotItem?.GetValue(slot) is ItemData d ? d : default;
            if (item.itemType == ItemData.ItemType.None) continue;
            var n = s_slotCount?.GetValue(slot) is int i && i > 0 ? i : (item.amount > 0 ? item.amount : 1);
            var name = ItemName(item);
            if (!string.IsNullOrWhiteSpace(name)) parts.Add(n > 1 ? LocalizationUtils.Format(LocalizationKey.POPUP_ITEM_COUNT_FORMAT, name, n) : name);
        }
        return parts.Count > 0 ? string.Join(", ", parts) : null;
    }

    // The overlay's actionable buttons (COLLECT REWARDS / UNLOCK, DROP RATES, close). The reward
    // slots themselves are excluded — their contents are spoken via SummarizeRewards instead of being
    // navigated as bare "x1" buttons. Ordered top-to-bottom, left-to-right.
    private List<Selectable> CollectButtons()
    {
        var result = new List<Selectable>();
        if (m_controller == null) return result;
        var seen = new HashSet<Selectable>();
        void Sweep(GameObject? root)
        {
            if (root == null) return;
            foreach (var b in root.GetComponentsInChildren<Button>(includeInactive: false))
                if (b.isActiveAndEnabled && b.GetComponentInParent<ProductItemSummarySlot>() == null && seen.Add(b))
                    result.Add(b);
        }
        Sweep(m_controller.gameObject);
        if (m_controller.activeOverlay != null) Sweep(m_controller.activeOverlay.gameObject);
        return result.OrderBy(s => PopupUtils.ReadingOrder(s.transform)).ToList();
    }

    // Worded buttons (DROP RATES, COLLECT REWARDS, UNLOCK) keep their text; the icon-only "X" reads "Close".
    private static string RewardButtonLabel(Selectable s)
    {
        foreach (var t in s.GetComponentsInChildren<TMP_Text>(includeInactive: false))
            if (!string.IsNullOrEmpty(t.text) && t.text.Any(char.IsLetter)) return KeyboardNavigator.DefaultLabel(s);
        return LocalizationUtils.Get(LocalizationKey.POPUP_CLOSE);
    }

    internal static string ItemName(ItemData item)
    {
        if (item.itemType == ItemData.ItemType.Card && !string.IsNullOrEmpty(item.clientId))
        {
            try
            {
                var row = ManagerSingleton<CardDatabaseManager>.instance?.TryGetCardFromDatabase(item.clientId);
                if (row != null) return CardText.NameWithType(row);
            }
            catch { /* database not ready: fall through to the generic name */ }
        }
        switch (item.itemType)
        {
            case ItemData.ItemType.HardCurrency: return ShopLabels.CurrencyWord(CurrencyType.HardCurrency);
            case ItemData.ItemType.TraderCurrency: return ShopLabels.CurrencyWord(CurrencyType.TradeCurrency);
            case ItemData.ItemType.TokenCurrency: return ShopLabels.CurrencyWord(CurrencyType.TokenCurrency);
            case ItemData.ItemType.SoftCurrency: return ShopLabels.CurrencyWord(CurrencyType.SoftCurrency);
        }
        if (LocalizationUtils.TryGetGame(item.contentLocId) is { } loc) return loc.Trim();
        // A booster's display name: the game titles it by using its clientId AS the loc key
        // (ContentItemRewardUI.SetTitle default case → SetTextByID(clientId)) — resolve the same key
        // and speak it VERBATIM: the loc string is the full display name and already ends in
        // "Booster Pack" ("Mega Evolution—Pitch Black Booster Pack", screenshot-verified 2026-07-23),
        // so appending anything would double it.
        if (item.itemType == ItemData.ItemType.Booster && LocalizationUtils.TryGetGame(item.clientId) is { } pack) return pack.Trim();
        return LocalizationUtils.Get(item.itemType switch
        {
            ItemData.ItemType.Booster => LocalizationKey.POPUP_ITEM_BOOSTER,
            ItemData.ItemType.Coin => LocalizationKey.POPUP_ITEM_COIN,
            ItemData.ItemType.Sleeve => LocalizationKey.POPUP_ITEM_SLEEVE,
            ItemData.ItemType.DeckBox => LocalizationKey.POPUP_ITEM_DECK_BOX,
            ItemData.ItemType.Deck => LocalizationKey.POPUP_ITEM_DECK,
            ItemData.ItemType.Avatar => LocalizationKey.POPUP_ITEM_AVATAR,
            _ => LocalizationKey.POPUP_ITEM,
        });
    }

    [HarmonyPatch(typeof(RewardOverlaysController), "ActivateCustomView")]
    private static class ActivateHook
    {
        private static void Postfix(RewardOverlaysController __instance)
        {
            s_instance.m_controller = __instance;
            PopupUtils.ShowOrRefresh(s_instance);
        }
    }

    [HarmonyPatch(typeof(RewardOverlaysController), "CloseScreen")]
    private static class CloseHook
    {
        private static void Postfix(RewardOverlaysController __instance)
        {
            if (s_instance.m_controller == __instance) s_instance.Close();
        }
    }
}
