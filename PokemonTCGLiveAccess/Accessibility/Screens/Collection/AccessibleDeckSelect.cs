using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using SharedSDKUtils;
using TMPro;
using TPCI.Rainier.Features.DeckManager;
using UnityEngine;
using UnityEngine.UI;
using static Accessibility.GameReflect;

namespace Accessibility;

// DECKS screen (the deck list / gateway to the editor). Driver: HUBDeckSelectController (a HUBScreenController,
// hook OnActivate/OnDeactivate). Sections, top-to-bottom:
//   • Top    — HUB tab strip (HOME / DECKS / …) + ⋮.
//   • Tabs   — deck grouping RECENTS / FAVORITES / ALL (DeckGroupingToggleGroup toggles).
//   • Decks  — CREATE A DECK + the deck tiles (DeckSelectEntry). Focusing a tile SELECTS it (the game's own
//              ReportDeckOptionClicked → updates the detail panel), and reads the deck name + valid/invalid + active
//              — navigated by data over deckList.ActiveDeckEntries.
//   • Detail — the selected deck's panel (DeckPanel_L): EDIT DECK / SET ACTIVE / TEST / DUPLICATE / EXPORT / DELETE /
//              rename / favorite. EDIT DECK opens the editor (AccessibleEditDeck).
//   • Filters — the format toggle, SHOW VALID ONLY and the search box. Each of them, and the grouping tabs, re-lists the
//              decks, so the list is compared with the game's every frame.
internal sealed class AccessibleDeckSelect : AccessibleScreen
{
    private enum Section { Top, Tabs, Filters, Decks, Detail }

    private static readonly AccessibleDeckSelect s_instance = new();

    private static readonly MethodInfo? s_openDeckEditorForDeck = AccessTools.Method(typeof(HUBDeckSelectController), "OpenDeckEditorForDeck");

    private readonly KeyboardNavigator m_topNav;
    private readonly KeyboardNavigator m_tabsNav;
    private readonly KeyboardNavigator m_detailNav;
    private readonly KeyboardNavigator m_filterNav;
    private HUBDeckSelectController? m_controller;
    private ExpandableSearchBar? m_search;
    private TMP_InputField? m_searchField;
    private bool m_searchOpened;
    private string m_entriesSig = "";
    private bool m_fresh;

    private Section m_section = Section.Decks;
    private Button? m_setActiveBtn;   // the live SET ACTIVE button (only for a non-active valid deck), matched by identity
    private Button? m_renameBtn;      // the ✎ pencil (DeckPanel_L.editDeckNameButton); runtime-wired, so labelled by identity
    private Button? m_createButton;   // "CREATE A DECK", the deck grid's leftmost top item

    // Challenge-deck-selection mode: the deck screen reached from a friend battle request. The controller shows
    // returnToFriendJoinMatchButton (active only then), and LeaveDeckManager() routes back to the challenge through it.
    // We surface that button + make Escape return to the challenge (instead of opening the ⋮ menu).
    private Button? m_returnButton;
    private bool m_challengeMode;

    private readonly List<DeckSelectEntry> m_entries = new();
    private int m_deckIndex;   // -1 = the synthetic "Create a deck" item, 0..n = deck tiles

    private AccessibleDeckSelect()
    {
        m_topNav = new KeyboardNavigator(this) { AllowTab = false };
        m_tabsNav = new KeyboardNavigator(this) { AllowTab = false, RadioToggleMode = true };
        m_detailNav = new KeyboardNavigator(this) { AllowTab = false };
        m_filterNav = new KeyboardNavigator(this) { AllowTab = false };
    }

