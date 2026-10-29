using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using CardDatabase.DataAccess;
using HarmonyLib;
using TMPro;
using TPCI.Rainier.Features.DeckEditor;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;
using _Rainier.Scripts.UI.FrontEnd;
using static Accessibility.GameReflect;
using CardCategory = TPCI.Rainier.CardCategory;

namespace Accessibility;

// DECK EDITOR — driver: HUBEditDeckController (a HUBScreenController), hook OnActivate/OnDeactivate.
//
// ── How adding/removing actually works ──
// The on-screen "drag a card into the deck" gesture is COSMETIC: CardEditorDrag just clones a picture of the card
// and moves it under the cursor. The real work happens at the drop, and DeckEditorCardDropTarget.OnDrop does nothing
// but call the controller's canonical HUBEditDeckController.AddCard / RemoveCard — the SAME methods the card-detail
// overlay's real +/- buttons (AddCardOverlayManager.AddClicked/RemoveClicked) call. AddCard already runs the legality
// check (CanAddCard → DeckValidationManager), mutates the deck, refreshes the UI and logs analytics. So this is the
// game's real endpoint, not a parallel op.
//
// There is NO placement target to choose — a card goes into the one deck — but the drag is still done properly as a
// pick-up → navigate → place flow (not a one-key shortcut): Enter picks a card up, Tab moves to the destination
// group, Enter there places it. It's symmetric: hold a LIBRARY card → place on the DECK = add; hold a DECK card →
// place on the LIBRARY = remove.
//
// ── The FTUE catch ──
// During the tutorial the deck step wires SetFtueDrop(...) so OnDrop — AFTER AddCard/RemoveCard — fires ftueDrop() →
// indicator.Unblock(), which advances the tutorial (in landscape the focused card has noClick=true, so that drop
// callback is the ONLY thing that advances it). We call AddCard/RemoveCard directly, so we fire the armed drop's
// callback ourselves (CompleteFtueDrop) — else the deck edits but the tutorial freezes. The armed drop target is the
// tutorial signal here; the FTUE-specific guidance of the previous mod (jump to and lock on the target card) needs the
// shared FTUE state and is not ported yet.
//
// ── Navigation ──
// The card library is a VIRTUALIZED EnhancedScroller (only ~a screenful of cells exist), so we navigate the DATA, not
// realized cells: the library keeps the full filtered list (reached via PopulateCollectionDrawer.GetNextCard /
// GetPreviousCard / GetCurrentCollectionAmount) and the deck keeps DataRowDeck.DeckStacks (filtered+sorted by
// CardSortHelper.SortDeck). A realized CardStubParts is only needed to inspect, and we scroll it into view the game's
// own way. Sections cycle with Tab; within a card group Left/Right moves between cards and Down/Up reads the card's
// detail lines (battle convention). I opens the game's real card-detail overlay (AccessibleCardDetail) — the second,
// quantity-precise add/remove method.
internal sealed class AccessibleEditDeck : AccessibleScreen
{
    private enum Section { Top, Tabs, Deck, Library }

    private static readonly AccessibleEditDeck s_instance = new();

    private static readonly MethodInfo? s_hideScreen = AccessTools.Method(typeof(HUBScreenController), "HideScreen");
    private static readonly FieldInfo? s_ftueDrop = AccessTools.Field(typeof(DeckEditorCardDropTarget), "ftueDrop");
    private static readonly FieldInfo? s_hasFtueDrop = AccessTools.Field(typeof(DeckEditorCardDropTarget), "hasFtueDrop");
    private static readonly FieldInfo? s_originalParent = AccessTools.Field(typeof(DeckEditorCardDropTarget), "originalParent");

    private const string RenameHandler = "Settings_BeginNameChange";

    private readonly KeyboardNavigator m_topNav;
    private readonly KeyboardNavigator m_tabsNav;
    private readonly KeyboardNavigator m_sortNav;
    private readonly CardLines m_card;
    private HUBEditDeckController? m_controller;
    private bool m_fresh;
    private int m_notCurrentFrames;   // consecutive frames the editor hasn't been the current HUB screen

    private Section m_section = Section.Library;
    private GlobalCategorySelector? m_categorySelector;
    private CardCategory m_lastCategory = (CardCategory)(-1);

    // The bulk "Exchange all unowned" button (CraftUnownedCardsButtonController), surfaced in the Top section only
    // while visible; cached each rebuild so TopLabel can give it a real label.
    private Button? m_exchangeButton;
    private LabeledPurchaseButton? m_exchangeParts;

    // Deck group (data): the filtered+sorted deck stacks and a cursor.
    private ArchetypeDBCards[] m_deckCards = Array.Empty<ArchetypeDBCards>();
    private int m_deckIndex = -1;

    // Library group (data): cursor into the drawer's filtered collection, navigated incrementally (the full list can be
    // hundreds of cards — we never materialize it, we step with GetNext/GetPreviousCard).
    private int m_libIndex = -1;
    private ArchetypeDBCards? m_libCurrent;
    private int m_libCount;
    private bool m_libAwaitingLoad;   // entered Library before its async filter query produced cards
    private bool m_collectionCallbackRegistered;

    // Held card (the "pick up" half of the drag). We hold the card's DATA row (not a realized stub) so pickup is
    // synchronous and immune to the scroller's virtualization — the place step calls AddCard/RemoveCard directly,
    // which is exactly what the drop does.
    private bool m_holding;
    private ICardDataRow? m_heldCard;
    private CardEditorDrag.CardDropAction m_heldAction;
    private Section m_heldFrom;
    private string m_heldName = "";

    // The sort / filter popups (AnimatedSetActive on the controller). When one is open we run a modal sort mode: focus
    // into its toggles, Tab is inert, Escape closes it.
    private AnimatedSetActive? m_sortPopup;   // the popup the modal mode is currently driving

    // Tutorial guidance (FtueTracker owns the FTUE hooks). Card step: jump to the card the tutorial names. Non-card step
    // (category tab, Back...): the coached control is reparented under a Blocker, outside our sections, so present it alone.
    private readonly KeyboardNavigator m_ftueNav;
    private string m_lastFtueCardId = "";
    private Selectable? m_ftueFocusShown;

