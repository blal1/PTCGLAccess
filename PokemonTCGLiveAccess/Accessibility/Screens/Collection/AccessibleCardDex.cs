using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using CardDatabase.DataAccess;
using HarmonyLib;
using SharedLogicUtils.source.CardDex;
using TPCI.Localization;
using TPCI.Rainier.CardDex;
using UnityEngine;
using UnityEngine.UI;
using static Accessibility.GameReflect;

namespace Accessibility;

// CARD DEX (card collection) screen — driver HUBCardDexScreenController, spawned by CardDexFeatureDriver onto a
// HUBScreenFeaturePlatform whose OnActivate/OnDeactivate events call the controller's public parameterless
// OnActivate()/OnDeactivate() — those are the lifecycle hook (NOT the standard HUBScreenController.OnActivate
// (HUBGroupController), because this controller is a plain MonoBehaviour).
//
// Presented as a section stack (Up/Down escapes between sections at their edge, Left/Right within, Tab cycles,
// Escape → overflow menu), visually top-to-bottom:
//   1. HUB top strip + overflow.
//   2. Series selector — the "MEGA EVOLUTION" dropdown trigger. Enter opens the in-place series list (a sub-mode
//      handled here, since in landscape it's not an OverlayManager overlay).
//   3. Set carousel — the row of expansion sets. Left/Right load the adjacent set (the game's own ArrowScroll).
//   4. View controls — the MASTER/MAIN SET VIEW toggle, the "(i)" key info button, and the SORT BY button. Enter
//      drives each one's real action (classification toggle / info popup / sort overlay).
//   5. Card grid — the collection. Arrows move a card; Enter opens the card-detail overlay (AccessibleCardDexDetail).
//
// The series list and the landscape sort overlay are in-place expandable panels (not OverlayManager overlays), so
// they're poll-detected sub-modes here.
internal sealed class AccessibleCardDex : AccessibleScreen
{
    private enum Mode { Normal, SeriesPicker, SortPicker }

    private static readonly AccessibleCardDex s_instance = new();

    private HUBCardDexScreenController? m_controller;
    private readonly List<INavSection> m_groups = new();
    private int m_active = -1;
    private Mode m_mode = Mode.Normal;
    private bool m_fresh;
    private string m_lastContentSig = "";

    // Reflected game refs (cached on activate).
    private CollectionSetCarousel? m_carousel;
    private CardDexSeriesSelector? m_seriesSelector;
    private HUBCollectionsScreenController? m_collections;
    private CardCollectionGalleryView? m_gallery;
    private LocText? m_seriesTitle;
    private Button? m_seriesBtn;
    private Button? m_infoBtn;
    private Selectable? m_masterSel;

    private KeyboardNavigator? m_seriesNav;
    private KeyboardNavigator? m_viewNav;
    private int m_seriesGroup = -1, m_viewGroup = -1, m_gridGroup = -1;

    // Series-picker sub-mode (data nav over the dropdown's series list).
    private List<CardDexSeriesObjectsData> m_seriesData = new();
    private int m_seriesPick;
    // Sort-picker sub-mode (real Selectable toggles inside the sort overlay).
    private readonly KeyboardNavigator m_sortNav;

    // Tutorial: the coached control, presented alone (it's reparented under a Blocker, outside our sections).
    private readonly KeyboardNavigator m_ftueNav;
    private Selectable? m_ftueShown;

    private AccessibleCardDex()
    {
        m_sortNav = new KeyboardNavigator(this) { AllowTab = false };
        m_ftueNav = new KeyboardNavigator(this) { AllowTab = false };
    }

    public string GetHelp() => LocalizationUtils.Get(LocalizationKey.COLLECTION_CARD_DEX_HELP);

    public void OnGainedFocus()
    {
        if (m_controller == null) return;
        m_mode = Mode.Normal;
        if (FtueFocus() != null) { m_ftueShown = null; UpdateFtue(); return; }
        if (m_fresh || m_groups.Count == 0)
        {
            m_fresh = false;
            Rebuild();
            if (m_groups.Count == 0) return;
            m_active = DefaultGroup();
            AccessibilityMgr.Output(this, ScreenSummary(), interrupt: true);
            m_groups[m_active].FocusFirst(announce: true, interruptPrior: false);
            return;
        }
        // Back from the card-detail overlay or a popup: the grid keeps its cursor on the card we opened.
        if (ContentSig() != m_lastContentSig) OnContentChanged();
        if (m_active >= 0 && m_active < m_groups.Count) m_groups[m_active].RefocusCurrent(announce: true, interrupt: true);
    }