    public void HandleInput()
    {
        if (m_controller == null) return;
        SyncEntries();

        // In the search field Escape must leave the field, not open the menu; Enter goes to the results.
        if (m_section == Section.Filters && m_filterNav.CurrentIsTextField)
        {
            if (!m_searchOpened) { m_searchOpened = true; m_search?.OpenSearch(); }
            if (AccessibleKey.ESCAPE.IsPressed() || AccessibleKey.CONFIRM.IsPressed()) { EnterSection(Section.Decks, interruptPrior: true); return; }
        }
        else if (AccessibleKey.READ_DECK_VALIDITY.IsPressed() && !KeyboardNavigator.IsTextInputFocused()) { ReadPanel(); return; }

        // Escape: in challenge-deck-selection it returns to the battle request (the game's own LeaveDeckManager →
        // returnToFriendJoinMatchButton); otherwise it opens the ⋮ menu (the deck list has no other "back" — the editor
        // is a separate screen).
        if (AccessibleKey.ESCAPE.IsPressed())
        {
            if (m_challengeMode) m_controller.LeaveDeckManager();
            else CollectionUi.OpenOverflowMenu();
            return;
        }
        if (AccessibleKey.NEXT_SECTION.IsPressed()) { CycleSection(+1); return; }
        if (AccessibleKey.PREV_SECTION.IsPressed()) { CycleSection(-1); return; }

        switch (m_section)
        {
            case Section.Top: HandleStrip(m_topNav, null, Section.Tabs); break;
            case Section.Tabs: HandleStrip(m_tabsNav, Section.Top, Section.Decks); break;
            case Section.Filters:
                if (AccessibleKey.CONFIRM.IsPressed() && m_filterNav.Current is Toggle) { m_filterNav.Activate(isSpaceKey: true); break; }
                m_filterNav.HandleInput();
                break;
            // The deck-options panel is a flat vertical list of deck actions: arrows STAY within it (no edge-escape —
            // it's a self-contained menu).
            case Section.Detail: m_detailNav.HandleInput(); break;
            case Section.Decks: HandleDecks(); break;
        }
    }

    public string GetHelp() => LocalizationUtils.Get(LocalizationKey.COLLECTION_DECKS_HELP);

    public void OnGainedFocus()
    {
        if (m_controller == null) return;
        Rebuild();
        if (m_fresh)
        {
            m_fresh = false;
            AccessibilityMgr.Output(this, LocalizationUtils.Get(LocalizationKey.COLLECTION_DECKS), interrupt: true);
            EnterSection(Section.Decks, interruptPrior: false);
            return;
        }
        // Back from the ⋮ menu / a popup: the exact prior position (Rebuild kept each navigator's index).
        switch (m_section)
        {
            case Section.Top: m_topNav.RefocusCurrent(); break;
            case Section.Tabs: m_tabsNav.RefocusCurrent(); break;
            case Section.Filters: m_filterNav.RefocusCurrent(); break;
            case Section.Detail: m_detailNav.RefocusCurrent(); break;
            case Section.Decks: AnnounceDeck(interrupt: true); break;
        }
    }

    // ── Sections ──

    private void Rebuild()
    {
        if (m_controller == null) return;

        // Challenge mode: surface the "return to the battle request" button (active only in this mode) at the front of
        // the top strip so it's reachable, and remember we're in challenge mode so Escape goes back to the challenge.
        var top = CollectionUi.TopStrip();
        m_returnButton = ReadField<Button>(m_controller, "returnToFriendJoinMatchButton");
        m_challengeMode = m_returnButton != null && m_returnButton.isActiveAndEnabled;
        if (m_challengeMode) top.Insert(0, m_returnButton!);
        SetItemsKeepingIndex(m_topNav, top, TopLabel);
        m_topNav.ForceFlatHorizontal();   // strip: Left/Right within, Up/Down escapes sections

        // Grouping tabs (RECENTS / FAVORITES / ALL).
        var grouping = ReadField<Component>(m_controller, "deckGroupingToggleGroup");
        var toggles = grouping != null
            ? CollectionUi.InReadingOrder(grouping.GetComponentsInChildren<Toggle>(includeInactive: false).Where(t => t.isActiveAndEnabled))
            : new List<Selectable>();
        SetItemsKeepingIndex(m_tabsNav, toggles, ToggleLabel);
        m_tabsNav.ForceFlatHorizontal();
        m_tabsNav.GroupName = LocalizationUtils.Get(LocalizationKey.COLLECTION_DECK_GROUPING);

        RebuildFilters();
        RebuildDetail();

        // CREATE A DECK — read the game's own create-deck prefab button (DeckSelectList.createDeckPrefab →
        // GetComponentInChildren<Button>, the exact button the game wires to CreateNewDeckButtonActions). A game update
        // moved/restructured that prefab so the old "scan deckSelectGrid for a button not inside a DeckSelectEntry"
        // stopped finding it, and the button vanished from nav. (That scan stays as the fallback.)
        m_createButton = null;
        var deckList = m_controller.GetDeckList();
        var createPrefab = ReadField<GameObject>(deckList, "createDeckPrefab");
        if (createPrefab != null) m_createButton = createPrefab.GetComponentInChildren<Button>(includeInactive: true);
        if (m_createButton == null)
        {
            var grid = ReadField<Component>(m_controller, "deckSelectGrid");
            if (grid != null)
                m_createButton = grid.GetComponentsInChildren<Button>(includeInactive: false).FirstOrDefault(b => b.GetComponentInParent<DeckSelectEntry>() == null);
        }

        m_entries.Clear();
        if (deckList != null) m_entries.AddRange(deckList.ActiveDeckEntries.Where(e => e != null));
        if (m_deckIndex >= m_entries.Count) m_deckIndex = m_entries.Count - 1;
        m_entriesSig = EntriesSig();
    }