    private AccessibleEditDeck()
    {
        m_topNav = new KeyboardNavigator(this) { AllowTab = false };
        m_tabsNav = new KeyboardNavigator(this) { AllowTab = false, RadioToggleMode = true };
        m_sortNav = new KeyboardNavigator(this) { AllowTab = false };
        m_card = new CardLines(this);
        m_ftueNav = new KeyboardNavigator(this) { AllowTab = false };
    }

    // ── Component ──

    public void HandleInput()
    {
        if (m_controller == null) return;

        // Lifecycle-desync guard: the FTUE (and some HUB nav paths) can leave the editor for another screen WITHOUT
        // firing HUBEditDeckController.OnDeactivate, which would leave us owning input on a screen we don't drive. If the
        // editor stops being the current HUB screen for a few frames, let go (a few frames, not one, so a 1-frame entry
        // race where currentScreen lags OnActivate can't drop a live editor).
        if (!IsCurrentHubScreen())
        {
            if (++m_notCurrentFrames >= 3)
            {
                Log.Info("DeckEditor: no longer the current HUB screen — letting go (lifecycle desync)");
                Deactivated(m_controller);
            }
            return;
        }
        m_notCurrentFrames = 0;

        // Category switched (toggle fired) → re-filter both card groups. The library re-queries asynchronously, so flag
        // it to re-focus card 0 when the new collection arrives (OnCollectionChanged); the deck is synchronous.
        if (m_categorySelector != null && m_categorySelector.CurrentVal != m_lastCategory)
        {
            m_lastCategory = m_categorySelector.CurrentVal;
            RefreshDeck();
            m_deckIndex = m_deckCards.Length > 0 ? 0 : -1;
            m_libIndex = -1;
            m_libCount = 0;
            m_libAwaitingLoad = true;
            if (m_section == Section.Deck && m_deckCards.Length > 0) DeckFocus(0, announce: true);
        }

        UpdateFtueGuidance();
        if (m_ftueFocusShown != null)
        {
            if (CollectionUi.BackPressed()) { AnnounceFtueFocus(); return; }   // the game blocks everything else too
            m_ftueNav.HandleInput();
            return;
        }

        // The sort / filter popup is its OWN modal: while it's open, drive only its options.
        if (HandleSortMode()) return;

        if (AccessibleKey.NEXT_SECTION.IsPressed()) { CycleSection(+1); return; }
        if (AccessibleKey.PREV_SECTION.IsPressed()) { CycleSection(-1); return; }

        // Escape / Backspace: cancel a hold first; otherwise back out of the editor (to DECKS).
        if (CollectionUi.BackPressed())
        {
            if (m_holding) CancelHold(announce: true);
            else DoBack();
            return;
        }

        switch (m_section)
        {
            case Section.Top: m_topNav.HandleInput(); break;
            case Section.Tabs: m_tabsNav.HandleInput(); break;
            case Section.Deck: HandleCardGroup(Section.Deck); break;
            case Section.Library: HandleCardGroup(Section.Library); break;
        }
    }

    public string GetHelp() => LocalizationUtils.Get(LocalizationKey.COLLECTION_EDIT_DECK_HELP);

    public void OnGainedFocus()
    {
        if (m_controller == null) return;
        ClearHold();
        RebuildSections();
        if (m_fresh)
        {
            m_fresh = false;
            m_section = Section.Library;
            m_lastFtueCardId = "";
            m_ftueFocusShown = null;
            AnnounceIntro();
            if (!FtueCardStep) EnterSection(Section.Library, interruptPrior: false);
            UpdateFtueGuidance();
            return;
        }
        if (m_ftueFocusShown != null) { AnnounceFtueFocus(); return; }
        // A child overlay (card detail) closed, or a popup over us did. The deck may have changed.
        ReAnnounceCurrent(interrupt: true);
    }

    // ── Sections ──

    private void RebuildSections()
    {
        if (m_controller == null) return;

        m_categorySelector = m_controller.GetComponentInChildren<GlobalCategorySelector>(includeInactive: false);
        if (m_categorySelector != null) m_lastCategory = m_categorySelector.CurrentVal;

        // Category tabs (Pokémon / Trainer / Energy / All) — radio toggles.
        m_tabsNav.SetItems(CategoryToggles(), CategoryLabel);
        m_tabsNav.GroupName = LocalizationUtils.Get(LocalizationKey.COLLECTION_CATEGORY);
        RebuildTopNav();

        // The library's filtered list loads asynchronously (and re-loads on category/search/filter changes). Subscribe to
        // its own change callback so we focus card 0 the moment it's ready, rather than polling.
        // AddCollectionChangedCallback dedups, and the drawer is a persistent singleton, so once is enough.
        if (!m_collectionCallbackRegistered && m_controller.CollectionDrawer != null)
        {
            m_controller.CollectionDrawer.AddCollectionChangedCallback(OnCollectionChanged);
            m_collectionCallbackRegistered = true;
        }

        RefreshDeck();
        SyncLibCount();
    }

    private List<Selectable> CategoryToggles() =>
        m_categorySelector != null
            ? CollectionUi.InReadingOrder(m_categorySelector.GetComponentsInChildren<Toggle>(includeInactive: false).Where(t => t.isActiveAndEnabled))
            : new List<Selectable>();

