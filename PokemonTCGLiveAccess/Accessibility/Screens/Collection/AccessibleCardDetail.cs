using System;
using System.Collections.Generic;
using System.Reflection;
using CardDatabase.DataAccess;
using HarmonyLib;
using MatchLogic;
using TMPro;
using TPCI.Localization;
using TPCI.Rainier.Features.DeckEditor;
using UnityEngine;
using UnityEngine.UI;
using static Accessibility.GameReflect;

namespace Accessibility;

// Card-detail overlay of the deck editor — driver: AddCardOverlayManager : BigCardOverlayController :
// OverlayScreenGroup, the big single-card view the deck editor's I key opens. It shows the card, its count in the
// deck, owned/craftable info, the rarity-VERSION drawer, and real + / − buttons.
//
// CRUCIAL: ownership and deck legality are keyed by the FULL CardID, which ENCODES the rarity — e.g. "budew" and
// "budew" reverse-holo are different CardIDs with separate owned counts, and the game validates per exact CardID. So
// every count here is PER VERSION (TotalOwnedQuantityForSpecificCard / TotalDeckQuantityForSpecificCard), never the
// archetype-wide TotalOwnedQuantity(). Reading the latter was the "says I own 2 but the deck is still invalid" bug:
// you owned 2 across two rarities but 0 of the one actually in the deck. (The game's own rarity bar uses the per-card
// numbers too — AddCardOverlayManager.SetupRarityInfoBar → RelativeDeckQuantityForSpecificCard.)
//
// Up/Down move between sections (only the ones that apply are present):
//   • Card     — the selected version: "name, type, rarity, N in deck, owned / not owned, exchangeable".
//                Left/Right browse other cards; +/− add/remove THIS version.
//   • Exchange — the game's "EXCHANGE" button (its code calls it Craft/CraftOpen). Reads "Exchange, N available";
//                Enter opens the quantity/cost popup (PurchaseCardButtonActions → AccessibleCraftCard). An unowned
//                card with no exchange option has no Exchange section; its reason is appended to the Card line.
//   • Versions — one entry per rarity printing, read off the game's RarityDrawerParts buttons. Enter picks it
//                (switches what +/− adds), exactly like clicking the rarity stack. Only with more than one version.
//   • Details  — HP / type / attacks / rules (CardText.DetailLines), line by line; Up at the first line steps back up.
//
// We drive the game's OWN widgets: +/− → AddClicked/RemoveClicked (= the deck editor's AddCard/RemoveCard the drag's
// drop also calls); a version pick → the ClickableCardStack button's onClick (= SelectedAlternateRarity).
internal sealed class AccessibleCardDetail : AccessibleUI
{
    private enum Section { Card, Craft, Versions, Details }

    private static readonly AccessibleCardDetail s_instance = new();

    // 1.5.0 renamed these protected properties to PascalCase (selectedCard -> SelectedCard); the getters are
    // case-sensitive, so the old lowercase lookup returned null and the overlay couldn't read which card was selected.
    private static readonly MethodInfo? s_selectedCard = AccessTools.PropertyGetter(typeof(BigCardOverlayController), "SelectedCard");
    private static readonly MethodInfo? s_selectedStack = AccessTools.PropertyGetter(typeof(BigCardOverlayController), "SelectedStack");
    private static readonly FieldInfo? s_numActiveVersions = AccessTools.Field(typeof(RarityDrawerParts), "_numActiveInstances");

    // True while the deck-editor card detail is open — lets the exchange popup hand back its "Exchanged" confirmation.
    public static bool IsOpen => s_instance.m_overlay != null;

    private AddCardOverlayManager? m_overlay;
    private Section m_section = Section.Card;
    private int m_verIndex;
    private int m_lineIndex;
    private readonly List<string> m_lines = new();
    private string? m_linesCardId;
    private bool m_fresh;

