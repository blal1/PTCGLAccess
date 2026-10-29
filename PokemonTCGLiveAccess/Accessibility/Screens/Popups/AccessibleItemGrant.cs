using System.Collections.Generic;
using System.Reflection;
using CardDatabase.DataAccess;
using HarmonyLib;
using SharedLogicUtils.source.DataTypes;
using TMPro;
using TPCI.Localization;
using UnityEngine;
using static Accessibility.GameReflect;

namespace Accessibility;

// Item Grant / reward card reveal — driver: ContentItemGrantingOverlay (OverlayScreenGroup),
// the screen that flips through cards/items you just earned (battle pass tiers, packs, shop, …).
//
// It shows ONE reward at a time as `currentItem` (ContentItemRewardController). Clicking it
// (OnClick → AnimateOut → ShowNextItem) reveals the next; `collectAllButton` skips to the end;
// once everything is shown `currentItem` goes null and a results display appears.
//
// We read each reward as the player flips through it: a card resolves its CardDataRow via
// CardDatabaseManager.TryGetCardFromDatabase(clientId) (name + type, Down/Up walk the detail lines);
// a non-card reward (currency/token/sleeve) reads the overlay's own displayed name + amount
// (ContentItemRewardUI.titleTxt + footerTxt — "Event Token, Amount 10"). Enter / Backspace advances
// (currentItem.OnClick); Left/Right move between the reward and the Collect All button (so it's
// reachable and read by name); on the final results display Enter closes (OnFinalDisplayClick).
//
// The reward changes under us (animations, the next item appearing), so it is followed in HandleInput:
// the new reward is announced on the frame it appears.
internal sealed class AccessibleItemGrant : AccessibleUI
{
    private static readonly AccessibleItemGrant s_instance = new();
    private static readonly FieldInfo? s_boosterState = AccessTools.Field(typeof(ContentItemBoosterInteraction), "state");
    private static readonly MethodInfo? s_boosterClick = AccessTools.Method(typeof(ContentItemBoosterInteraction), "OnActiveClick");

    private readonly CardLines m_card;
    private ContentItemGrantingOverlay? m_overlay;

    private ContentItemRewardController? m_shown;   // reward currently announced
    private string m_header = "";                    // what the reward (or booster card) is announced as
    private List<string> m_lines = new();            // its card detail lines
    private bool m_finalAnnounced;
    private bool m_namePending;                       // a non-card reward whose title wasn't ready yet
    private bool m_onCollectAll;                      // Left/Right focus: the reward, or the Collect All button

    private ContentItemRewardController_Booster? m_booster;   // a pack being revealed card-by-card
    private int m_boosterStep = int.MinValue;

    private AccessibleItemGrant()
    {
        m_card = new CardLines(this);
    }

    public void HandleInput()
    {
        if (m_overlay == null || !m_overlay.gameObject.activeInHierarchy)
        {
            Close();
            return;
        }

        // Final "COLLECTED" summary (resultsDisplay active): read the totals + the DONE button.
        var results = ReadField<ContentItemResultsDisplay>(m_overlay, "resultsDisplay");
        if (results != null && results.gameObject.activeInHierarchy)
        {
            if (!m_finalAnnounced)
            {
                m_finalAnnounced = true;
                m_shown = null;
                AnnounceCollected(results);
            }
            if (AccessibleKey.CONFIRM.IsPressed() || AccessibleKey.BACK.IsPressed()) m_overlay.OnFinalDisplayClick();
            return;
        }
        m_finalAnnounced = false;

        var item = m_overlay.currentItem;

        // A booster pack reveals its cards INSIDE one currentItem (ContentItemRewardController_Booster):
        // each tap steps the 3D pack to the next card. The normal per-currentItem path would read the
        // pack as a single item and Enter→OnClick would skip the whole pack, so drive the pack's own
        // reveal here instead — card by card.
        if (item is ContentItemRewardController_Booster booster)
        {
            HandleBooster(booster);
            return;
        }
        m_booster = null;

        if (item != m_shown)
        {
            m_shown = item;
            m_onCollectAll = false;
            if (item != null) AnnounceItem(item);
        }
        else if (m_namePending && item != null)
        {
            AnnounceItem(item);   // title wasn't ready on the first pass — retry until it lands
        }
        if (item == null) return;

        if (HandleCollectAllFocus(() => AnnounceItem(item))) return;
        // On the reward: Enter / Backspace advances to the next reward (or the results display); Down/Up walk
        // a card's detail lines.
        if (AccessibleKey.CONFIRM.IsPressed() || AccessibleKey.BACK.IsPressed()) item.OnClick();
        else m_card.HandleInput();
    }