    // Top bar: header/options/back buttons — every Button that ISN'T a card stub's button or a category toggle.
    //
    // Rebuilt on its own, and not only from RebuildSections, because "Exchange all unowned" APPEARS LATE.
    // CraftUnownedCardsButtonController hides that button on Start and re-hides it at the top of every
    // OnDeckCardCountsChanged; it only comes back at the end of a coroutine that awaits Commerce.GetCurrencyAsync — a
    // Task, so at best a frame later and at worst a network round trip. Every scan that races it therefore MISSES the
    // button: on entering the editor, and again after each add/remove. The button's own label and cost are also written
    // by that same Setup call. LabeledPurchaseButton.Setup's hook re-runs this the instant the game finishes setting the
    // button up, which is exactly when a sighted player sees it.
    private void RebuildTopNav()
    {
        if (m_controller == null) return;
        var keep = m_section == Section.Top ? m_topNav.Current : null;
        var toggles = new HashSet<Selectable>(CategoryToggles());
        var buttons = m_controller.GetComponentsInChildren<Button>(includeInactive: false)
            .Where(b => b.isActiveAndEnabled && b.GetComponentInParent<CardStubParts>() == null && !toggles.Contains(b))
            .Cast<Selectable>().ToList();
        DiscoverExchangeButton(buttons);
        EnsureRenameButton(buttons);
        buttons = CollectionUi.InReadingOrder(buttons);
        m_topNav.SetItems(buttons, TopLabel);
        // These buttons are scattered across the top bar AND the right deck panel, so auto-axis detection strands one
        // cluster. Force a flat Up/Down list so every option is reachable in reading order.
        m_topNav.ForceFlatVertical();
        m_topNav.GroupName = LocalizationUtils.GetGame("deck_options", LocalizationKey.UI_DECK_OPTIONS);

        // Rebuilding drops the cursor. If the player is standing in this section, put them back on the SAME option (by
        // identity — a late button is inserted in reading order, so the index is not stable), silently.
        var i = keep != null ? buttons.IndexOf(keep) : -1;
        if (i >= 0) m_topNav.FocusIndex(i, announce: false);
    }

    // Locate the "Exchange all unowned" button (CraftUnownedCardsButtonController, not a serialized child of the deck
    // controller) and, when its content is showing, add its real Button to the Top list. Activating it routes to
    // AttemptCraftingPurchase → CraftAllConfirmOverlay (AccessibleCraftAllConfirm) or the insufficient-funds error.
    private void DiscoverExchangeButton(List<Selectable> buttons)
    {
        m_exchangeButton = null;
        m_exchangeParts = null;
        var ctrl = UnityEngine.Object.FindFirstObjectByType<CraftUnownedCardsButtonController>();
        var lpb = ReadField<LabeledPurchaseButton>(ctrl, "purchaseButton");
        var btn = ReadField<Button>(lpb, "button");
        if (lpb == null || btn == null) return;
        var content = ReadField<GameObject>(lpb, "buttonContent");
        var visible = content != null ? content.activeInHierarchy : btn.gameObject.activeInHierarchy;
        if (!visible) return;
        m_exchangeButton = btn;
        m_exchangeParts = lpb;
        if (!buttons.Contains(btn)) buttons.Add(btn);
    }

    // The ✎ beside the deck name — the editor's only route to renaming a deck. It's icon-only, so it can't be matched by
    // text and its GameObject name is an art detail; match it by the game method its prefab onClick calls
    // (HUBEditDeckController.Settings_BeginNameChange). The generic sweep normally picks it up; this is the guarantee.
    private void EnsureRenameButton(List<Selectable> buttons)
    {
        if (buttons.Any(s => UiLabels.HasHandler(s as Button, RenameHandler))) return;
        var rename = m_controller!.GetComponentsInChildren<Button>(includeInactive: true).FirstOrDefault(b => UiLabels.HasHandler(b, RenameHandler));
        if (rename == null) { Log.Warn($"DeckEditor: no button wired to {RenameHandler} — renaming is unreachable from the editor"); return; }
        if (rename.isActiveAndEnabled) buttons.Add(rename);
    }

    // "Exchange all unowned, 1500 Credits" — the button's own label text + cost, both read off the LabeledPurchaseButton
    // so they match the screen.
    private string TopLabel(Selectable s)
    {
        if (m_exchangeButton == null || s != m_exchangeButton) return KeyboardNavigator.DefaultLabel(s);
        var label = CollectionUi.Text(ReadField<TMP_Text>(m_exchangeParts, "buttonLabel")) ?? LocalizationUtils.Get(LocalizationKey.COLLECTION_EXCHANGE_ALL_UNOWNED);
        var cost = CollectionUi.Text(ReadField<TMP_Text>(m_exchangeParts, "costLabel"));
        return cost == null ? label : LocalizationUtils.Format(LocalizationKey.COMMON_STATE_FORMAT, label, $"{cost} {AccessibleCraftCard.CreditsWord()}");
    }

    // The drawer's filtered collection finished (re)loading — initial open, category switch, search or filter. Refresh
    // our count and, if we're parked in the Library awaiting it, focus card 0.
    private void OnCollectionChanged(int count)
    {
        m_libCount = count;
        if (m_controller == null || m_section != Section.Library || !m_libAwaitingLoad) return;
        m_libAwaitingLoad = false;
        if (count > 0) LibFocus(0, announce: true, interrupt: false);
    }

    private bool SectionHasContent(Section s) => s switch
    {
        Section.Top => m_topNav.Count > 0,
        Section.Tabs => m_tabsNav.Count > 0,
        // While holding a card to ADD, the Deck section must be reachable even when empty — otherwise you can't Tab to it
        // to drop the first card. Same for the Library while holding a card to REMOVE.
        Section.Deck => m_deckCards.Length > 0 || (m_holding && m_heldAction == CardEditorDrag.CardDropAction.Add),
        Section.Library => m_libCount > 0 || m_libAwaitingLoad || (m_holding && m_heldAction == CardEditorDrag.CardDropAction.Remove),
        _ => false,
    };

    private void CycleSection(int dir)
    {
        // While holding a card, only cycle the groups you can place it into (Deck / Library) — Tab shouldn't wander to
        // the Top bar or Category tabs mid-drag.
        var order = m_holding
            ? new[] { Section.Deck, Section.Library }
            : new[] { Section.Top, Section.Tabs, Section.Deck, Section.Library };
        var cur = Array.IndexOf(order, m_section);
        // Currently OUTSIDE the restricted order: enter the first allowed section rather than stepping from it (treating
        // cur as 0 made Tab SKIP Deck and land on Library — "Tab to the deck" announced the card library instead).
        if (cur < 0)
        {
            foreach (var s in order)
                if (SectionHasContent(s)) { EnterSection(s, interruptPrior: true); return; }
            return;
        }
        for (var step = 1; step <= order.Length; step++)
        {
            var cand = order[((cur + dir * step) % order.Length + order.Length) % order.Length];
            if (SectionHasContent(cand)) { EnterSection(cand, interruptPrior: true); return; }
        }
    }