    public void HandleInput()
    {
        if (m_overlay == null) return;
        if (CollectionUi.BackPressed()) { m_overlay.CloseAddCardOverlay(); return; }

        // +/− always act on the currently-selected version, from any section.
        if (CollectionUi.AddCopyPressed()) { Adjust(add: true); return; }
        if (CollectionUi.RemoveCopyPressed()) { Adjust(add: false); return; }

        // Up/Down move between sections — EXCEPT inside Details, where they read the card's lines, stepping back up to
        // the section above only at the first line. Left/Right move within the horizontal sections.
        if (AccessibleKey.READ_NEXT_MENU_OPTION.IsPressed()) { if (m_section == Section.Details) StepLine(+1); else MoveSection(+1); return; }
        if (AccessibleKey.READ_PREV_MENU_OPTION.IsPressed()) { if (m_section == Section.Details && m_lineIndex > 0) StepLine(-1); else MoveSection(-1); return; }
        if (AccessibleKey.READ_NEXT_ITEM.IsPressed()) { MoveWithin(+1); return; }
        if (AccessibleKey.READ_PREV_ITEM.IsPressed()) { MoveWithin(-1); return; }
        if (AccessibleKey.CONFIRM.IsPressed()) Activate();
    }

    public string GetHelp() => LocalizationUtils.Get(LocalizationKey.COLLECTION_CARD_DETAIL_HELP);

    public void OnGainedFocus()
    {
        ResetToCard();
        if (m_fresh)
        {
            m_fresh = false;
            AccessibilityMgr.Output(this, LocalizationUtils.Get(LocalizationKey.COLLECTION_CARD_DETAILS), interrupt: true);
            AnnounceCard(interrupt: false);
            return;
        }
        // The exchange popup just closed. If a craft succeeded, confirm it, then re-read the (now-owned) card so the new
        // owned count is heard right after.
        if (AccessibleCraftCard.JustCrafted)
        {
            AccessibleCraftCard.JustCrafted = false;
            AccessibilityMgr.Output(this, LocalizationUtils.Get(LocalizationKey.COLLECTION_EXCHANGED), interrupt: true);
            AnnounceCard(interrupt: false);
        }
        else AnnounceCard(interrupt: true);
    }

    private void ResetToCard()
    {
        HideVersions();
        m_section = Section.Card;
        m_verIndex = 0;
        m_lineIndex = 0;
        m_linesCardId = null;
    }

    // ───────── sections ─────────

    private List<Section> Sections()
    {
        var list = new List<Section> { Section.Card };
        if (IsCraftable()) list.Add(Section.Craft);
        if (VersionStacks().Count > 1) list.Add(Section.Versions);
        EnsureLines();
        if (m_lines.Count > 0) list.Add(Section.Details);
        return list;
    }

    private void MoveSection(int dir)
    {
        var secs = Sections();
        var next = Math.Max(0, secs.IndexOf(m_section)) + dir;
        if (next < 0 || next >= secs.Count) return;   // stop at the ends
        if (m_section == Section.Versions) HideVersions();
        m_section = secs[next];
        switch (m_section)
        {
            case Section.Card:
                AnnounceCard(interrupt: true);
                break;
            case Section.Craft:
                AccessibilityMgr.Output(this, CraftLabel());
                break;
            case Section.Versions:
                ShowVersions();
                m_verIndex = CurrentVersionIndex();
                AccessibilityMgr.Output(this, LocalizationUtils.Get(LocalizationKey.COLLECTION_VERSIONS));
                AnnounceVersion(interrupt: false);
                break;
            case Section.Details:
                m_lineIndex = 0;
                AccessibilityMgr.Output(this, LocalizationUtils.Get(LocalizationKey.COLLECTION_DETAILS));
                if (m_lines.Count > 0) AccessibilityMgr.Output(this, m_lines[0], interrupt: false);
                break;
        }
    }

    private void MoveWithin(int dir)
    {
        if (m_section == Section.Card) Flip(dir);
        else if (m_section == Section.Versions) StepVersion(dir);
    }

    private void Activate()
    {
        if (m_section == Section.Versions) { ChooseVersion(); return; }
        if (m_section == Section.Craft) { m_overlay!.PurchaseCardButtonActions(); return; }   // → AccessibleCraftCard
        m_overlay!.CloseAddCardOverlay();   // Card / Details: Enter closes (Escape does too)
    }