    public string GetHelp() => LocalizationUtils.Get(LocalizationKey.POPUP_ITEM_GRANT_HELP);

    // The current reward (or the final summary) is announced by the next HandleInput.
    public void OnGainedFocus()
    {
        m_shown = null;
        m_booster = null;
        m_finalAnnounced = false;
        m_onCollectAll = false;
    }

    private void Close()
    {
        m_overlay = null;
        AccessibilityMgr.HideUI(this);
    }

    // Left/Right move between the reward and the Collect All button, so the button is reachable + read by
    // name — no hidden shortcut. True when the key was used.
    private bool HandleCollectAllFocus(System.Action announceReward)
    {
        var btn = m_overlay!.collectAllButton;
        if (AccessibleKey.READ_PREV_ITEM.IsPressed())
        {
            if (m_onCollectAll)
            {
                m_onCollectAll = false;
                announceReward();
            }
            return true;
        }
        if (AccessibleKey.READ_NEXT_ITEM.IsPressed())
        {
            if (btn != null && btn.isActiveAndEnabled && !m_onCollectAll)
            {
                m_onCollectAll = true;
                AccessibilityMgr.Output(this, CollectAllLabel(), interrupt: true);
            }
            return true;
        }
        if (!m_onCollectAll) return false;
        if (AccessibleKey.CONFIRM.IsPressed() && btn != null && btn.isActiveAndEnabled)
        {
            if (btn.IsInteractable()) btn.onClick?.Invoke();
            else AccessibilityMgr.Output(this, CollectAllLabel(), interrupt: true);
        }
        return true;
    }

    // The tutorial DISABLES this button. ItemGrantButtonController.WaitForOverlay sets
    // `grantOverlay.collectAllButton.interactable = false` as soon as any item-grant FTUE step arms, and
    // only its CollectAll step ever turns it back on — during the ClickItem step you are meant to reveal
    // each card yourself. So it stays reachable but reads as unavailable, and pressing it says so instead
    // of doing nothing at all.
    private string CollectAllLabel()
    {
        var btn = m_overlay?.collectAllButton;
        var label = PopupUtils.Text(btn) ?? LocalizationUtils.Get(LocalizationKey.POPUP_COLLECT_ALL);
        return btn != null && !btn.IsInteractable() ? Announce.WithState(label, LocalizationKey.GLOBAL_DISABLED) : label;
    }

    // The final "COLLECTED" summary: the totals (cards + each currency), then the DONE action (Enter / Backspace).
    private void AnnounceCollected(ContentItemResultsDisplay rd)
    {
        var parts = new List<string>();
        AddStat(parts, ReadField<GameObject>(rd, "cardsCollectedContainer"), ReadField<LocText>(rd, "cardCountTxt"), LocalizationUtils.Get(LocalizationKey.POPUP_CARDS));
        AddStat(parts, ReadField<GameObject>(rd, "hardCollectedContainer"), ReadField<LocText>(rd, "hardCountTxt"), ShopLabels.CurrencyWord(CurrencyType.HardCurrency));
        AddStat(parts, ReadField<GameObject>(rd, "traderCollectedContainer"), ReadField<LocText>(rd, "traderCountTxt"), ShopLabels.CurrencyWord(CurrencyType.TradeCurrency));
        AddStat(parts, ReadField<GameObject>(rd, "tokensCollectedContainer"), ReadField<LocText>(rd, "tokenCountTxt"), ShopLabels.CurrencyWord(CurrencyType.TokenCurrency));
        AccessibilityMgr.Output(this, parts.Count == 0
            ? LocalizationUtils.Get(LocalizationKey.POPUP_COLLECTED_DONE)
            : LocalizationUtils.Format(LocalizationKey.POPUP_COLLECTED_DONE_FORMAT, string.Join(", ", parts)), interrupt: true);
    }