    public void HandleInput()
    {
        if (m_controller == null) return;

        if (UpdateFtue()) return;

        // In-place dropdowns take over while open (poll their own open flags — there's no Activate hook to ride).
        if (SeriesOpen()) { SeriesPicker(); return; }
        if (SortOpen()) { SortPicker(); return; }
        if (m_mode != Mode.Normal)
        {
            m_mode = Mode.Normal;   // a dropdown just closed — return to the section we came from
            if (m_active >= 0 && m_active < m_groups.Count) m_groups[m_active].RefocusCurrent(announce: true);
        }

        var sig = ContentSig();
        if (sig != m_lastContentSig) { OnContentChanged(); return; }
        if (m_groups.Count == 0 || m_active < 0) return;
        var nav = m_groups[m_active];

        if (CollectionUi.BackPressed()) { CollectionUi.OpenOverflowMenu(); return; }
        if (AccessibleKey.NEXT_SECTION.IsPressed() || AccessibleKey.PREV_SECTION.IsPressed())
        {
            if (m_groups.Count < 2) return;
            var dir = AccessibleKey.PREV_SECTION.IsPressed() ? -1 : 1;
            m_active = (m_active + dir + m_groups.Count) % m_groups.Count;
            m_groups[m_active].FocusFirst(announce: true, interruptPrior: true);
            return;
        }
        if (AccessibleKey.READ_PREV_MENU_OPTION.IsPressed() && m_active > 0 && nav.IsAtTopRow)
        {
            var col = nav.CurrentColumn;
            m_active--;
            m_groups[m_active].FocusLastRow(col, announce: true, interruptPrior: true);
            return;
        }
        if (AccessibleKey.READ_NEXT_MENU_OPTION.IsPressed() && m_active < m_groups.Count - 1 && nav.IsAtBottomRow)
        {
            var col = nav.CurrentColumn;
            m_active++;
            m_groups[m_active].FocusFirstRow(col, announce: true, interruptPrior: true);
            return;
        }
        // Enter / Space on a control section drives that control's real action (the dropdowns aren't wired to
        // onClick in landscape, so calling the public methods is the reliable game path).
        if ((m_active == m_seriesGroup || m_active == m_viewGroup) && (AccessibleKey.CONFIRM.IsPressed() || AccessibleKey.SPACE.IsPressed()))
        {
            ActivateControl(nav as KeyboardNavigator);
            return;
        }
        nav.HandleInput();
    }

    private void ActivateControl(KeyboardNavigator? nav)
    {
        if (m_active == m_seriesGroup) { m_seriesSelector?.ToggleSelectionMenu(); return; }
        var cur = nav?.Current;
        if (cur == null) return;
        if (cur == m_infoBtn) { m_infoBtn!.onClick.Invoke(); return; }
        if (m_masterSel != null && cur == m_masterSel) { m_collections?.ToggleClassificationAndRefreshDisplay(); return; }
        // Only the real SORT control opens the sort overlay: this section also holds the Card collection / Research
        // missions tabs and the collector stats button, which must do their own thing.
        if (cur.gameObject.name.IndexOf("sort", StringComparison.OrdinalIgnoreCase) >= 0
            || cur == ReadField<Button>(m_controller, "sortOrderBtn")
            || (cur is Button sortBtn && UiLabels.HasHandler(sortBtn, "ToggleLandscapeSortingOverlay")))
        {
            m_gallery?.ToggleLandscapeSortingOverlay();
            return;
        }
        if (cur is Toggle tg) tg.isOn = true;   // tabs: Enter selects, never un-toggles
        else if (cur is Button bt) bt.onClick.Invoke();
    }

    // ── Groups ──

    private void CacheRefs()
    {
        m_carousel = ReadField<CollectionSetCarousel>(m_controller, "collectionSetCarousel");
        m_seriesSelector = ReadField<CardDexSeriesSelector>(m_controller, "cardDexSeriesSelector");
        m_seriesTitle = ReadField<LocText>(m_controller, "seriesTitle");
        m_seriesBtn = ReadField<Button>(m_controller, "dexSeriesSelectBtn");
        m_infoBtn = ReadField<Button>(m_controller, "cardDexInfoBtn");
        m_collections = ReadField<HUBCollectionsScreenController>(m_controller, "collectionsScreenController");
        m_gallery = ReadField<CardCollectionGalleryView>(m_collections, "_cardCollectionGalleryView");
        var master = ReadField<Component>(m_collections, "classificationToggle");
        m_masterSel = master != null ? master.GetComponentInChildren<Selectable>(true) : null;
    }