    private void EnterSection(Section s, bool interruptPrior)
    {
        // Fall through to a section that actually has content (e.g. empty deck → Library).
        if (!SectionHasContent(s))
        {
            var fallback = new[] { Section.Library, Section.Deck, Section.Tabs, Section.Top }.FirstOrDefault(SectionHasContent);
            if (!SectionHasContent(fallback)) return;
            Log.Info($"DeckEditor: section {s} is empty (deck={m_deckCards.Length} lib={m_libCount} holding={m_holding}) → entered {fallback}");
            s = fallback;
        }
        m_section = s;
        switch (s)
        {
            case Section.Top: m_topNav.FocusFirst(announce: true, interruptPrior); break;
            case Section.Tabs: m_tabsNav.FocusFirst(announce: true, interruptPrior); break;
            case Section.Deck:
                if (m_deckCards.Length == 0)
                {
                    // Empty deck (reachable while holding a card to add): say so; Enter then places the held card.
                    m_deckIndex = -1;
                    AccessibilityMgr.Output(this, LocalizationUtils.Format(LocalizationKey.COLLECTION_EMPTY, DeckGroupName()), interruptPrior);
                }
                else
                {
                    AccessibilityMgr.Output(this, DeckGroupName(), interruptPrior);   // section name, then the card queues
                    DeckFocus(0, announce: true, interrupt: false);
                }
                break;
            case Section.Library:
                SyncLibCount();
                if (m_libCount > 0)
                {
                    m_libAwaitingLoad = false;
                    AccessibilityMgr.Output(this, LibGroupName(), interruptPrior);
                    LibFocus(0, announce: true, interrupt: false);
                }
                else
                {
                    m_libAwaitingLoad = true;
                    AccessibilityMgr.Output(this, LocalizationUtils.Format(LocalizationKey.COLLECTION_LOADING, LibGroupName()), interruptPrior);
                }
                break;
        }
    }

    private void ReAnnounceCurrent(bool interrupt)
    {
        switch (m_section)
        {
            case Section.Top: m_topNav.RefocusCurrent(); break;
            case Section.Tabs: m_tabsNav.RefocusCurrent(); break;
            case Section.Deck: if (m_deckIndex >= 0) ReadCard(interrupt); else EnterSection(Section.Deck, interrupt); break;
            case Section.Library: if (m_libIndex >= 0) ReadCard(interrupt); else EnterSection(Section.Library, interrupt); break;
        }
    }

    // Section names from the game's own headers ("CARD LIBRARY" banner, the deck panel).
    private static string LibGroupName() => LocalizationUtils.GetGame("deck_card_library", LocalizationKey.COLLECTION_CARD_LIBRARY);

    private static string DeckGroupName() => LocalizationUtils.GetGame("deck_deck", LocalizationKey.COLLECTION_DECK);

    // ── Card groups (data nav) ──

    private void HandleCardGroup(Section group)
    {
        if (AccessibleKey.READ_NEXT_ITEM.IsPressed()) { MoveCard(group, +1); return; }
        if (AccessibleKey.READ_PREV_ITEM.IsPressed()) { MoveCard(group, -1); return; }
        if (AccessibleKey.INSPECT_CARD.IsPressed()) { Inspect(group); return; }
        if (AccessibleKey.CONFIRM.IsPressed())
        {
            if (m_holding) TryPlace(group);
            else PickUp(group);
            return;
        }
        m_card.HandleInput();   // Down / Up / Shift+Up / Shift+Down: the card's detail lines
    }

    private void MoveCard(Section group, int dir)
    {
        if (group == Section.Deck)
        {
            var next = m_deckIndex + dir;
            if (next >= 0 && next < m_deckCards.Length) DeckFocus(next, announce: true);
            return;
        }
        var live = SyncLibCount();
        var target = m_libIndex + dir;
        if (live == 0 || target < 0 || target >= live) return;
        var drawer = m_controller!.CollectionDrawer;
        var kv = dir > 0 ? drawer.GetNextCard(m_libCurrent ?? default, m_libIndex) : drawer.GetPreviousCard(m_libCurrent ?? default, m_libIndex);
        m_libCurrent = kv.Key;
        m_libIndex = kv.Value;
        ReadCard(interrupt: true);
    }

    private void DeckFocus(int index, bool announce, bool interrupt = true)
    {
        if (index < 0 || index >= m_deckCards.Length) { m_deckIndex = -1; return; }
        m_deckIndex = index;
        if (announce) ReadCard(interrupt);
    }

    private void LibFocus(int index, bool announce, bool interrupt = true)
    {
        var live = SyncLibCount();
        if (live == 0) { m_libIndex = -1; return; }
        // Step to the requested index from index 0 (only for the first focus; movement steps incrementally).
        // GetNextCard(default, -1) yields the collection's first card.
        var drawer = m_controller!.CollectionDrawer;
        var kv = drawer.GetNextCard(default, -1);
        m_libCurrent = kv.Key;
        m_libIndex = kv.Value;
        for (var i = 0; i < index && m_libIndex < live - 1; i++)
        {
            kv = drawer.GetNextCard(m_libCurrent ?? default, m_libIndex);
            m_libCurrent = kv.Key;
            m_libIndex = kv.Value;
        }
        if (announce) ReadCard(interrupt);
    }

    private void RefreshDeck()
    {
        var deck = m_controller != null ? m_controller.DataDeck : null;
        m_deckCards = deck != null ? CardSortHelper.SortDeck(deck, m_categorySelector != null ? m_categorySelector.CurrentVal : CardCategory.All) : Array.Empty<ArchetypeDBCards>();
        if (m_deckIndex >= m_deckCards.Length) m_deckIndex = m_deckCards.Length - 1;
    }