    // ───────── Exchange ─────────
    // The game labels this "EXCHANGE" (its code calls it Craft / CraftOpen): the button that opens the quantity/cost
    // popup. addCardPurchaseParts is the coin/card ICON sitting on that button, so the real Exchange button is the
    // icon's parent Button.

    private AddCardPurchaseParts? PurchaseParts() => ReadField<AddCardPurchaseParts>(m_overlay, "addCardPurchaseParts");

    private Button? ExchangeButton()
    {
        var p = PurchaseParts();
        return p != null ? p.GetComponentInParent<Button>(includeInactive: true) : null;
    }

    // Show the Exchange section whenever that button is on screen — the icon being active (the game's own "can
    // purchase" signal) OR, failing that, the button itself being visible (a disabled Exchange, announced as
    // "unavailable"). Hidden exchange UI → no section.
    private bool IsCraftable()
    {
        var p = PurchaseParts();
        if (p != null && p.gameObject.activeInHierarchy) return true;
        var b = ExchangeButton();
        return b != null && b.isActiveAndEnabled;
    }

    private string CraftLabel()
    {
        var exchange = LocalizationUtils.Get(LocalizationKey.COLLECTION_EXCHANGE);
        var avail = AvailabilityText();
        var label = avail == null ? exchange : LocalizationUtils.Format(LocalizationKey.COMMON_STATE_FORMAT, exchange, avail);
        var b = ExchangeButton();
        return b != null && !b.IsInteractable() ? Announce.WithState(label, LocalizationKey.GLOBAL_DISABLED) : label;
    }

    // The game's own on-card availability readout ("3 Available" / "2 Unowned cards") — rarityCountText is the number,
    // rarityCountLabel the word. Read straight off the LocTexts so it matches the screen.
    private string? AvailabilityText()
    {
        var num = CollectionUi.Text(ReadField<LocText>(m_overlay, "rarityCountText"));
        var word = CollectionUi.Text(ReadField<LocText>(m_overlay, "rarityCountLabel"));
        if (num == null) return word;
        return word == null ? num : $"{num} {word}";
    }

    // The localized reason an unowned card can't be crafted (promo / not-yet-craftable date), read off the game's
    // uncraftable message bar — only when it's actually showing.
    private string? UncraftableReason()
    {
        var bar = ReadField<AnimatedSetActive>(m_overlay, "uncraftableIndicator");
        if (bar == null || !bar.currentlyVisible) return null;
        var parts = new List<string>();
        foreach (var t in bar.GetComponentsInChildren<TMP_Text>(includeInactive: false))
            if (CollectionUi.Text(t) is { } s) parts.Add(s);
        return parts.Count == 0 ? LocalizationUtils.Get(LocalizationKey.COLLECTION_CANNOT_EXCHANGE) : string.Join(", ", parts);
    }

    // ───────── Card ─────────

    private void Flip(int dir)
    {
        if (dir > 0) m_overlay!.NextClicked();
        else m_overlay!.PrevClicked();
        if (m_overlay == null) return;   // flipping past the end closes the overlay
        ResetToCard();
        AnnounceCard(interrupt: true);
    }

    private void AnnounceCard(bool interrupt)
    {
        var card = SelectedCard();
        if (card == null) { AccessibilityMgr.Output(this, LocalizationUtils.Get(LocalizationKey.COLLECTION_CARD_DETAILS), interrupt); return; }
        var row = ManagerSingleton<CardDatabaseManager>.instance?.TryGetCardFromDatabase(card.CardID);
        var parts = new List<string> { row != null ? CardText.NameWithType(row) : card.CardID };
        if ((CardText.RarityWithVariant(card) ?? CardText.RarityWithVariant(row)) is { Length: > 0 } rarity) parts.Add(rarity);
        if (CardText.SetName(card) is { Length: > 0 } set) parts.Add(set);
        parts.Add(LocalizationUtils.Format(LocalizationKey.COLLECTION_IN_DECK, DeckQty(card)));
        var owned = Owned(card);
        if (owned > 0) parts.Add(LocalizationUtils.Format(LocalizationKey.CARD_OWNED, owned));
        else
        {
            parts.Add(LocalizationUtils.Get(LocalizationKey.CARD_NOT_OWNED));
            var other = Stack()?.TotalOwnedQuantity() ?? 0;
            if (other > 0) parts.Add(LocalizationUtils.Format(LocalizationKey.COLLECTION_OWNED_OTHER_VERSIONS, other));
        }
        // Whether this version can be exchanged (Down to the Exchange section) or, if not, why not.
        if (IsCraftable()) parts.Add(LocalizationUtils.Get(LocalizationKey.COLLECTION_EXCHANGEABLE));
        else if (UncraftableReason() is { } reason) parts.Add(reason);
        AccessibilityMgr.Output(this, string.Join(", ", parts), interrupt);
    }