    private void Rebuild()
    {
        m_groups.Clear();
        m_seriesGroup = m_viewGroup = m_gridGroup = -1;
        m_seriesNav = m_viewNav = null;
        m_lastContentSig = ContentSig();
        if (m_controller == null) return;

        var top = CollectionUi.TopStrip();
        if (top.Count > 0) AddNav(top, UiLabels.TopNavLabel, null);
        var known = new HashSet<Selectable>(top);

        // The chrome controls (series trigger, view toggle, info, sort), discovered by exclusion and bucketed by whether
        // they sit above the set carousel (the series selector) or below it (the view controls).
        var carouselY = m_carousel != null ? m_carousel.transform.position.y : float.NaN;
        var above = new List<Selectable>();
        var below = new List<Selectable>();
        foreach (var s in ChromeControls(known))
        {
            if (!float.IsNaN(carouselY) && s.transform.position.y > carouselY) above.Add(s);
            else below.Add(s);
        }

        if (above.Count > 0)
        {
            m_seriesNav = AddNav(CollectionUi.InReadingOrder(above), ControlLabel, LocalizationUtils.Get(LocalizationKey.COLLECTION_SERIES));
            m_seriesGroup = m_groups.Count - 1;
        }
        if (m_carousel != null) m_groups.Add(new CarouselSection(this, m_carousel));
        if (below.Count > 0)
        {
            m_viewNav = AddNav(CollectionUi.InReadingOrder(below), ControlLabel, LocalizationUtils.Get(LocalizationKey.COLLECTION_VIEW));
            m_viewGroup = m_groups.Count - 1;
        }
        if (m_gallery != null)
        {
            m_groups.Add(new GridSection(this, m_gallery));
            m_gridGroup = m_groups.Count - 1;
        }
    }

    private KeyboardNavigator AddNav(List<Selectable> items, KeyboardNavigator.LabelResolver resolver, string? groupName)
    {
        var nav = new KeyboardNavigator(this) { AllowTab = false, GroupName = groupName };
        nav.SetItems(items, resolver);
        m_groups.Add(nav);
        return nav;
    }

    // Interactable Buttons / Toggles of the screen chrome — everything under the controller (and the collections subpage,
    // which owns the master-set toggle + sort button) that isn't the top strip, a carousel cell, a grid cell, or a
    // dropdown item.
    private List<Selectable> ChromeControls(HashSet<Selectable> known)
    {
        var result = new List<Selectable>();
        var seen = new HashSet<Selectable>(known);
        var carouselT = m_carousel != null ? m_carousel.transform : null;
        var gridC = ReadField<Component>(m_gallery, "contentWindow");
        var gridT = gridC != null ? gridC.transform : null;
        var seriesListT = m_seriesSelector != null ? m_seriesSelector.transform : null;
        var sortT = m_gallery != null && m_gallery.SortingOverlay != null ? m_gallery.SortingOverlay.transform : null;

        void TryAdd(Selectable? s)
        {
            if (s == null || seen.Contains(s) || !s.isActiveAndEnabled) return;
            if (s is not Button && s is not Toggle) return;
            if (carouselT != null && s.transform.IsChildOf(carouselT)) return;
            if (gridT != null && s.transform.IsChildOf(gridT)) return;
            if (seriesListT != null && s.transform.IsChildOf(seriesListT)) return;
            if (sortT != null && s.transform.IsChildOf(sortT)) return;
            // The master toggle is a wrapper with an inner child Selectable — keep only the inner one.
            if (s is Toggle && m_masterSel != null && s != m_masterSel && m_masterSel.transform.IsChildOf(s.transform)) return;
            seen.Add(s);
            result.Add(s);
        }

        // Seed with the controls we hold direct refs to (the scan may not reach them), then sweep.
        TryAdd(m_seriesBtn);
        TryAdd(m_infoBtn);
        TryAdd(m_masterSel);
        foreach (var root in new Component?[] { m_controller, m_collections })
            if (root != null)
                foreach (var s in root.GetComponentsInChildren<Selectable>(includeInactive: false)) TryAdd(s);
        return result;
    }