    // The drawer rebuilds its collection inside a coroutine: PostProcessDataTableQuery swaps in an EMPTY list, fills it,
    // then yields a frame before _onCollectionChanged reports the new total. Across that window a cached count still
    // holds the PREVIOUS filter's count, while the drawer's own GetNextCard indexes `(current + 1) % _curCollection.Count`
    // — so authorizing a move against the cached count during a category / filter / search change divides by zero inside
    // the game's code. Every index must be authorized against the LIVE count; this re-reads it.
    private int SyncLibCount()
    {
        m_libCount = m_controller != null && m_controller.CollectionDrawer != null ? m_controller.CollectionDrawer.GetCurrentCollectionAmount() : 0;
        if (m_libIndex >= m_libCount) m_libIndex = m_libCount - 1;
        return m_libCount;
    }

    private ArchetypeDBCards? CurrentStack()
    {
        if (m_section == Section.Deck) return m_deckIndex >= 0 && m_deckIndex < m_deckCards.Length ? m_deckCards[m_deckIndex] : null;
        if (m_section == Section.Library) return m_libIndex >= 0 && m_libCount > 0 ? m_libCurrent : null;
        return null;
    }

    private void ReadCard(bool interrupt)
    {
        if (CurrentStack() is not { } stack) return;
        m_card.Reset();
        var deck = m_section == Section.Deck;
        AccessibilityMgr.Output(this, Announce.Item(CardLabel(stack), null, (deck ? m_deckIndex : m_libIndex) + 1, deck ? m_deckCards.Length : m_libCount), interrupt);
    }

    // "Charizard ex, Stage 2 Pokémon, 1 in deck, 4 owned" — name+type from the printed card, plus the counts a sighted
    // player reads off the badges: how many are in the deck and how many you OWN (TotalOwnedQuantity counts every
    // printing). A card you don't own reads "not owned"; a deck that uses more copies than you own (legal to build, but
    // you can't play it ranked) reads "you don't own enough" — the missing flag the player couldn't see.
    //
    // Ownership is PER VERSION (a card's rarities are distinct CardIDs with separate owned counts), so the archetype
    // totals can BOTH look fine yet the deck still be invalid — e.g. you own 1 Budew of each of two rarities (2 owned)
    // and put 2 of the SAME rarity in the deck. HasUnownedVersionInDeck catches that hidden case.
    private static string CardLabel(ArchetypeDBCards stack)
    {
        var parts = new List<string> { CardName(stack) };
        var inDeck = stack.TotalDeckQuantity();
        var owned = stack.TotalOwnedQuantity();
        if (inDeck > 0) parts.Add(LocalizationUtils.Format(LocalizationKey.COLLECTION_IN_DECK, inDeck));
        if (owned <= 0) parts.Add(LocalizationUtils.Get(LocalizationKey.CARD_NOT_OWNED));
        else
        {
            parts.Add(LocalizationUtils.Format(LocalizationKey.CARD_OWNED, owned));
            if (inDeck > owned) parts.Add(LocalizationUtils.Get(LocalizationKey.COLLECTION_NOT_ENOUGH_OWNED));
            else if (HasUnownedVersionInDeck(stack)) parts.Add(LocalizationUtils.Get(LocalizationKey.COLLECTION_NOT_ENOUGH_OF_VERSION));
        }
        return string.Join(", ", parts);
    }

    private static string CardName(ArchetypeDBCards stack) => Row(stack) is { } row ? CardText.NameWithType(row) : stack.LocalizedCardName();

    private static CardDataRow? Row(ArchetypeDBCards stack)
    {
        var card = stack.GetPreferredDisplayCard();
        return card != null ? ManagerSingleton<CardDatabaseManager>.instance?.TryGetCardFromDatabase(card.CardID) : null;
    }

    // True when any single rarity version in the deck uses more copies than you own of THAT exact version — the
    // per-CardID check the game's validator runs, which archetype totals can mask.
    private static bool HasUnownedVersionInDeck(ArchetypeDBCards stack)
    {
        foreach (var c in stack.Cards)
        {
            if (c == null) continue;
            var inDeck = stack.TotalDeckQuantityForSpecificCard(c.CardID);
            if (inDeck > 0 && stack.TotalOwnedQuantityForSpecificCard(c.CardID) < inDeck) return true;
        }
        return false;
    }

    // The focused card read line by line: its label, then the printed detail lines.
    private sealed class CardLines : AccessibleItem
    {
        private readonly AccessibleEditDeck m_owner;

        public CardLines(AccessibleEditDeck owner) : base(owner) => m_owner = owner;

        protected internal override List<string> GetLines()
        {
            var lines = new List<string>();
            if (m_owner.CurrentStack() is not { } stack) return lines;
            lines.Add(CardLabel(stack));
            if (Row(stack) is { } row) lines.AddRange(CardText.DetailLines(row));
            return lines;
        }
    }

    // ── Pick up → navigate → place (the proper drag) ──
    // Pickup records the card's DATA row (synchronous — no scroller realization). Place calls AddCard/RemoveCard, exactly
    // what DeckEditorCardDropTarget.OnDrop does, and during the tutorial also fires the ftueDrop the drop would have.

    private void PickUp(Section group)
    {
        if (CurrentStack() is not { } stack) return;
        var action = group == Section.Deck ? CardEditorDrag.CardDropAction.Remove : CardEditorDrag.CardDropAction.Add;
        var card = group == Section.Deck ? stack.GetPreferredDisplayCardForDeck() ?? stack.GetPreferredDisplayCard() : stack.GetPreferredDisplayCard();
        var name = stack.LocalizedCardName();
        if (card == null)
        {
            AccessibilityMgr.Output(this, LocalizationUtils.Format(LocalizationKey.COLLECTION_CANT_PICK_UP, name));
            return;
        }
        m_heldCard = card;
        m_heldAction = action;
        m_heldFrom = group;
        m_heldName = name;
        m_holding = true;
        HUBEditDeckController.IsEditorDragging = true;   // match the game's drag state
        AccessibilityMgr.Output(this, LocalizationUtils.Format(LocalizationKey.COLLECTION_HOLDING, name));
    }