    // One TOTAL-COLLECTED stat if its container is shown; the count text is just the number, so append the
    // type label unless the game's text already has words.
    private static void AddStat(List<string> parts, GameObject? container, LocText? count, string label)
    {
        if (container == null || !container.activeInHierarchy) return;
        if (PopupUtils.Text(count) is not { } txt) return;
        parts.Add(UiLabels.HasLetter(txt) ? txt : LocalizationUtils.Format(LocalizationKey.POPUP_STAT_FORMAT, txt, label));
    }

    private void AnnounceItem(ContentItemRewardController item)
    {
        m_namePending = false;
        m_lines = new List<string>();
        m_card.Reset();

        var granted = ReadField<GrantedItem>(item, "itemData");
        var row = CardRow(granted?.clientId);
        if (row != null)
        {
            m_lines = CardText.DetailLines(row);
            var name = CardText.NameTypeRarity(row);
            m_header = granted!.isNewCard ? LocalizationUtils.Format(LocalizationKey.POPUP_NEW_CARD_FORMAT, name) : name;
            AccessibilityMgr.Output(this, m_header, interrupt: true);
            return;
        }

        // Non-card reward (avatar item, currency, sleeve, …) — read the overlay's OWN displayed
        // name + amount (ContentItemRewardUI.titleTxt / footerTxt). The reveal sets the title a beat
        // AFTER the item appears, so on the first pass it can still be blank — never blurt the raw
        // item id ("upper_f_035-002") as a fallback; flag it pending and re-announce (HandleInput retries)
        // once the title lands.
        var ui = ReadField<ContentItemRewardUI>(item, "ui");
        var title = PopupUtils.Text(ReadField<LocText>(ui, "titleTxt"));
        if (title == null)
        {
            m_namePending = true;
            return;
        }
        var amount = PopupUtils.Text(ReadField<LocText>(ui, "footerTxt"));
        m_header = amount != null ? LocalizationUtils.Format(LocalizationKey.COMMON_STATE_FORMAT, title, amount) : title;
        AccessibilityMgr.Output(this, m_header, interrupt: true);
    }

    // ---- booster pack reveal (card by card) ----

    private void HandleBooster(ContentItemRewardController_Booster booster)
    {
        if (!ReferenceEquals(booster, m_booster))
        {
            m_booster = booster;
            m_shown = booster;
            m_boosterStep = int.MinValue;
            m_onCollectAll = false;
        }

        // The interaction's `state`: the revealed card index + 1 (0 = the unopened pack, before the first tap).
        var interaction = ReadField<ContentItemBoosterInteraction>(booster, "interaction");
        var step = interaction != null && s_boosterState?.GetValue(interaction) is int s ? s : int.MinValue;
        if (step != m_boosterStep)
        {
            m_boosterStep = step;
            m_onCollectAll = false;
            AnnounceBooster(booster);
        }

        // Same Left/Right focus model as the one-reward flow: the pack/card, or the Collect All
        // button. The game keeps Collect All active and clickable right through a pack reveal
        // (ContentItemGrantingOverlay.Activate shows it; OnClickCollectAll works until the final
        // tally) — a sighted player can bail out of a long reveal at any card, so we must too.
        if (HandleCollectAllFocus(() => AnnounceBooster(booster))) return;

        // Enter / Backspace step the real 3D pack to the next card via the interaction's own click handler
        // (what a tap calls) — it reveals the next card, or finishes the pack (→ the next reward).
        if (AccessibleKey.CONFIRM.IsPressed() || AccessibleKey.BACK.IsPressed())
        {
            if (interaction != null) s_boosterClick?.Invoke(interaction, null);
        }
        else m_card.HandleInput();
    }