    private int DefaultGroup() => m_gridGroup >= 0 ? m_gridGroup : m_groups.Count - 1;

    private CardDexClassification Classification() =>
        m_collections != null ? m_collections.GetCardDexClassification() : CardDexClassification.MasterSet;

    // ── Content changes (set / series / view / sort) ──

    private string ContentSig()
    {
        if (m_controller == null) return "none";
        var cards = ReadField<List<ArchetypeDBCards>>(m_gallery, "curCollection")?.Count ?? 0;
        return $"{HUBCardDexScreenController.LoadedSeriesId}|{HUBCardDexScreenController.LoadedSetId}|{Classification()}|{cards}";
    }

    private void OnContentChanged()
    {
        var prev = m_active;
        Rebuild();
        if (m_groups.Count == 0) { m_active = -1; return; }
        m_active = Mathf.Clamp(prev < 0 ? DefaultGroup() : prev, 0, m_groups.Count - 1);
        m_groups[m_active].RefocusCurrent(announce: false);
    }

    private void OpenCardDetail(ArchetypeDBCards stack)
    {
        var ov = ManagerSingleton<OverlayManager>.instance?.cardDexPurchaseCardOverlay;
        if (ov == null || m_gallery == null) return;
        ov.SetAdjacentProvider(m_gallery);
        ov.Setup(stack);   // → AccessibleCardDexDetail
    }

    // ── Series picker ──

    private bool SeriesOpen() => m_seriesSelector != null && s_isOpen?.GetValue(m_seriesSelector) is true;

    private static readonly FieldInfo? s_isOpen = AccessTools.Field(typeof(CardDexSeriesSelector), "isOpen");

    private void SeriesPicker()
    {
        if (m_mode != Mode.SeriesPicker)
        {
            m_mode = Mode.SeriesPicker;
            m_seriesData = ReadField<Dictionary<string, CardDexSeriesObjectsData>>(m_seriesSelector, "_cardDexSeriesObjectsData")?.Values.Where(v => v != null).ToList() ?? new();
            m_seriesPick = Math.Max(0, m_seriesData.FindIndex(e => e.seriesId == HUBCardDexScreenController.LoadedSeriesId));
            AccessibilityMgr.Output(this, LocalizationUtils.Get(LocalizationKey.COLLECTION_SERIES), interrupt: true);
            AnnounceSeriesPick(interrupt: false);
        }
        if (CollectionUi.BackPressed()) { m_seriesSelector?.CloseSeriesOverlay(); return; }
        if (AccessibleKey.READ_NEXT_MENU_OPTION.IsPressed()) { if (m_seriesPick < m_seriesData.Count - 1) { m_seriesPick++; AnnounceSeriesPick(true); } }
        else if (AccessibleKey.READ_PREV_MENU_OPTION.IsPressed()) { if (m_seriesPick > 0) { m_seriesPick--; AnnounceSeriesPick(true); } }
        else if (AccessibleKey.CONFIRM.IsPressed() && m_seriesPick >= 0 && m_seriesPick < m_seriesData.Count)
        {
            var e = m_seriesData[m_seriesPick];
            e.onSelectSeries?.Invoke(e.seriesProgress, e.seriesId);
        }
    }

    private void AnnounceSeriesPick(bool interrupt)
    {
        if (m_seriesPick < 0 || m_seriesPick >= m_seriesData.Count) return;
        var e = m_seriesData[m_seriesPick];
        var parts = new List<string> { SetOrSeriesName(e.seriesId) ?? LocalizationUtils.Get(LocalizationKey.COLLECTION_SERIES) };
        if (e.seriesProgress?.SeriesProgress != null && e.seriesProgress.SeriesProgress.TryGetValue(Classification(), out var p) && p != null)
            parts.Add(LocalizationUtils.Format(LocalizationKey.COLLECTION_COLLECTED, p.CardProgress, p.CardTotal));
        if (e.seriesId == HUBCardDexScreenController.LoadedSeriesId) parts.Add(LocalizationUtils.Get(LocalizationKey.GLOBAL_SELECTED));
        AccessibilityMgr.Output(this, Announce.Item(string.Join(", ", parts), null, m_seriesPick + 1, m_seriesData.Count), interrupt);
    }

    // ── Sort picker ──

    private bool SortOpen()
    {
        var blocker = ReadField<GameObject>(m_gallery, "sortUIBlocker");
        return blocker != null && blocker.activeSelf;
    }