    private void TryPlace(Section destination)
    {
        // A library card (Add) goes onto the Deck; a deck card (Remove) goes onto the Library. Placing it back where it
        // came from cancels.
        var add = m_heldAction == CardEditorDrag.CardDropAction.Add;
        if ((add && destination != Section.Deck) || (!add && destination != Section.Library))
        {
            if (destination == m_heldFrom) { CancelHold(announce: true); return; }
            AccessibilityMgr.Output(this, LocalizationUtils.Format(add ? LocalizationKey.COLLECTION_TAB_TO_DECK : LocalizationKey.COLLECTION_TAB_TO_LIBRARY, m_heldName));
            return;
        }

        var card = m_heldCard;
        var name = m_heldName;
        var from = m_heldFrom;
        if (card == null) { CancelHold(announce: true); return; }
        var ftueDrop = ArmedFtueDropTarget();
        var canAdd = !add || m_controller!.CanAddCard(card);

        // The game's own gate said no (deck full / copy limit / not owned). Outside the tutorial, report and stop — the
        // deck didn't change. During the tutorial we must NOT stop here: DeckEditorCardDropTarget.OnDrop fires ftueDrop()
        // (the advance) even when CanAddCard is false, so the drop GESTURE advances the FTUE regardless. Bailing out
        // would leave the sealed tutorial waiting on an advance that never comes.
        if (!canAdd && ftueDrop == null)
        {
            ClearHold();
            m_section = from;
            AccessibilityMgr.Output(this, LocalizationUtils.Format(LocalizationKey.COLLECTION_CANT_ADD, name, FullDeckHint()));
            ReAnnounceCurrent(interrupt: false);
            return;
        }

        if (add) { if (canAdd) m_controller!.AddCard(card, focusOnAddedCard: true); }
        else m_controller!.RemoveCard(card);
        if (ftueDrop != null) CompleteFtueDrop(ftueDrop);

        ClearHold();
        // Full rebuild (not just the counts): the deck's unowned set changed, so the Top section may lose the Exchange-all
        // button. It comes BACK asynchronously, and the LabeledPurchaseButton.Setup hook re-runs the section then.
        RebuildSections();
        m_section = from;
        if (!canAdd) AccessibilityMgr.Output(this, LocalizationUtils.Format(LocalizationKey.COLLECTION_COULDNT_ADD, name, FullDeckHint()));
        else AccessibilityMgr.Output(this, LocalizationUtils.Format(add ? LocalizationKey.COLLECTION_ADDED : LocalizationKey.COLLECTION_REMOVED, name, InDeckCount(name)));
        ReAnnounceCurrent(interrupt: false);
    }

    private string FullDeckHint()
    {
        var deck = m_controller?.DataDeck?.WorkingDeck;
        var max = ManagerSingleton<DeckValidationManager>.instance?.GetDeckSizeForGameMode(DeckEditorGameMode.CurrentGameMode) ?? 60;
        return deck != null && deck.size >= max ? LocalizationUtils.Get(LocalizationKey.COLLECTION_DECK_FULL) : "";
    }

    private int InDeckCount(string name)
    {
        foreach (var s in m_deckCards)
            if (string.CompareOrdinal(s.LocalizedCardName(), name) == 0) return s.TotalDeckQuantity();
        return 0;
    }

    // The drop target the tutorial armed with its ftueDrop callback, if any — only the FTUE arms one.
    private static DeckEditorCardDropTarget? ArmedFtueDropTarget()
    {
        if (s_hasFtueDrop == null) return null;
        foreach (var dt in Resources.FindObjectsOfTypeAll<DeckEditorCardDropTarget>())
            if (s_hasFtueDrop.GetValue(dt) is true) return dt;
        return null;
    }

    // The tutorial armed a drop target with an ftueDrop callback (Unfocus + SaveInventory + indicator.Unblock) that
    // OnDrop fires AFTER AddCard/RemoveCard to advance the FTUE. We call AddCard/RemoveCard directly, so replicate that
    // tail on the armed target — else the deck edits but the tutorial freezes.
    private static void CompleteFtueDrop(DeckEditorCardDropTarget dt)
    {
        (s_ftueDrop?.GetValue(dt) as UnityAction)?.Invoke();
        s_hasFtueDrop?.SetValue(dt, false);
        s_ftueDrop?.SetValue(dt, null);
        if (s_originalParent?.GetValue(dt) is Transform orig && orig != null) dt.transform.SetParent(orig);
        Log.Info("DeckEditor: fired FTUE drop advance");
    }

    private void CancelHold(bool announce)
    {
        var name = m_heldName;
        ClearHold();
        if (announce) AccessibilityMgr.Output(this, LocalizationUtils.Format(LocalizationKey.COLLECTION_HOLD_CANCELLED, name));
    }

    private void ClearHold()
    {
        if (m_holding) HUBEditDeckController.IsEditorDragging = false;
        m_holding = false;
        m_heldCard = null;
        m_heldName = "";
    }

    // ── Inspect (the second method: the game's real card-detail overlay) ──

    private void Inspect(Section group)
    {
        if (m_holding) { AccessibilityMgr.Output(this, LocalizationUtils.Get(LocalizationKey.COLLECTION_PLACE_FIRST)); return; }
        if (CurrentStack() is not { } stack || stack.GetPreferredDisplayCard() is not { } card) return;

        // Open the overlay exactly the way a click does — invoke the realized stub's cardButton (AccessibleCardDetail
        // opens itself off the overlay's Setup hook).
        if (group == Section.Deck)
        {
            ClickStub(m_controller!.DeckPanel.GetCardButton(card.CardID));
            return;
        }
        // 1.5.0 removed PopulateCollectionDrawer.GetCardButton(id, cb); ScrollToCard(id, cb) is the replacement — it
        // scrolls the (virtualized) collection to the card and hands back its realized CardStubParts.
        m_controller!.CollectionDrawer.ScrollToCard(card.CardID, ClickStub);
    }