    private void Adjust(bool add)
    {
        // Detect a real change via the archetype's total deck count (robust to the game switching the selected version
        // on remove); report the now-selected version's count.
        var before = Stack()?.TotalDeckQuantity() ?? 0;
        if (add) m_overlay!.AddClicked();
        else m_overlay!.RemoveClicked();
        if ((Stack()?.TotalDeckQuantity() ?? 0) == before)
        {
            AccessibilityMgr.Output(this, LocalizationUtils.Get(add ? LocalizationKey.COLLECTION_CANT_ADD_MORE : LocalizationKey.COLLECTION_NONE_IN_DECK));
            return;
        }
        AccessibilityMgr.Output(this, LocalizationUtils.Format(add ? LocalizationKey.COLLECTION_ADDED_COUNT : LocalizationKey.COLLECTION_REMOVED_COUNT, DeckQty(SelectedCard())));
    }

    // ───────── Versions ─────────

    private void StepVersion(int dir)
    {
        var list = VersionStacks();
        var next = m_verIndex + dir;
        if (next < 0 || next >= list.Count) return;
        m_verIndex = next;
        var b = list[m_verIndex].GetClickableCardStackButton();
        if (b != null) b.Select();
        AnnounceVersion(interrupt: true);
    }

    private void ChooseVersion()
    {
        var list = VersionStacks();
        if (m_verIndex < 0 || m_verIndex >= list.Count) return;
        var btn = list[m_verIndex].GetClickableCardStackButton();
        if (btn == null) return;
        btn.onClick?.Invoke();   // → SelectedAlternateRarity (+ the game hides the drawer)
        HideVersions();
        m_section = Section.Card;
        m_linesCardId = null;    // version changed → detail lines change
        AnnounceCard(interrupt: true);
    }

    private void AnnounceVersion(bool interrupt)
    {
        var list = VersionStacks();
        if (list.Count == 0) { AccessibilityMgr.Output(this, LocalizationUtils.Get(LocalizationKey.COLLECTION_NO_VERSIONS), interrupt); return; }
        m_verIndex = Mathf.Clamp(m_verIndex, 0, list.Count - 1);
        var card = list[m_verIndex].StoredCard;
        var parts = new List<string> { CardText.RarityWithVariant(card) ?? LocalizationUtils.Format(LocalizationKey.COLLECTION_VERSION_N, m_verIndex + 1) };
        if (CardText.SetName(card) is { Length: > 0 } set) parts.Add(set);
        var owned = Owned(card);
        parts.Add(LocalizationUtils.Format(LocalizationKey.COLLECTION_OWNED_IN_DECK, owned, DeckQty(card)));
        if (owned == 0) parts.Add(LocalizationUtils.Get(LocalizationKey.CARD_NOT_OWNED));
        if (IsSelectedVersion(card)) parts.Add(LocalizationUtils.Get(LocalizationKey.GLOBAL_SELECTED));
        AccessibilityMgr.Output(this, LocalizationUtils.Format(LocalizationKey.MENU_OPTION_FORMAT, string.Join(", ", parts), m_verIndex + 1, list.Count), interrupt);
    }

    private int CurrentVersionIndex()
    {
        var list = VersionStacks();
        for (var i = 0; i < list.Count; i++)
            if (IsSelectedVersion(list[i].StoredCard)) return i;
        return 0;
    }

    private RarityDrawerParts? RarityDrawer() => ReadField<RarityDrawerParts>(m_overlay, "rarityDrawerParts");