    private void SortPicker()
    {
        if (m_mode != Mode.SortPicker)
        {
            m_mode = Mode.SortPicker;
            var overlay = m_gallery != null ? m_gallery.SortingOverlay : null;
            var items = overlay != null
                ? CollectionUi.InReadingOrder(overlay.GetComponentsInChildren<Selectable>(includeInactive: false).Where(s => (s is Button || s is Toggle) && s.isActiveAndEnabled))
                : new List<Selectable>();
            m_sortNav.SetItems(items);
            m_sortNav.ForceFlatVertical();
            AccessibilityMgr.Output(this, LocalizationUtils.Get(LocalizationKey.COLLECTION_SORT_BY), interrupt: true);
            m_sortNav.FocusFirst(announce: true, interruptPrior: false);
        }
        if (CollectionUi.BackPressed()) { m_gallery?.CloseSortingOverlay(); return; }
        m_sortNav.HandleInput();
    }

    // ── Tutorial ──

    private static Selectable? FtueFocus() =>
        FtueTracker.FtueIsRunning && FtueTracker.FocusedElement is { } f && f.isActiveAndEnabled ? f : null;

    // A coached control present → it's the only thing to do (the game blocks the rest). True = input consumed.
    private bool UpdateFtue()
    {
        var focus = FtueFocus();
        if (focus == null)
        {
            if (m_ftueShown == null) return false;
            m_ftueShown = null;
            OnContentChanged();
            if (m_active >= 0) m_groups[m_active].RefocusCurrent(announce: true);
            return true;
        }
        if (focus != m_ftueShown)
        {
            m_ftueShown = focus;
            m_ftueNav.RadioToggleMode = focus is Toggle;   // a coached Toggle is a tab: Enter must SELECT it
            m_ftueNav.SetItems(new List<Selectable> { focus });
            m_ftueNav.FocusFirst(announce: true, interruptPrior: true);
            return true;
        }
        m_ftueNav.HandleInput();
        return true;
    }

    // ── Labels ──

    private static string? SetOrSeriesName(string? id) => CardDexText.Loc("tcg_" + (id ?? "").ToLowerInvariant());

    private string ScreenSummary()
    {
        var parts = new List<string> { LocalizationUtils.Get(LocalizationKey.COLLECTION_CARD_DEX) };
        var series = SetOrSeriesName(HUBCardDexScreenController.LoadedSeriesId) ?? CollectionUi.Text(m_seriesTitle);
        if (!string.IsNullOrWhiteSpace(series)) parts.Add(series!);
        if (SetOrSeriesName(HUBCardDexScreenController.LoadedSetId) is { Length: > 0 } set) parts.Add(set);
        return string.Join(", ", parts);
    }

    private string ControlLabel(Selectable s)
    {
        if (s == m_infoBtn) return LocalizationUtils.Get(LocalizationKey.COLLECTION_CARD_DEX_KEY);
        if (m_seriesNav != null && m_seriesNav.Items.Contains(s))
        {
            var name = CollectionUi.Text(m_seriesTitle) ?? SetOrSeriesName(HUBCardDexScreenController.LoadedSeriesId);
            return string.IsNullOrWhiteSpace(name)
                ? LocalizationUtils.Get(LocalizationKey.COLLECTION_SERIES)
                : LocalizationUtils.Format(LocalizationKey.COMMON_STATE_FORMAT, LocalizationUtils.Get(LocalizationKey.COLLECTION_SERIES), name!);
        }
        if (s == m_masterSel)
        {
            var label = CollectionUi.FirstLetterText(s) ?? LocalizationUtils.Get(LocalizationKey.COLLECTION_SET_VIEW);
            return s.interactable ? label : Announce.WithState(label, LocalizationKey.GLOBAL_DISABLED);
        }
        // Sort and anything else: the control's own text is self-describing ("SORT BY: ...").
        return KeyboardNavigator.DefaultLabel(s);
    }

    // ── Hooks ──

    private void Activated(HUBCardDexScreenController controller)
    {
        var wasCurrent = AccessibilityMgr.CurrentScreen == this && m_controller == controller;
        m_controller = controller;
        CacheRefs();
        if (wasCurrent) return;
        m_fresh = true;
        m_mode = Mode.Normal;
        m_ftueShown = null;
        m_groups.Clear();
        AccessibilityMgr.SetScreen(this);
    }