    private void ClickStub(CardStubParts? stub)
    {
        if (stub != null && stub.CardButton != null) stub.CardButton.onClick.Invoke();
        else AccessibilityMgr.Output(this, LocalizationUtils.Get(LocalizationKey.COLLECTION_CANT_OPEN_DETAILS));
    }

    // ── Tutorial guidance ──

    private static bool FtueCardStep => FtueTracker.FtueIsRunning && !string.IsNullOrEmpty(FtueTracker.DeckEditorFtueCardId);

    private void UpdateFtueGuidance()
    {
        if (!FtueTracker.FtueIsRunning) { m_lastFtueCardId = ""; m_ftueFocusShown = null; return; }

        var cardId = FtueTracker.DeckEditorFtueCardId;
        if (!string.IsNullOrEmpty(cardId))
        {
            m_ftueFocusShown = null;
            if (cardId == m_lastFtueCardId) return;
            m_lastFtueCardId = cardId!;
            var jumped = JumpToCard(cardId!);
            Log.Info($"DeckEditor FTUE target: element={FtueTracker.DeckEditorFtueElement} card={cardId} jumped={jumped}");
            if (jumped) AccessibilityMgr.Output(this, LocalizationUtils.Format(LocalizationKey.COLLECTION_FTUE_THIS_CARD, FtueVerb()), interrupt: false);
            else AnnounceFtueCardStep();
            return;
        }

        // Non-card step: present the coached control directly for Enter — the same real control a sighted player clicks.
        m_lastFtueCardId = "";
        var focus = FtueTracker.FocusedElement;
        if (focus == null || !focus.isActiveAndEnabled) { m_ftueFocusShown = null; return; }
        if (focus == m_ftueFocusShown) return;
        m_ftueFocusShown = focus;
        m_ftueNav.RadioToggleMode = focus is Toggle;
        m_ftueNav.SetItems(new List<Selectable> { focus });
        AnnounceFtueFocus();
    }

    private void AnnounceFtueFocus()
    {
        if (m_ftueFocusShown == null) return;
        AccessibilityMgr.Output(this, LocalizationUtils.Format(LocalizationKey.COLLECTION_FTUE_USE, KeyboardNavigator.DefaultLabel(m_ftueFocusShown)), interrupt: true);
        m_ftueNav.FocusFirst(announce: false, interruptPrior: false);
    }

    private void AnnounceFtueCardStep()
    {
        var cardId = FtueTracker.DeckEditorFtueCardId;
        var row = !string.IsNullOrEmpty(cardId) ? ManagerSingleton<CardDatabaseManager>.instance?.TryGetCardFromDatabase(cardId) : null;
        AccessibilityMgr.Output(this, LocalizationUtils.Format(LocalizationKey.COLLECTION_FTUE_CARD, FtueVerb(), row != null ? row.LocalizedCardName : cardId ?? ""), interrupt: true);
    }

    // The edit the tutorial asks for, named by its own indicator element.
    private static CardEditorDrag.CardDropAction? FtueExpectedAction() => FtueTracker.DeckEditorFtueElement switch
    {
        "LibraryCard" or "AddCard" or "OpenableLibraryCard" => CardEditorDrag.CardDropAction.Add,
        "DeckCard" or "RemoveCard" => CardEditorDrag.CardDropAction.Remove,
        _ => null,
    };

    private static string FtueVerb() => LocalizationUtils.Get(FtueTracker.DeckEditorFtueElement switch
    {
        "LibraryCard" or "AddCard" => LocalizationKey.COLLECTION_FTUE_ADD,
        "DeckCard" or "RemoveCard" => LocalizationKey.COLLECTION_FTUE_REMOVE,
        "OpenableLibraryCard" => LocalizationKey.COLLECTION_FTUE_OPEN,
        _ => LocalizationKey.COLLECTION_FTUE_SELECT,
    });

    // The tutorial NAMES the list its card lives in: search only that list (searching the deck for a card the tutorial
    // wants ADDED landed on the deck's existing copy, and Enter then removed one instead).
    private bool JumpToCard(string cardId)
    {
        var row = ManagerSingleton<CardDatabaseManager>.instance?.TryGetCardFromDatabase(cardId);
        if (row == null) return false;
        return FtueExpectedAction() switch
        {
            CardEditorDrag.CardDropAction.Add => TryJumpLibrary(row),
            CardEditorDrag.CardDropAction.Remove => TryJumpDeck(row),
            _ => TryJumpDeck(row) || TryJumpLibrary(row),
        };
    }

    private bool TryJumpDeck(CardDataRow row)
    {
        for (var i = 0; i < m_deckCards.Length; i++)
        {
            bool has;
            try { has = m_deckCards[i].HasCard(row); } catch { has = false; }
            if (!has) continue;
            m_section = Section.Deck;
            AccessibilityMgr.Output(this, DeckGroupName(), interrupt: false);
            DeckFocus(i, announce: true, interrupt: false);
            return true;
        }
        return false;
    }

    private bool TryJumpLibrary(CardDataRow row)
    {
        if (m_controller?.CollectionDrawer == null) return false;
        var idx = m_controller.CollectionDrawer.GetCardIndex(row.ArchetypeID);
        if (idx < 0 || idx >= SyncLibCount()) return false;
        m_section = Section.Library;
        AccessibilityMgr.Output(this, LibGroupName(), interrupt: false);
        LibFocus(idx, announce: true, interrupt: false);
        return true;
    }

    // ── Intro / back ──