    // Format button, valid-only toggle, then the search field last: inside a text field the arrows move the caret, so
    // anything after it would be stranded.
    private void RebuildFilters()
    {
        var items = new List<Selectable>();
        var format = ReadField<ChangeGameModeButton>(m_controller, "headerChangeGameModeButton");
        var formatButton = format != null ? format.GetComponentInChildren<Button>(includeInactive: false) ?? format.GetComponentInParent<Button>() : null;
        if (formatButton != null && formatButton.isActiveAndEnabled) items.Add(formatButton);
        var validOnly = ReadField<SilencableToggle>(m_controller, "validOnlyToggle");
        if (validOnly != null && validOnly.MyToggle != null && validOnly.MyToggle.isActiveAndEnabled) items.Add(validOnly.MyToggle);
        m_search = ReadField<ExpandableSearchBar>(m_controller, "deckSearchBar");
        m_searchField = CollectionUi.SearchField(m_search);
        if (m_searchField != null && m_searchField.isActiveAndEnabled) items.Add(m_searchField);
        SetItemsKeepingIndex(m_filterNav, items, s => KeyboardNavigator.DefaultLabel(s));
        m_filterNav.ForceFlatVertical();
        m_filterNav.GroupName = LocalizationUtils.Get(LocalizationKey.COLLECTION_FILTER);
    }

    private string EntriesSig()
    {
        var list = m_controller?.GetDeckList();
        return list == null ? "" : string.Join(",", list.ActiveDeckEntries.Where(e => e != null).Select(e => e.GetInstanceID()));
    }

    // Grouping, format, valid-only and search all re-list the decks asynchronously: follow, keep the cursor on the same
    // deck when it survived, and say how many are left.
    private void SyncEntries()
    {
        var sig = EntriesSig();
        if (sig == m_entriesSig) return;
        var current = m_deckIndex >= 0 && m_deckIndex < m_entries.Count ? m_entries[m_deckIndex] : null;
        m_entries.Clear();
        m_entries.AddRange(m_controller!.GetDeckList().ActiveDeckEntries.Where(e => e != null));
        m_entriesSig = sig;
        var kept = current != null ? m_entries.IndexOf(current) : -1;
        m_deckIndex = kept >= 0 ? kept : m_entries.Count > 0 ? 0 : -1;
        AccessibilityMgr.Output(this, LocalizationUtils.Format(LocalizationKey.COLLECTION_DECK_COUNT, m_entries.Count), interrupt: false);
    }

    // V: the selected deck as the panel shows it: name, format, card count, validity and the rule rows.
    private void ReadPanel()
    {
        var panel = m_controller!.GetDeckPanel();
        if (panel == null) return;
        var parts = new List<string>();
        if (CollectionUi.Text(ReadField<TextMeshProUGUI>(panel, "deckTitleText")) is { } title) parts.Add(title);
        if (CollectionUi.Text(ReadField<TPCI.Localization.LocText>(panel, "formatText")) is { } format) parts.Add(format);
        if (CollectionUi.Text(ReadField<TPCI.Localization.LocText>(panel, "deckCountText")) is { } count) parts.Add(count);
        if (CollectionUi.Text(ReadField<TPCI.Localization.LocText>(panel, "validityLocText")) is { } validity) parts.Add(validity);
        parts.AddRange(CollectionUi.ValidityRows(panel));
        AccessibilityMgr.Output(this, string.Join(". ", parts), interrupt: true);
    }

    private static void SetItemsKeepingIndex(KeyboardNavigator nav, List<Selectable> items, KeyboardNavigator.LabelResolver resolver)
    {
        var keep = nav.Index;
        nav.SetItems(items, resolver);
        if (keep >= 0 && keep < nav.Count) nav.FocusIndex(keep, announce: false);
    }