    private void Deactivated(HUBCardDexScreenController controller)
    {
        if (controller != m_controller) return;
        m_controller = null;
        m_groups.Clear();
        AccessibilityMgr.ClearScreen(this);
    }

    [HarmonyPatch(typeof(HUBCardDexScreenController), "OnActivate")]
    private static class OnActivateHook
    {
        private static void Postfix(HUBCardDexScreenController __instance) => s_instance.Activated(__instance);
    }

    [HarmonyPatch(typeof(HUBCardDexScreenController), "OnDeactivate")]
    private static class OnDeactivateHook
    {
        private static void Postfix(HUBCardDexScreenController __instance) => s_instance.Deactivated(__instance);
    }

    // ── Set carousel ──
    // The horizontal strip of expansion SETS. Left/Right call the carousel's own ArrowScroll, which loads that set's cards
    // into the grid below (exactly what the < > arrows do). One logical row, so Up/Down always escape.
    private sealed class CarouselSection : AccessibleElement, INavSection
    {
        private static readonly FieldInfo? s_data = AccessTools.Field(typeof(CollectionSetCarousel), "_carouselObjectsData");

        private readonly AccessibleCardDex m_owner;
        private readonly CollectionSetCarousel m_carousel;

        public CarouselSection(AccessibleCardDex owner, CollectionSetCarousel carousel) : base(owner)
        {
            m_owner = owner;
            m_carousel = carousel;
        }

        private List<CollectionCarouselObjectsData> Data() => s_data?.GetValue(m_carousel) as List<CollectionCarouselObjectsData> ?? new();

        private int CurrentIndex()
        {
            var data = Data();
            var i = data.FindIndex(d => d != null && d.setId == HUBCardDexScreenController.LoadedSetId);
            return i >= 0 ? i : data.Count > 0 ? 0 : -1;
        }

        public int Index => CurrentIndex();
        public int Count => Data().Count;
        public bool IsAtTopRow => true;
        public bool IsAtBottomRow => true;
        public int CurrentColumn => 0;

        public void FocusFirst(bool announce = true, bool interruptPrior = true) { if (announce) Speak(interruptPrior, withSection: true); }
        public void FocusFirstRow(int preferredColumn = -1, bool announce = true, bool interruptPrior = true) => FocusFirst(announce, interruptPrior);
        public void FocusLastRow(int preferredColumn = -1, bool announce = true, bool interruptPrior = true) => FocusFirst(announce, interruptPrior);
        public void RefocusCurrent(bool announce = true, bool interrupt = true) => FocusFirst(announce, interrupt);

        public bool HandleInput()
        {
            if (AccessibleKey.READ_PREV_ITEM.IsPressed()) { m_carousel.ArrowScroll(-1); Speak(true, false); return true; }
            if (AccessibleKey.READ_NEXT_ITEM.IsPressed()) { m_carousel.ArrowScroll(1); Speak(true, false); return true; }
            if (AccessibleKey.CONFIRM.IsPressed()) { Speak(true, false); return true; }
            return false;
        }

        private void Speak(bool interrupt, bool withSection)
        {
            var data = Data();
            var idx = CurrentIndex();
            var section = LocalizationUtils.Get(LocalizationKey.COLLECTION_SETS);
            if (idx < 0 || idx >= data.Count) { Output(section, interrupt); return; }
            var d = data[idx];
            var parts = new List<string> { SetOrSeriesName(d.setId) ?? d.setId ?? section };
            if (d.setProgress?.SetProgress != null && d.setProgress.SetProgress.TryGetValue(m_owner.Classification(), out var p) && p != null)
                parts.Add(LocalizationUtils.Format(LocalizationKey.COLLECTION_COLLECTED, p.CardProgress, p.CardTotal));
            if (d.setProgress != null && d.setProgress.HasNewCards) parts.Add(LocalizationUtils.Get(LocalizationKey.COLLECTION_NEW_CARDS));
            var label = Announce.Item(string.Join(", ", parts), null, idx + 1, data.Count);
            if (withSection) { Output(section, interrupt); Output(label, interrupt: false); }
            else Output(label, interrupt);
        }
    }

    // ── Card grid ──
    // Navigates the gallery's curCollection list as a grid (not the virtualized scroller cells, which only spawn the
    // on-screen handful): Left/Right step one card, Up/Down jump a row; Enter opens the card-detail overlay — the same
    // overlay a tile click opens.
    private sealed class GridSection : AccessibleElement, INavSection
    {
        private static readonly FieldInfo? s_numColumns = AccessTools.Field(typeof(CardDexScrollerController), "numColumns");