    private void AnnounceBooster(ContentItemRewardController_Booster booster)
    {
        m_card.Reset();
        m_lines = new List<string>();
        if (m_boosterStep <= 0)
        {
            // Sealed pack: no card is on screen, so no detail lines. Without this clear, Down/Up
            // kept reading the PREVIOUS pack's last card — the card was gone but its stats weren't
            // (reported 2026-07-23).
            m_header = SealedPackText(booster);
        }
        else
        {
            var index = m_boosterStep - 1;
            var cards = ReadField<List<GrantedItem>>(booster, "cardsData");
            var granted = cards != null && index < cards.Count ? cards[index] : null;
            var row = CardRow(granted?.clientId);
            if (row != null)
            {
                m_lines = CardText.DetailLines(row);
                var name = CardText.NameTypeRarity(row);
                if (granted!.isNewCard) name = LocalizationUtils.Format(LocalizationKey.POPUP_NEW_CARD_FORMAT, name);
                m_header = LocalizationUtils.Format(LocalizationKey.MENU_OPTION_FORMAT, name, index + 1, booster.numberOfCards);
            }
            else m_header = granted?.clientId ?? LocalizationUtils.Get(LocalizationKey.CARD_CARD);
        }
        AccessibilityMgr.Output(this, m_header, interrupt: true);
    }

    // The sealed-pack screen, spoken as it reads: "Mega Evolution—Pitch Black Booster Pack, 10 cards.
    // Packs left: 3." Title and packs-left are the game's own texts (no invented "Opening" — the
    // screen doesn't say it, and nothing has opened yet); the card count is the one mod-added fact.
    private static string SealedPackText(ContentItemRewardController_Booster booster)
    {
        var ui = ReadField<ContentItemRewardUI>(booster, "ui");
        var name = LocalizationUtils.TryGetGame(ReadField<GrantedItem>(booster, "itemData")?.clientId)
            ?? PopupUtils.Text(ReadField<LocText>(ui, "titleTxt"))
            ?? LocalizationUtils.Get(LocalizationKey.POPUP_PACK);
        var text = LocalizationUtils.Format(LocalizationKey.POPUP_PACK_FORMAT, name.Trim(), booster.numberOfCards);
        var footer = PopupUtils.Text(ReadField<LocText>(ui, "footerTxt"));
        return footer != null && !footer.Contains("(Missing)") ? LocalizationUtils.Format(LocalizationKey.POPUP_TITLE_BODY, text, footer) : text;
    }

    private static CardDataRow? CardRow(string? clientId)
    {
        if (string.IsNullOrEmpty(clientId)) return null;
        try { return ManagerSingleton<CardDatabaseManager>.instance?.TryGetCardFromDatabase(clientId); }
        catch { return null; }
    }

    // The announced reward, then its card detail lines (Down / Up / Shift+Down).
    private sealed class CardLines : AccessibleItem
    {
        public CardLines(AccessibleItemGrant parent) : base(parent) { }

        protected internal override List<string> GetLines()
        {
            var lines = new List<string> { s_instance.m_header };
            lines.AddRange(s_instance.m_lines);
            return lines;
        }
    }

    [HarmonyPatch(typeof(ContentItemGrantingOverlay), "Activate")]
    private static class ActivateHook
    {
        private static void Postfix(ContentItemGrantingOverlay __instance)
        {
            s_instance.m_overlay = __instance;
            if (!AccessibilityMgr.IsFocused(s_instance)) AccessibilityMgr.ShowUI(s_instance);
        }
    }

    [HarmonyPatch(typeof(ContentItemGrantingOverlay), "OnDeActivate")]
    private static class DeactivateHook
    {
        private static void Postfix(ContentItemGrantingOverlay __instance)
        {
            if (s_instance.m_overlay == __instance) s_instance.Close();
        }
    }
}