    // Detail panel = a flat vertical LIST of deck actions, in reading order:
    //   favorite ★ · rename ✎ · options ⋮ · SET ACTIVE · CUSTOMIZE · EDIT DECK · TEST DECK.
    // (The cards below are content — CardStubParts, excluded.) The old 2-D spatial nav made Left/Right jump to
    // unexpected options on this irregular layout, so it's a list.
    //
    // Re-scanned on every entry to Section.Detail, NOT once: the panel reflects the currently-selected deck, and SET
    // ACTIVE is only a live button for a non-active, valid deck. The active deck — which GetDefaultDeckEntry() picks as
    // the default selection — hides it (SetActiveDeckButtonParts.SetState(DISABLED) → activeState.SetActive(false)), so
    // a one-shot scan would capture the active deck's panel and permanently miss SET ACTIVE for every other deck. The
    // selection→panel update is synchronous, so the live hierarchy is correct by the time the player Tabs in.
    private void RebuildDetail()
    {
        if (m_controller == null) return;
        var panel = m_controller.GetDeckPanel();
        var items = new List<Selectable>();
        if (panel != null)
        {
            items = panel.GetComponentsInChildren<Selectable>(includeInactive: false)
                .Where(s => (s is Button || s is Toggle) && s.isActiveAndEnabled && s.GetComponentInParent<CardStubParts>() == null)
                .ToList();

            // Guarantee SET ACTIVE is present by pulling the game's own setActiveButton reference from
            // SetActiveDeckButtonParts, rather than trusting the generic descendant scan to reach it (it lives in a nested
            // state-swapper that toggles activeState on/off per deck). Only when it's the live, clickable button.
            var parts = ReadField<SetActiveDeckButtonParts>(panel, "setActiveDeckButtonParts");
            var setBtn = ReadField<Button>(parts, "setActiveButton");
            m_setActiveBtn = setBtn != null && setBtn.isActiveAndEnabled ? setBtn : null;
            if (m_setActiveBtn != null && !items.Contains(m_setActiveBtn)) items.Add(m_setActiveBtn);

            // The ✎ rename pencil (DeckPanel_L.editDeckNameButton). Its onClick is added at runtime (OnEnable →
            // AddListener(OpenDeckRenameMenu)), so UiLabels.ByAction — which reads only prefab-persistent onClick
            // entries — can't see it. Recorded by identity to give it the game's own "Rename deck" label.
            m_renameBtn = ReadField<Button>(panel, "editDeckNameButton");
        }
        m_detailNav.SetItems(CollectionUi.InReadingOrder(items), DetailLabel);
        m_detailNav.ForceFlatVertical();   // predictable Up/Down list, not a confusing spatial grid
        m_detailNav.GroupName = LocalizationUtils.GetGame("deck_options", LocalizationKey.UI_DECK_OPTIONS);
    }

    private bool SectionHasContent(Section s) => s switch
    {
        Section.Top => m_topNav.Count > 0,
        Section.Tabs => m_tabsNav.Count > 0,
        Section.Filters => m_filterNav.Count > 0,
        Section.Decks => true,   // always has at least "Create a deck"
        Section.Detail => m_detailNav.Count > 0,
        _ => false,
    };

    private void CycleSection(int dir)
    {
        var order = new[] { Section.Top, Section.Tabs, Section.Filters, Section.Decks, Section.Detail };
        var cur = Math.Max(0, Array.IndexOf(order, m_section));
        for (var step = 1; step <= order.Length; step++)
        {
            var cand = order[((cur + dir * step) % order.Length + order.Length) % order.Length];
            if (cand == Section.Detail) RebuildDetail();
            if (SectionHasContent(cand)) { EnterSection(cand, interruptPrior: true); return; }
        }
    }

    private void EnterSection(Section s, bool interruptPrior)
    {
        CollectionUi.ReleaseTextField(m_searchField);
        m_searchOpened = false;
        // Re-scan the detail panel against the live deck selection before entering it, so SET ACTIVE appears for a
        // non-active valid deck (it's hidden for the active deck, the default selection).
        if (s == Section.Detail) RebuildDetail();
        if (!SectionHasContent(s)) s = Section.Decks;
        m_section = s;
        switch (s)
        {
            case Section.Top: m_topNav.FocusFirst(announce: true, interruptPrior); break;
            case Section.Tabs: m_tabsNav.FocusFirst(announce: true, interruptPrior); break;
            case Section.Filters: RebuildFilters(); m_filterNav.FocusFirst(announce: true, interruptPrior); break;
            case Section.Detail: m_detailNav.FocusFirst(announce: true, interruptPrior); break;
            case Section.Decks:
                var index = m_deckIndex < -1 || m_deckIndex >= m_entries.Count ? (m_entries.Count > 0 ? 0 : -1) : m_deckIndex;
                DeckFocus(index, interruptPrior);
                break;
        }
    }