        private readonly AccessibleCardDex m_owner;
        private readonly CardCollectionGalleryView m_gallery;
        private int m_index;

        public GridSection(AccessibleCardDex owner, CardCollectionGalleryView gallery) : base(owner)
        {
            m_owner = owner;
            m_gallery = gallery;
        }

        private List<ArchetypeDBCards> Cards() => ReadField<List<ArchetypeDBCards>>(m_gallery, "curCollection") ?? new();

        private int Columns()
        {
            var cw = ReadField<CardDexScrollerController>(m_gallery, "contentWindow");
            return cw != null && s_numColumns?.GetValue(cw) is int n && n > 0 ? n : 4;
        }

        public int Index => m_index;
        public int Count => Cards().Count;
        public int CurrentColumn => m_index >= 0 ? m_index % Columns() : 0;
        public bool IsAtTopRow => m_index >= 0 && m_index < Columns();

        public bool IsAtBottomRow
        {
            get
            {
                int count = Cards().Count, cols = Columns();
                return count == 0 || m_index < 0 || m_index >= (count - 1) / cols * cols;
            }
        }

        public void FocusFirst(bool announce = true, bool interruptPrior = true)
        {
            m_index = Cards().Count > 0 ? 0 : -1;
            if (announce) Speak(interruptPrior, withSection: true);
        }

        public void FocusFirstRow(int preferredColumn = -1, bool announce = true, bool interruptPrior = true)
        {
            int count = Cards().Count, cols = Columns();
            m_index = count == 0 ? -1 : preferredColumn < 0 ? 0 : Mathf.Min(preferredColumn, Mathf.Min(cols - 1, count - 1));
            if (announce) Speak(interruptPrior, withSection: true);
        }

        public void FocusLastRow(int preferredColumn = -1, bool announce = true, bool interruptPrior = true)
        {
            int count = Cards().Count, cols = Columns();
            var lastRowStart = count == 0 ? -1 : (count - 1) / cols * cols;
            m_index = count == 0 ? -1 : preferredColumn < 0 ? lastRowStart : Mathf.Min(lastRowStart + preferredColumn, count - 1);
            if (announce) Speak(interruptPrior, withSection: true);
        }

        public void RefocusCurrent(bool announce = true, bool interrupt = true)
        {
            var count = Cards().Count;
            if (count == 0) m_index = -1;
            else if (m_index < 0 || m_index >= count) m_index = 0;
            if (announce) Speak(interrupt, withSection: true);
        }

        public bool HandleInput()
        {
            var cards = Cards();
            var count = cards.Count;
            if (count == 0) return false;
            var cols = Columns();
            if (AccessibleKey.READ_PREV_ITEM.IsPressed()) { if (m_index > 0) { m_index--; Speak(true, false); } return true; }
            if (AccessibleKey.READ_NEXT_ITEM.IsPressed()) { if (m_index < count - 1) { m_index++; Speak(true, false); } return true; }
            if (AccessibleKey.READ_PREV_MENU_OPTION.IsPressed()) { if (m_index - cols >= 0) { m_index -= cols; Speak(true, false); } return true; }
            if (AccessibleKey.READ_NEXT_MENU_OPTION.IsPressed())
            {
                if (m_index + cols < count) { m_index += cols; Speak(true, false); }
                else if (m_index < count - 1) { m_index = count - 1; Speak(true, false); }
                return true;
            }
            if (AccessibleKey.CONFIRM.IsPressed())
            {
                if (m_index >= 0 && m_index < count) m_owner.OpenCardDetail(cards[m_index]);
                return true;
            }
            return false;
        }

        private void Speak(bool interrupt, bool withSection)
        {
            var cards = Cards();
            var section = LocalizationUtils.Get(LocalizationKey.COLLECTION_CARD_COLLECTION);
            if (m_index < 0 || m_index >= cards.Count) { Output(LocalizationUtils.Format(LocalizationKey.COLLECTION_EMPTY, section), interrupt); return; }
            var label = Announce.Item(CardDexText.CardStackLabel(cards[m_index]), null, m_index + 1, cards.Count);
            if (withSection) { Output(section, interrupt); Output(label, interrupt: false); }
            else Output(label, interrupt);
        }
    }
}