    // The active per-version stack buttons the game built, in the drawer's own (rarity-sorted) order. activeSelf (not
    // activeInHierarchy) so they enumerate even while the drawer is visually collapsed.
    private List<ClickableCardStack> VersionStacks()
    {
        var result = new List<ClickableCardStack>();
        var drawer = RarityDrawer();
        var raw = ReadField<List<ClickableCardStack>>(drawer, "_cardRarityInstances");
        if (raw == null) return result;
        var n = s_numActiveVersions?.GetValue(drawer) is int active ? Math.Min(active, raw.Count) : raw.Count;
        for (var i = 0; i < n; i++)
            if (raw[i] != null && raw[i].gameObject.activeSelf && raw[i].StoredCard != null) result.Add(raw[i]);
        return result;
    }

    private void ShowVersions() => RarityDrawer()?.ShowRarityDrawer();

    private void HideVersions()
    {
        var drawer = RarityDrawer();
        if (drawer != null) drawer.HideRarityDrawer();
    }

    private bool IsSelectedVersion(ICardDataRow? card)
    {
        var sel = SelectedCard();
        return card != null && sel != null && string.Equals(card.CardID, sel.CardID, StringComparison.OrdinalIgnoreCase);
    }

    // ───────── Details ─────────

    private void StepLine(int dir)
    {
        EnsureLines();
        var next = m_lineIndex + dir;
        if (next < 0 || next >= m_lines.Count) return;
        m_lineIndex = next;
        AccessibilityMgr.Output(this, m_lines[m_lineIndex]);
    }

    private void EnsureLines()
    {
        var id = SelectedCard()?.CardID;
        if (id != null && id == m_linesCardId) return;
        m_lines.Clear();
        m_linesCardId = id;
        if (id == null) return;
        var row = ManagerSingleton<CardDatabaseManager>.instance?.TryGetCardFromDatabase(id);
        if (row != null) m_lines.AddRange(CardText.DetailLines(row));
    }

    // ───────── data ─────────

    private ICardDataRow? SelectedCard() => m_overlay != null ? s_selectedCard?.Invoke(m_overlay, null) as ICardDataRow : null;

    private ArchetypeDBCards? Stack() => m_overlay != null && s_selectedStack?.Invoke(m_overlay, null) is ArchetypeDBCards s ? s : null;

    // This version's copies in the deck (keyed by exact CardID).
    private int DeckQty(ICardDataRow? card) => card != null && Stack() is { } s ? s.TotalDeckQuantityForSpecificCard(card.CardID) : 0;

    // This version's copies owned (keyed by exact CardID) — the number that actually gates legality.
    private int Owned(ICardDataRow? card) => card != null && Stack() is { } s ? s.TotalOwnedQuantityForSpecificCard(card.CardID) : 0;

    // ───────── hooks ─────────

    private void Open(AddCardOverlayManager overlay)
    {
        m_overlay = overlay;
        if (AccessibilityMgr.IsFocused(this))
        {
            // Re-Setup while open (rare): just re-read.
            ResetToCard();
            AnnounceCard(interrupt: true);
            return;
        }
        m_fresh = true;
        AccessibilityMgr.ShowUI(this);
    }

    private void Closed(AddCardOverlayManager overlay)
    {
        if (overlay != m_overlay) return;
        m_overlay = null;
        AccessibilityMgr.HideUI(this);
    }

    // 1.5.0 expanded Setup to 5 params (added preferredDisplayCard + archetypeChangedAction). BigCardOverlayController
    // also declares a 2-param Setup, so name-only resolution is ambiguous — match the full signature.
    [HarmonyPatch(typeof(AddCardOverlayManager), "Setup", new[] { typeof(ArchetypeDBCards), typeof(ICardDataRow), typeof(AddCardOverlayManager.CardLocation), typeof(GameMode), typeof(AddCardOverlayManager.ArchetypeChangedDelegate) })]
    private static class SetupHook
    {
        private static void Postfix(AddCardOverlayManager __instance) => s_instance.Open(__instance);
    }

    [HarmonyPatch(typeof(AddCardOverlayManager), "OnDeActivate")]
    private static class OnDeActivateHook
    {
        private static void Postfix(AddCardOverlayManager __instance) => s_instance.Closed(__instance);
    }
}