    // Horizontal strip (top nav / grouping tabs): Left/Right within; Up/Down escape to the section above/below — the
    // HOME-style arrow escape so the top strip is reachable without Tab.
    private void HandleStrip(KeyboardNavigator nav, Section? up, Section? down)
    {
        if (AccessibleKey.READ_PREV_MENU_OPTION.IsPressed()) { if (up.HasValue) EnterSection(up.Value, interruptPrior: true); return; }
        if (AccessibleKey.READ_NEXT_MENU_OPTION.IsPressed()) { if (down.HasValue) EnterSection(down.Value, interruptPrior: true); return; }
        nav.HandleInput();
    }

    // ── Deck grid (data nav; focus selects the deck → updates the detail panel) ──

    private void HandleDecks()
    {
        if (AccessibleKey.READ_NEXT_ITEM.IsPressed()) MoveDeckSpatial(1, 0);
        else if (AccessibleKey.READ_PREV_ITEM.IsPressed()) MoveDeckSpatial(-1, 0);
        else if (AccessibleKey.READ_NEXT_MENU_OPTION.IsPressed()) MoveDeckSpatial(0, -1);
        // Up at the top row escapes to the grouping tabs (→ then the top strip), like HOME.
        else if (AccessibleKey.READ_PREV_MENU_OPTION.IsPressed()) { if (!MoveDeckSpatial(0, 1)) EnterSection(Section.Tabs, interruptPrior: true); }
        else if (AccessibleKey.CONFIRM.IsPressed())
        {
            if (m_deckIndex < 0) m_controller!.CreateNewDeckButtonActions();                          // CREATE A DECK
            else if (m_deckIndex < m_entries.Count) OpenEditor(m_entries[m_deckIndex].storedDeck);    // open the deck in the editor
        }
    }

    // Move within the deck grid spatially (4-wide layout + the CREATE tile). Returns false when there's nothing that
    // way, so Up at the top row escapes upward.
    private bool MoveDeckSpatial(int dx, int dy)
    {
        var hasCreate = m_createButton != null && m_createButton.isActiveAndEnabled;
        var pos = new List<Vector2>();
        if (hasCreate) pos.Add(Center(m_createButton!.transform));
        foreach (var e in m_entries) pos.Add(Center(e.transform));
        if (pos.Count == 0) return false;
        var cur = (hasCreate ? 1 : 0) + m_deckIndex;
        if (cur < 0 || cur >= pos.Count) cur = 0;
        var best = SpatialPick(pos, cur, dx, dy);
        if (best < 0) return false;
        DeckFocus(best - (hasCreate ? 1 : 0), interrupt: true);
        return true;
    }

    // Open the editor for a deck — the controller's own OpenDeckEditorForDeck (private) does the full
    // SetupForDeck/Activate; driving it directly is more reliable than the panel's EDIT DECK button, which depends on
    // the panel having committed the selection.
    private void OpenEditor(DeckInfo deck) => s_openDeckEditorForDeck?.Invoke(m_controller, new object[] { deck });

    private static Vector2 Center(Transform t)
    {
        if (t is RectTransform rt)
        {
            var c = new Vector3[4];
            rt.GetWorldCorners(c);
            return (c[0] + c[2]) * 0.5f;
        }
        return t.position;
    }

    private static int SpatialPick(IReadOnlyList<Vector2> pos, int cur, float dx, float dy)
    {
        var from = pos[cur];
        int best = -1;
        var bestScore = float.MaxValue;
        for (var i = 0; i < pos.Count; i++)
        {
            if (i == cur) continue;
            float ox = pos[i].x - from.x, oy = pos[i].y - from.y;
            var along = ox * dx + oy * dy;
            if (along <= 2f) continue;
            var perp = Mathf.Abs(ox * dy - oy * dx);
            var score = along + perp * 2.5f;
            if (score < bestScore) { bestScore = score; best = i; }
        }
        return best;
    }

    private void DeckFocus(int index, bool interrupt)
    {
        m_deckIndex = index;
        // Select the deck the game's own way so the detail panel reflects it.
        if (index >= 0 && index < m_entries.Count) m_entries[index].ReportDeckOptionClicked();
        AnnounceDeck(interrupt);
    }