    private void AnnounceIntro()
    {
        var deck = m_controller?.DataDeck?.WorkingDeck;
        if (deck == null) { AccessibilityMgr.Output(this, LocalizationUtils.Get(LocalizationKey.COLLECTION_DECK_EDITOR), interrupt: true); return; }
        var max = ManagerSingleton<DeckValidationManager>.instance?.GetDeckSizeForGameMode(DeckEditorGameMode.CurrentGameMode) ?? 60;
        var valid = m_controller!.DataDeck.IsValidQuick(DeckEditorGameMode.CurrentGameMode);
        var name = string.IsNullOrEmpty(deck.deckName) ? LocalizationUtils.Get(LocalizationKey.COLLECTION_DECK) : deck.deckName;
        AccessibilityMgr.Output(this, LocalizationUtils.Format(valid ? LocalizationKey.COLLECTION_EDITOR_INTRO_VALID : LocalizationKey.COLLECTION_EDITOR_INTRO_INVALID, name, deck.size, max), interrupt: true);
    }

    // Back out of the editor to DECKS. Drives the controller's own exit (HideScreen → navigation.GoToPreviousScreen),
    // gated by CanExit — an invalid deck shows the game's own "leave anyway?" validation popup (which runs the exit on
    // confirm); a valid deck leaves at once.
    private void DoBack()
    {
        // During the tutorial's card step the game's blocker forbids leaving (backing out stranded the FTUE waiting on a
        // drop that could no longer happen), so restate the objective instead of eating the key silently.
        if (FtueCardStep) { AnnounceFtueCardStep(); ReAnnounceCurrent(interrupt: false); return; }
        var controller = m_controller;
        if (controller == null || s_hideScreen == null) return;
        Action leave = () => s_hideScreen.Invoke(controller, null);
        if (controller.CanExit(leave)) leave();
    }

    // Is the deck editor still the HUB's current screen? Authoritative source is the HUB navigation's currentScreen; a
    // hidden GameObject is a secondary signal. True when unreadable, so a live editor is never dropped by mistake.
    private bool IsCurrentHubScreen()
    {
        if (m_controller == null) return false;
        var nav = ManagerSingleton<RainierManager>.instance?._mainMenuController?.navigation;
        if (nav != null && nav.currentScreen != null && nav.currentScreen != m_controller) return false;
        return m_controller.gameObject.activeInHierarchy;
    }

    // The category toggle's only visible text is its COUNT badge (e.g. "13"), so reading the text gave just a number.
    // Speak the category NAME from the filter tab's loc id, with the count as a qualifier: "Pokémon, 13 cards, selected".
    private string CategoryLabel(Selectable s)
    {
        string? name = null;
        if (m_categorySelector != null)
            foreach (var tab in m_categorySelector.AllTabs)
                if (tab?.Toggle != null && tab.Toggle.MyToggle == s) name = LocalizationUtils.TryGetGame(tab.FilterName)?.Trim();
        var count = CollectionUi.Text(s.GetComponentInChildren<TMP_Text>(includeInactive: false));
        var label = name ?? count ?? KeyboardNavigator.ResolveBaseLabel(s);
        if (name != null && count != null) label = LocalizationUtils.Format(LocalizationKey.COMMON_STATE_FORMAT, name, LocalizationUtils.Format(LocalizationKey.COLLECTION_CARDS, count));
        return CollectionUi.TabState(label, s);
    }

    // ── Sort / filter popup (modal) ──

    // Drive the modal sort/filter mode while a popup is open; returns true to consume the frame so the section nav and
    // Tab stay inert. Escape closes the popup via its own Hide().
    private bool HandleSortMode()
    {
        var sort = ReadField<AnimatedSetActive>(m_controller, "landscapeSortingOverlay");
        var filter = ReadField<AnimatedSetActive>(m_controller, "landscapeFilterOverlay");
        var popup = sort != null && sort.gameObject.activeInHierarchy ? sort : filter != null && filter.gameObject.activeInHierarchy ? filter : null;
        if (popup == null)
        {
            if (m_sortPopup == null) return false;
            m_sortPopup = null;
            ReAnnounceCurrent(interrupt: true);
            return false;
        }
        if (popup != m_sortPopup)
        {
            m_sortPopup = popup;
            var items = CollectionUi.InReadingOrder(popup.GetComponentsInChildren<Selectable>(includeInactive: false)
                .Where(s => (s is Toggle || s is Button) && s.isActiveAndEnabled && s.IsInteractable()));
            m_sortNav.SetItems(items);
            AccessibilityMgr.Output(this, LocalizationUtils.Get(popup == filter ? LocalizationKey.COLLECTION_FILTER : LocalizationKey.COLLECTION_SORT_BY), interrupt: true);
            m_sortNav.FocusFirst(announce: true, interruptPrior: false);
        }
        if (CollectionUi.BackPressed()) { popup.Hide(); return true; }   // the mode ends next frame once the popup is gone
        m_sortNav.HandleInput();
        return true;
    }

    // ── Hooks ──

    private void Activated(HUBEditDeckController controller)
    {
        var wasCurrent = AccessibilityMgr.CurrentScreen == this && m_controller == controller;
        m_controller = controller;
        m_notCurrentFrames = 0;
        if (wasCurrent) return;
        m_fresh = true;
        m_sortPopup = null;
        AccessibilityMgr.SetScreen(this);
    }

    private void Deactivated(HUBEditDeckController controller)
    {
        if (controller != m_controller) return;
        ClearHold();
        m_controller = null;
        AccessibilityMgr.ClearScreen(this);
    }

    [HarmonyPatch(typeof(HUBEditDeckController), "OnActivate")]
    private static class OnActivateHook
    {
        private static void Postfix(HUBEditDeckController __instance) => s_instance.Activated(__instance);
    }

    [HarmonyPatch(typeof(HUBEditDeckController), "OnDeactivate")]
    private static class OnDeactivateHook
    {
        private static void Postfix(HUBEditDeckController __instance) => s_instance.Deactivated(__instance);
    }

    // The game just gave the "Exchange all unowned" button its label, cost and onClick — it is on screen now. Re-read the
    // Top section so the option is there before the player arrives at it.
    [HarmonyPatch(typeof(LabeledPurchaseButton), "Setup")]
    private static class PurchaseButtonSetupHook
    {
        private static void Postfix()
        {
            if (s_instance.m_controller != null) s_instance.RebuildTopNav();
        }
    }
}