    private void AnnounceDeck(bool interrupt)
    {
        if (m_deckIndex < 0)
        {
            AccessibilityMgr.Output(this, LocalizationUtils.GetGame("deck_manager_create_a_deck", LocalizationKey.COLLECTION_CREATE_DECK), interrupt);
            return;
        }
        if (m_deckIndex >= m_entries.Count) return;
        var entry = m_entries[m_deckIndex];
        var deck = entry.storedDeck;
        var valid = false;
        try { valid = deck.IsValid(HUBGameModeController.gameMode); }
        catch { /* a deck the validator can't read is announced as invalid */ }
        var status = LocalizationUtils.Get(valid ? LocalizationKey.COLLECTION_VALID : LocalizationKey.COLLECTION_INVALID);

        // A deck can be the active deck for more than one format at once — the game shows one coloured check per mode on
        // the tile. Say exactly which format(s): "active for Standard", "active for Standard and Expanded" — so two decks
        // each active for a different format don't both read a bare, ambiguous "active". Mode names come from the game's
        // own GetGameModeLocString; a mode with no player-facing name falls back to a plain "active".
        var modeNames = new List<string>();
        var anyActive = false;
        foreach (var mode in HUBGameModeController.GetOrderedGameModes())
        {
            if (!HUBDeckSelectController.IsActiveDeckForMode(mode, deck)) continue;
            anyActive = true;
            if (LocalizationUtils.TryGetGame(HUBGameModeController.GetGameModeLocString(mode)) is { } name) modeNames.Add(name.Trim());
        }
        if (modeNames.Count > 0) status = LocalizationUtils.Format(LocalizationKey.COMMON_STATE_FORMAT, status, LocalizationUtils.Format(LocalizationKey.COLLECTION_ACTIVE_FOR, CollectionUi.Conjoin(modeNames)));
        else if (anyActive) status = LocalizationUtils.Format(LocalizationKey.COMMON_STATE_FORMAT, status, LocalizationUtils.Get(LocalizationKey.COLLECTION_ACTIVE));

        var label = LocalizationUtils.Format(LocalizationKey.COMMON_STATE_FORMAT, entry.DeckName, status);
        AccessibilityMgr.Output(this, Announce.Item(label, null, m_deckIndex + 1, m_entries.Count), interrupt);
    }

    // ── Labels ──

    // Top strip: HUB tabs use the persistent-nav label; the challenge "return" button reads as a clear action (its own
    // text is a bare back-arrow / generic style name).
    private string TopLabel(Selectable s) =>
        s == m_returnButton ? LocalizationUtils.Get(LocalizationKey.COLLECTION_RETURN_TO_CHALLENGE) : UiLabels.TopNavLabel(s);

    private static string ToggleLabel(Selectable s) => CollectionUi.TabState(CollectionUi.FirstLetterText(s) ?? KeyboardNavigator.ResolveBaseLabel(s), s);

    // SET ACTIVE is matched by identity (the game's own setActiveButton reference), not by name/text. Its clickable
    // target can carry no worded label of its own (the "SET ACTIVE" text sits on a sibling dynamic-button state object),
    // so if the text-scrape finds no real word, name it explicitly. Every other option keeps the default label.
    private string DetailLabel(Selectable s)
    {
        if (m_setActiveBtn != null && s == m_setActiveBtn && !UiLabels.HasLetter(KeyboardNavigator.ResolveBaseLabel(s)))
            return LocalizationUtils.Get(LocalizationKey.COLLECTION_SET_ACTIVE);
        if (m_renameBtn != null && s == m_renameBtn)
            return LocalizationUtils.GetGame("ui_deck_options_rename", LocalizationKey.UI_RENAME_DECK);
        return KeyboardNavigator.DefaultLabel(s);
    }

    // ── Hooks ──

    private void Activated(HUBDeckSelectController controller)
    {
        var wasCurrent = AccessibilityMgr.CurrentScreen == this && m_controller == controller;
        m_controller = controller;
        if (wasCurrent) return;
        m_fresh = true;
        AccessibilityMgr.SetScreen(this);
    }

    private void Deactivated(HUBDeckSelectController controller)
    {
        if (controller != m_controller) return;
        m_controller = null;
        AccessibilityMgr.ClearScreen(this);
    }

    [HarmonyPatch(typeof(HUBDeckSelectController), "OnActivate")]
    private static class OnActivateHook
    {
        private static void Postfix(HUBDeckSelectController __instance) => s_instance.Activated(__instance);
    }

    [HarmonyPatch(typeof(HUBDeckSelectController), "OnDeactivate")]
    private static class OnDeactivateHook
    {
        private static void Postfix(HUBDeckSelectController __instance) => s_instance.Deactivated(__instance);
    }
}
