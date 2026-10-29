using System;
using System.Collections.Generic;
using System.Reflection;
using EnhancedUI.EnhancedScroller;
using HarmonyLib;
using SharedLogicUtils.source.DataTypes;
using SharedSDKUtils;
using TPCI.Localization;
using TPCI.Avatar.Customizations;
using UnityEngine;
using UnityEngine.UI;
using static Accessibility.GameReflect;

namespace Accessibility;

// Sub-screen for the avatar customization items panel — the list of FACE / HAIR / GLASSES /
// etc. items that appears when the user activates a category from the Profile grid.
//
// Driver: HUBProfileController (TPCI.RainierClient/HUBProfileController.cs). Items live in the
// private List<AvatarCustomizationData> `_currSortedItemData`, populated by RefreshItemMenu
// when a category opens. The visible UI uses an EnhancedScroller (virtualised) plus a phrase
// ScrollRect — neither of which are friendly to traverse via Selectable scans. We bypass that
// view layer and navigate the data list directly; activation goes through
// HUBProfileController.SelectItemByIndex which is what CustomizationItem buttons themselves
// call, so the avatar visually updates exactly as it would from a mouse click.
//
// Activation flow:
//   • User on outer Profile screen, focuses a grid item (FACE / HAIR / etc.)
//   • Presses Enter — AvatarCustomizationGridItem.OnCustomizationTypeSelected fires
//   • Calls HUBProfileController.SelectCategory(type) — our postfix shows this UI over Profile
//
// Close path:
//   • User presses Escape / Backspace — we call HUBProfileController.SetState(None) which
//     closes the items panel, then hide; Profile regains focus
//   • OR Profile screen deactivates (user navigated away) — HUBProfileController.ResetFromClose
//     fires (called from InternalDeactivation), which our postfix uses to hide cleanly
internal sealed class AccessibleAvatarCategory : AccessibleUI
{
    private static readonly AccessibleAvatarCategory s_instance = new();

    private static readonly FieldInfo? s_selectedItemIndex = AccessTools.Field(typeof(HUBProfileController), "_selectedItemIndex");
    private static readonly FieldInfo? s_itemsPerRow = AccessTools.Field(typeof(HUBProfileController), "_itemsPerRow");
    private static readonly MethodInfo? s_getEquippedIndex = AccessTools.Method(typeof(HUBProfileController), "GetIndexForEquippedCustOfCurrentType");
    private static readonly MethodInfo? s_isOwned = AccessTools.Method(typeof(HUBProfileController), "IsOwned", new[] { typeof(AvatarCustomizationData) });

    private HUBProfileController? m_controller;
    private AvatarCustomizationType m_category = AvatarCustomizationType.None;
    private int m_index;
    private bool m_shown;
    private bool m_announceHeader;

    // Section pointer. The category panel is a stack of vertically-adjacent groups,
    // bottom-to-top:
    //   Items   — the main item grid (always present)
    //   Slider  — single-value slider (Hair hue; Glasses frames / lens sliders)
    //   Colors  — color swatch strip (Face skin/eyes; Hair color presets)
    //   Tabs    — sub-tab strip (Intro/Victory for Catchphrase + Anim; Face/Eyes for Face; Frames/Lenses)
    //   Back    — the close-list button at the top-left of every panel (always present)
    // Up moves to the next entry, Down to the previous one; sections absent for the category are skipped.
    private enum Section { Items, Slider, Colors, Tabs, Back }
    private static readonly Section[] s_orderedSections = { Section.Items, Section.Slider, Section.Colors, Section.Tabs, Section.Back };
    private Section m_section = Section.Items;

    // Two-toggle pair backing whichever sub-tab strip the current category has.
    // Intro/Victory (phrase/anim panels): tab0=Intro, tab1=Victory; activation routes
    // through SelectCategory (game wires onValueChanged → SelectCategory). Face/Eyes
    // (face panel's color-strip switcher): tab0=Face, tab1=Eyes; activation calls the
    // game's OnFaceSelected/OnEyesSelected listeners which toggle the color group's
    // visibility WITHOUT changing _selectedCategory, so we re-announce here.
    private Toggle? m_tab0, m_tab1;
    private int m_tabIndex;

    // Color swatches in whichever group is currently visible (SkinToneF/M when Face tab
    // active, EyesF/M when Eyes tab active — both inside AvatarGroupObjectsController's
    // _colorGroupObjectDict). Rediscovered every time the user enters Colors because the
    // active group can change between visits (e.g. user swaps tab, comes back).
    private readonly List<Toggle> m_colorSwatches = new();
    private int m_colorIndex;
    private string m_colorSectionName = "";

    // The customization HUD's close button — the back arrow at the top-left of every
    // category panel. Lives on AvatarGroupObjectsController._closeListButton (game-native);
    // its onClick runs CloseList which saves customizations and tears down the panel.
    private Button? m_backButton;

    // Slider section state. Hair surfaces a single slider (hue). Glasses surfaces 1 (frames
    // colour) or 2 (lens colour + transparency) depending on which Frames/Lenses sub-tab is
    // active. Within the section Up/Down moves between sliders while Left/Right adjusts the focused
    // one's value. The game's wired onValueChanged on each slider applies the corresponding
    // colour/alpha — setting `.value` is enough; no extra apply call needed.
    private readonly List<Slider> m_sliders = new();
    private readonly List<LocalizationKey> m_sliderNames = new();
    private int m_sliderIndex;

    // Step size for arrow-key adjustments. 5% gives 20 discrete positions over the
    // slider's 0..1 range — fine enough for the gradient sliders (smooth color ramps)
    // without forcing the user to hold the key down forever.
    private const float SliderStep = 0.05f;

    internal static void Hide()
    {
        s_instance.m_shown = false;
        AccessibilityMgr.HideUI(s_instance);
    }

    public string GetHelp() => LocalizationUtils.Get(LocalizationKey.SOCIAL_AVATAR_HELP);

    public void OnGainedFocus()
    {
        DiscoverAll();
        AnnounceCurrent(includeHeader: m_announceHeader);
        m_announceHeader = false;
    }

    private void DiscoverAll()
    {
        DiscoverSubTabs();
        DiscoverColorSwatches();
        DiscoverSlider();
        m_backButton = ReadField<Button>(GroupObjects(), "_closeListButton");
        // The discovered Button (named 'CloseButton' in landscape, lives under 'TabButtonGroup')
        // is normally inactive — it's a click-handler endpoint wired by Initialize, not a
        // visible focused control. UnityEvent.onClick.Invoke routes through regardless of
        // activeInHierarchy, so the click works either way (CloseList fires, panel closes).
    }

    public void HandleInput()
    {
        if (m_controller == null) return;
        if (AccessibleKey.ESCAPE.IsPressed() || AccessibleKey.BACK.IsPressed())
        {
            CloseCategoryPanel();
            return;
        }
        switch (m_section)
        {
            case Section.Back: HandleBack(); break;
            case Section.Tabs: HandleTabs(); break;
            case Section.Colors: HandleColors(); break;
            case Section.Slider: HandleSlider(); break;
            default: HandleItems(); break;
        }
    }

    private static bool Activate() => AccessibleKey.CONFIRM.IsPressed() || AccessibleKey.SPACE.IsPressed();

    #region Items

    private void HandleItems()
    {
        var items = Items();
        if (items == null || items.Count == 0) return;

        // Per-row count detection differs by category. Phrase categories (CatchphraseIntro /
        // Victory) spawn their items into _phraseContent under a LayoutGroup driven by the
        // prefab — observed in landscape to be a vertical list (perRow=1). For non-phrase
        // categories the EnhancedScroller's `_itemsPerRow` gives us the column count.
        var perRow = Math.Max(1, IsPhraseCategory(m_category) ? DetectPhraseColumns() : ItemsPerRow());

        // Up at the top row escapes to the section above (tabs / colors / back). Other arrows stop at edges.
        if (AccessibleKey.READ_NEXT_ITEM.IsPressed()) MoveHorizontal(+1, items.Count, perRow);
        else if (AccessibleKey.READ_PREV_ITEM.IsPressed()) MoveHorizontal(-1, items.Count, perRow);
        else if (AccessibleKey.READ_NEXT_MENU_OPTION.IsPressed()) MoveVertical(+1, items.Count, perRow);
        else if (AccessibleKey.READ_PREV_MENU_OPTION.IsPressed()) { if (!MoveVertical(-1, items.Count, perRow)) EscapeUp(Section.Items); }
        else if (Activate()) ActivateCurrent();
    }

    // Stays inside the current row. For perRow=1 (vertical list) any horizontal move is a no-op —
    // exactly the desired behaviour for catchphrases.
    private bool MoveHorizontal(int direction, int total, int perRow)
    {
        var rowStart = m_index / perRow * perRow;
        var rowEnd = Math.Min(rowStart + perRow - 1, total - 1);
        var next = m_index + direction;
        if (next < rowStart || next > rowEnd) return false;
        m_index = next;
        AnnounceCurrent(includeHeader: false);
        return true;
    }

    // Jumps to the target row, clamping the column to whatever the target row actually has.
    private bool MoveVertical(int direction, int total, int perRow)
    {
        var targetRow = m_index / perRow + direction;
        if (targetRow < 0) return false;
        var rowStart = targetRow * perRow;
        if (rowStart >= total) return false;
        var target = Math.Min(rowStart + m_index % perRow, Math.Min(rowStart + perRow - 1, total - 1));
        if (target == m_index) return false;
        m_index = target;
        AnnounceCurrent(includeHeader: false);
        return true;
    }

    private int ItemsPerRow()
    {
        try { return s_itemsPerRow?.GetValue(m_controller) is int n ? Math.Max(1, n) : 1; }
        catch { return 1; }
    }

    private static bool IsPhraseCategory(AvatarCustomizationType c) =>
        c == AvatarCustomizationType.CatchphraseIntro || c == AvatarCustomizationType.CatchphraseVictory;

    // For CatchphraseIntro / Victory the items are pre-spawned CustomizationItem instances
    // inside _phraseContent, laid out by a LayoutGroup we don't know the shape of in advance
    // (the landscape prefab could be a grid while portrait is a vertical list). We read the
    // spawned items' world Y positions and count how many sit in the first row.
    private int DetectPhraseColumns()
    {
        var list = ReadField<System.Collections.IList>(m_controller, "_spawnedPhraseCustomizations");
        if (list == null || list.Count == 0) return 1;
        var firstY = float.NaN;
        int columns = 0;
        foreach (var obj in list)
        {
            if (obj is not Component comp || comp == null || !comp.gameObject.activeInHierarchy) continue;
            var y = comp.transform.position.y;
            if (float.IsNaN(firstY)) { firstY = y; columns = 1; continue; }
            if (Math.Abs(y - firstY) <= 1f) columns++;
            else break;
        }
        return Math.Max(1, columns);
    }

    private void ActivateCurrent()
    {
        m_controller!.SelectItemByIndex(m_index);
        // Selection may have changed equipped state — re-announce. The controller has updated
        // its _selectedItemIndex; we follow it so subsequent moves are relative to the new pick.
        if (s_selectedItemIndex?.GetValue(m_controller) is int selected && selected >= 0) m_index = selected;
        AnnounceCurrent(includeHeader: false);
    }

    private List<AvatarCustomizationData>? Items() => ReadField<List<AvatarCustomizationData>>(m_controller, "_currSortedItemData");

    private int EquippedIndex()
    {
        try { return s_getEquippedIndex?.Invoke(m_controller, null) is int i ? i : 0; }
        catch { return 0; }
    }

    private bool IsOwned(AvatarCustomizationData data)
    {
        try { return s_isOwned?.Invoke(m_controller, new object[] { data }) is true; }
        catch { return false; }
    }

    private string? ItemLabel(AvatarCustomizationData? data, int index, int total)
    {
        if (data == null) return null;
        var id = data.ID ?? "";
        string name;
        if (id.Contains("none"))
            name = LocalizationUtils.Get(LocalizationKey.SOCIAL_AVATAR_NONE);
        else if (m_category == AvatarCustomizationType.Face || m_category == AvatarCustomizationType.Hair)
            // FACE and HAIR item IDs aren't loc keys — the game itself hides item names for
            // these categories (HUBProfileController.IsValidItemNameCategory returns false).
            // Fall back to a position-based identifier so the user has a stable handle.
            name = LocalizationUtils.Format(LocalizationKey.SOCIAL_NUMBERED_FORMAT, CategoryName(m_category), index + 1);
        else
            name = LocalizationUtils.TryGetGame(id) ?? id;

        // Shop-like format: item, status/price, position. Status is "equipped" for the currently
        // worn item (matches GetIndexForEquippedCustOfCurrentType) or "owned" for unworn-owned,
        // or the price for unowned.
        string status;
        if (index == EquippedIndex()) status = LocalizationUtils.Get(LocalizationKey.SOCIAL_EQUIPPED);
        else if (IsOwned(data)) status = LocalizationUtils.Get(LocalizationKey.SOCIAL_OWNED);
        else if (data.Cost > 0) status = LocalizationUtils.Format(LocalizationKey.SOCIAL_PRICE_FORMAT, data.Cost, ShopLabels.CurrencyWord(data.currencyType));
        else status = LocalizationUtils.Get(LocalizationKey.GLOBAL_DISABLED);
        return Announce.Item(name, status, index + 1, total);
    }

    // Section name for the items list. For categories that share a panel with a sub-tab partner
    // (Catchphrase / Anim Intro/Victory), the tab name IS the section name — the panel-level category is
    // implicit context. For categories without sub-tabs the category name is the section name.
    private static string CategoryName(AvatarCustomizationType type) => type switch
    {
        AvatarCustomizationType.CatchphraseIntro or AvatarCustomizationType.AnimIntro => LocalizationUtils.GetGame("avatar_intro", LocalizationKey.SOCIAL_AVATAR_INTRO),
        AvatarCustomizationType.CatchphraseVictory or AvatarCustomizationType.AnimVictory => LocalizationUtils.GetGame("avatar_victory", LocalizationKey.SOCIAL_AVATAR_VICTORY),
        AvatarCustomizationType.Face => LocalizationUtils.GetGame("avatar_face", LocalizationKey.SOCIAL_AVATAR_FACE),
        AvatarCustomizationType.Eyes => LocalizationUtils.GetGame("avatar_eyes", LocalizationKey.SOCIAL_AVATAR_EYES),
        AvatarCustomizationType.Hair => LocalizationUtils.GetGame("avatar_hair", LocalizationKey.SOCIAL_AVATAR_HAIR),
        AvatarCustomizationType.Glasses => LocalizationUtils.GetGame("avatar_glasses", LocalizationKey.SOCIAL_AVATAR_GLASSES),
        AvatarCustomizationType.Hats => LocalizationUtils.GetGame("avatar_hats", LocalizationKey.SOCIAL_AVATAR_HATS),
        AvatarCustomizationType.Tops => LocalizationUtils.GetGame("avatar_tops", LocalizationKey.SOCIAL_AVATAR_TOPS),
        AvatarCustomizationType.Bottoms => LocalizationUtils.GetGame("avatar_bottoms", LocalizationKey.SOCIAL_AVATAR_BOTTOMS),
        AvatarCustomizationType.Shoes => LocalizationUtils.GetGame("avatar_shoes", LocalizationKey.SOCIAL_AVATAR_SHOES),
        AvatarCustomizationType.Pose => LocalizationUtils.GetGame("avatar_poses", LocalizationKey.SOCIAL_AVATAR_POSE),
        _ => type.ToString(),
    };

    #endregion

    #region Sub-tabs

    // Three flavours of sub-tab strip:
    //   • Intro/Victory pair — Catchphrase + Anim categories. Each tab switches _selectedCategory
    //     (via SelectCategory) and rebuilds the items panel.
    //   • Face/Eyes pair — Face category only. Tabs don't switch category; they sit above the
    //     color-swatch strip and toggle between skin-tone and eyes-color swatches.
    //   • Frames/Lenses pair — Glasses: switches which sliders are shown.
    private static bool HasIntroVictoryTabs(AvatarCustomizationType t) =>
        t is AvatarCustomizationType.CatchphraseIntro or AvatarCustomizationType.CatchphraseVictory
          or AvatarCustomizationType.AnimIntro or AvatarCustomizationType.AnimVictory;

    private bool PairTabsDontSwitchCategory => m_category is AvatarCustomizationType.Face or AvatarCustomizationType.Glasses;

    private string TabLabel(int index) => m_category switch
    {
        AvatarCustomizationType.Face => index == 0
            ? LocalizationUtils.GetGame("avatar_face", LocalizationKey.SOCIAL_AVATAR_FACE)
            : LocalizationUtils.GetGame("avatar_eyes", LocalizationKey.SOCIAL_AVATAR_EYES),
        AvatarCustomizationType.Glasses => index == 0
            ? LocalizationUtils.GetGame("avatar_frames", LocalizationKey.SOCIAL_AVATAR_FRAMES)
            : LocalizationUtils.GetGame("avatar_lenses", LocalizationKey.SOCIAL_AVATAR_LENSES),
        _ => index == 0
            ? LocalizationUtils.GetGame("avatar_intro", LocalizationKey.SOCIAL_AVATAR_INTRO)
            : LocalizationUtils.GetGame("avatar_victory", LocalizationKey.SOCIAL_AVATAR_VICTORY),
    };

    // Face/Eyes and Frames/Lenses live as named Toggle children inside a private GameObject SerializeField on
    // AvatarGroupObjectsController (_faceSelectorGameObject / _glassesSelectorGameObject) — the single
    // unambiguous anchor. Intro/Victory are ColoredEventTriggerToggles named "Intro" / "Victory", siblings
    // of the items scroller (phrase panel: _phraseScrollRect, anim panel: _avatarEnhancedScroller): walking
    // up from the scroller and scanning each ancestor finds the pair even when several instances exist in
    // the scene (landscape/portrait × phrase/anim = 4 each).
    private void DiscoverSubTabs()
    {
        m_tab0 = m_tab1 = null;
        if (m_controller == null) return;
        if (m_category == AvatarCustomizationType.Face)
        {
            FindTogglePair(ReadField<GameObject>(GroupObjects(), "_faceSelectorGameObject"), "Face", "Eyes");
            return;
        }
        if (m_category == AvatarCustomizationType.Glasses)
        {
            FindTogglePair(ReadField<GameObject>(GroupObjects(), "_glassesSelectorGameObject"), "Frames", "Lenses");
            return;
        }
        if (!HasIntroVictoryTabs(m_category)) return;

        Transform? anchor = IsPhraseCategory(m_category)
            ? ReadField<ScrollRect>(m_controller, "_phraseScrollRect")?.transform
            : ReadField<EnhancedScroller>(m_controller, "_avatarEnhancedScroller")?.transform;
        for (var t = anchor != null ? anchor.parent : null; t != null; t = t.parent)
        {
            ColoredEventTriggerToggle? intro = null, victory = null;
            foreach (var tog in t.GetComponentsInChildren<ColoredEventTriggerToggle>(includeInactive: true))
            {
                if (tog == null) continue;
                var n = tog.gameObject.name;
                if (intro == null && string.Equals(n, "Intro", StringComparison.OrdinalIgnoreCase)) intro = tog;
                else if (victory == null && string.Equals(n, "Victory", StringComparison.OrdinalIgnoreCase)) victory = tog;
            }
            if (intro != null && victory != null)
            {
                m_tab0 = intro.MyToggle;
                m_tab1 = victory.MyToggle;
                return;
            }
        }
    }

    private void FindTogglePair(GameObject? selector, string first, string second)
    {
        if (selector == null) return;
        Toggle? a = null, b = null;
        foreach (var t in selector.GetComponentsInChildren<Toggle>(includeInactive: true))
        {
            if (a == null && string.Equals(t.gameObject.name, first, StringComparison.OrdinalIgnoreCase)) a = t;
            else if (b == null && string.Equals(t.gameObject.name, second, StringComparison.OrdinalIgnoreCase)) b = t;
        }
        if (a != null && b != null) { m_tab0 = a; m_tab1 = b; }
    }

    // For Intro/Victory the source of truth is _selectedCategory (the game's own state). For Face/Eyes and
    // Frames/Lenses the category never changes, so read which toggle is on.
    private int ActiveTabIndex()
    {
        if (PairTabsDontSwitchCategory) return m_tab1 != null && m_tab1.isOn ? 1 : 0;
        return m_category is AvatarCustomizationType.CatchphraseVictory or AvatarCustomizationType.AnimVictory ? 1 : 0;
    }

    private void HandleTabs()
    {
        if (AccessibleKey.READ_NEXT_ITEM.IsPressed()) { if (m_tabIndex < 1) { m_tabIndex++; AnnounceTab(); } }
        else if (AccessibleKey.READ_PREV_ITEM.IsPressed()) { if (m_tabIndex > 0) { m_tabIndex--; AnnounceTab(); } }
        else if (AccessibleKey.READ_NEXT_MENU_OPTION.IsPressed()) EscapeDown(Section.Tabs);
        else if (AccessibleKey.READ_PREV_MENU_OPTION.IsPressed()) EscapeUp(Section.Tabs);
        else if (Activate()) ActivateTab();
    }

    private void AnnounceTab()
    {
        var sel = m_tabIndex == 0 ? m_tab0 : m_tab1;
        if (sel == null) return;
        sel.Select();
        AccessibilityMgr.Output(this, Announce.Item(TabLabel(m_tabIndex), null, m_tabIndex + 1, 2), interrupt: true);
    }

    private void ActivateTab()
    {
        var sel = m_tabIndex == 0 ? m_tab0 : m_tab1;
        if (sel == null) return;
        // Intro/Victory: setting isOn=true routes through the toggle's onValueChanged → the game's
        // OnCatchPhraseIntroSelected/… → HUBProfileController.SelectCategory, whose postfix re-announces.
        // Face/Eyes and Frames/Lenses: onValueChanged only toggles the color group / sliders — no
        // SelectCategory call — so we refresh and re-announce here.
        if (!sel.isOn) sel.isOn = true;
        if (PairTabsDontSwitchCategory)
        {
            DiscoverColorSwatches();
            DiscoverSlider();
            AnnounceTab();
        }
    }

    #endregion

    #region Colors

    // Source of truth is the game's own AvatarGroupObjectsController._colorGroupObjectDict — whichever
    // ColorGroupObjects' _localGameObject has activeInHierarchy=true is the strip the user sees, and its
    // _localScrollRect.content carries the swatch toggles (spawned via CreateColorToggleSwatch +
    // AssignAndRegisterUIToggleElement). Toggle ordering follows sibling index, which matches visual
    // left-to-right. Face → skin tone or eye color; Hair → hair-color presets.
    private void DiscoverColorSwatches()
    {
        m_colorSwatches.Clear();
        m_colorSectionName = "";
        if (m_category is not (AvatarCustomizationType.Face or AvatarCustomizationType.Hair)) return;
        var dict = ReadField<Dictionary<ColorGroupID, ColorGroupObjects>>(GroupObjects(), "_colorGroupObjectDict");
        if (dict == null) return;
        foreach (var pair in dict)
        {
            var group = pair.Value;
            if (group?._localGameObject == null || !group._localGameObject.activeInHierarchy) continue;
            var content = group._localScrollRect != null ? group._localScrollRect.content : null;
            if (content == null) return;
            foreach (var tog in content.GetComponentsInChildren<Toggle>(includeInactive: false))
                if (tog != null) m_colorSwatches.Add(tog);
            m_colorSectionName = ColorSectionName(pair.Key);
            return;
        }
    }

    private static string ColorSectionName(ColorGroupID id) => id switch
    {
        ColorGroupID.SkinToneF or ColorGroupID.SkinToneM => LocalizationUtils.Get(LocalizationKey.SOCIAL_AVATAR_SKIN_TONE),
        ColorGroupID.EyesF or ColorGroupID.EyesM => LocalizationUtils.GetGame("avatar_eyecolor", LocalizationKey.SOCIAL_AVATAR_EYE_COLOR),
        ColorGroupID.Hair => LocalizationUtils.Get(LocalizationKey.SOCIAL_AVATAR_HAIR_COLOR),
        _ => LocalizationUtils.Get(LocalizationKey.SOCIAL_AVATAR_COLOR),
    };

    private void HandleColors()
    {
        if (m_colorSwatches.Count == 0)
        {
            // Lost the swatches (group hidden by animation, etc.) — fall back into Items rather than
            // freezing the user in an empty section.
            m_section = Section.Items;
            AnnounceCurrent(includeHeader: true);
            return;
        }
        if (AccessibleKey.READ_NEXT_ITEM.IsPressed()) { if (m_colorIndex < m_colorSwatches.Count - 1) { m_colorIndex++; AnnounceColor(false); } }
        else if (AccessibleKey.READ_PREV_ITEM.IsPressed()) { if (m_colorIndex > 0) { m_colorIndex--; AnnounceColor(false); } }
        else if (AccessibleKey.READ_NEXT_MENU_OPTION.IsPressed()) EscapeDown(Section.Colors);
        else if (AccessibleKey.READ_PREV_MENU_OPTION.IsPressed()) EscapeUp(Section.Colors);
        else if (Activate()) ActivateColor();
    }

    // Swatches don't have human-readable names — they're keyed by IDs like "skintone_f_001". The best we
    // can do is the index; "equipped" comes from the toggle's own isOn (the game keeps a single-selected
    // invariant via ToggleGroup) so the user always knows which swatch is their current pick.
    private void AnnounceColor(bool includeHeader)
    {
        if (m_colorSwatches.Count == 0) return;
        if (m_colorIndex < 0 || m_colorIndex >= m_colorSwatches.Count) m_colorIndex = 0;
        var swatch = m_colorSwatches[m_colorIndex];
        if (swatch != null) swatch.Select();
        var status = LocalizationUtils.Get(swatch != null && swatch.isOn ? LocalizationKey.SOCIAL_EQUIPPED : LocalizationKey.SOCIAL_AVAILABLE);
        var label = Announce.Item(LocalizationUtils.Format(LocalizationKey.SOCIAL_AVATAR_COLOR_FORMAT, m_colorIndex + 1), status, m_colorIndex + 1, m_colorSwatches.Count);
        Speak(includeHeader ? m_colorSectionName : null, label);
    }

    private void ActivateColor()
    {
        var swatch = m_colorSwatches[m_colorIndex];
        if (swatch == null) return;
        // Setting isOn=true routes through the toggle's onValueChanged → game's OnFaceColorToggle /
        // OnEyesColorToggle / OnHairColorToggle, which apply the color via AvatarManager. The ToggleGroup
        // flips the previously-selected swatch off automatically. Re-announce to confirm.
        if (!swatch.isOn) swatch.isOn = true;
        AnnounceColor(false);
    }

    #endregion

    #region Sliders

    // Source of truth is the game's own private fields on AvatarGroupObjectsController — populated during
    // Initialize, with onValueChanged already wired to the apply call:
    //   _colorHairSlider     → OnProcessHairColorSlider     → ApplyHairColor
    //   _framesColorSlider   → OnProcessFrameColorSlider    → ApplyGlassesFrameColor
    //   _lensesColorSlider   → OnProcessLensesColorSlider   → ApplyGlassesLenseColor
    //   _lensesAlphaSlider   → OnProcessLensesAlphaSlider   → ApplyGlassesLenseColor (alpha-only)
    private void DiscoverSlider()
    {
        m_sliders.Clear();
        m_sliderNames.Clear();
        var ago = GroupObjects();
        if (m_category == AvatarCustomizationType.Hair)
        {
            AddSlider(ReadField<Slider>(ago, "_colorHairSlider"), LocalizationKey.SOCIAL_AVATAR_HAIR_TINT);
        }
        else if (m_category == AvatarCustomizationType.Glasses)
        {
            // Frames tab active → frames color slider only. Lenses tab active → lens color and transparency.
            if (m_tab1 != null && m_tab1.isOn)
            {
                AddSlider(ReadField<Slider>(ago, "_lensesColorSlider"), LocalizationKey.SOCIAL_AVATAR_LENS_COLOR);
                AddSlider(ReadField<Slider>(ago, "_lensesAlphaSlider"), LocalizationKey.SOCIAL_AVATAR_LENS_TRANSPARENCY);
            }
            else
            {
                AddSlider(ReadField<Slider>(ago, "_framesColorSlider"), LocalizationKey.SOCIAL_AVATAR_FRAMES_COLOR);
            }
        }
        if (m_sliderIndex >= m_sliders.Count) m_sliderIndex = 0;
    }

    private void AddSlider(Slider? slider, LocalizationKey name)
    {
        if (slider == null) return;
        m_sliders.Add(slider);
        m_sliderNames.Add(name);
    }

    private void HandleSlider()
    {
        if (m_sliders.Count == 0) { EscapeDown(Section.Slider); return; }
        if (AccessibleKey.READ_NEXT_ITEM.IsPressed()) AdjustSlider(+1);
        else if (AccessibleKey.READ_PREV_ITEM.IsPressed()) AdjustSlider(-1);
        // Walk the stacked sliders within the section first; only escape once at the edge.
        else if (AccessibleKey.READ_PREV_MENU_OPTION.IsPressed()) { if (m_sliderIndex > 0) { m_sliderIndex--; AnnounceSlider(true); } else EscapeUp(Section.Slider); }
        else if (AccessibleKey.READ_NEXT_MENU_OPTION.IsPressed()) { if (m_sliderIndex < m_sliders.Count - 1) { m_sliderIndex++; AnnounceSlider(true); } else EscapeDown(Section.Slider); }
        // No Enter activation — sliders commit via Left/Right value changes.
    }

    private void AnnounceSlider(bool includeName)
    {
        if (m_sliderIndex < 0 || m_sliderIndex >= m_sliders.Count) return;
        var slider = m_sliders[m_sliderIndex];
        slider.Select();
        var pct = LocalizationUtils.Format(LocalizationKey.SOCIAL_PERCENT_FORMAT, Mathf.RoundToInt(slider.value * 100f));
        AccessibilityMgr.Output(this, includeName ? SocialUi.Join(LocalizationUtils.Get(m_sliderNames[m_sliderIndex]), pct) : pct, interrupt: true);
    }

    private void AdjustSlider(int direction)
    {
        var slider = m_sliders[m_sliderIndex];
        var value = Mathf.Clamp01(slider.value + direction * SliderStep);
        if (Mathf.Approximately(value, slider.value)) return;
        slider.value = value;   // fires the game's apply call; the avatar updates synchronously
        AnnounceSlider(false);
    }

    #endregion

    #region Sections

    private void EscapeUp(Section from)
    {
        for (int i = Array.IndexOf(s_orderedSections, from) + 1; i < s_orderedSections.Length; i++)
            if (TryEnterSection(s_orderedSections[i])) return;
    }

    private void EscapeDown(Section from)
    {
        for (int i = Array.IndexOf(s_orderedSections, from) - 1; i >= 0; i--)
            if (TryEnterSection(s_orderedSections[i])) return;
    }

    private bool TryEnterSection(Section target)
    {
        switch (target)
        {
            case Section.Back:
                if (m_backButton == null) return false;
                m_section = Section.Back;
                AnnounceBack();
                return true;
            case Section.Tabs:
                if (m_tab0 == null || m_tab1 == null) return false;
                m_section = Section.Tabs;
                m_tabIndex = ActiveTabIndex();
                AnnounceTab();
                return true;
            case Section.Colors:
                DiscoverColorSwatches();
                if (m_colorSwatches.Count == 0) return false;
                m_section = Section.Colors;
                m_colorIndex = EquippedColorIndex();
                AnnounceColor(includeHeader: true);
                return true;
            case Section.Slider:
                DiscoverSlider();
                if (m_sliders.Count == 0) return false;
                m_section = Section.Slider;
                m_sliderIndex = 0;   // most panels have one; for Lenses (2) the top is the predictable default
                AnnounceSlider(true);
                return true;
            default:
                m_section = Section.Items;
                AnnounceCurrent(includeHeader: true);
                return true;
        }
    }

    // Index of the equipped (isOn) swatch in the current strip, or 0 if none: default focus lands on the
    // player's current pick.
    private int EquippedColorIndex()
    {
        for (int i = 0; i < m_colorSwatches.Count; i++)
            if (m_colorSwatches[i] != null && m_colorSwatches[i].isOn) return i;
        return 0;
    }

    private void HandleBack()
    {
        if (m_backButton == null) { EscapeDown(Section.Back); return; }
        if (AccessibleKey.READ_NEXT_MENU_OPTION.IsPressed()) EscapeDown(Section.Back);
        // Invoking the close button's onClick routes through AvatarGroupObjectsController.CloseList —
        // which calls SaveHairAndGlassesColorIfChanged, ResetFromClose, ResetProfileItemsForClose,
        // SetState(None), and DeActivateAll in the right order. ResetFromClose triggers our hook → Hide.
        else if (Activate()) m_backButton.onClick?.Invoke();
    }

    private void AnnounceBack()
    {
        if (m_backButton != null) m_backButton.Select();
        AccessibilityMgr.Output(this, LocalizationUtils.GetGame("global_back", LocalizationKey.SOCIAL_BACK), interrupt: true);
    }

    #endregion

    private void AnnounceCurrent(bool includeHeader)
    {
        if (m_controller == null) return;
        // Tabs / Colors / Back are independent of the items list — handle them first so a
        // momentarily-empty _currSortedItemData (e.g. mid-transition) doesn't suppress them.
        switch (m_section)
        {
            case Section.Back: AnnounceBack(); return;
            case Section.Tabs: m_tabIndex = ActiveTabIndex(); AnnounceTab(); return;
            case Section.Colors: AnnounceColor(includeHeader); return;
            case Section.Slider: AnnounceSlider(includeHeader); return;
        }
        var items = Items();
        if (items == null || items.Count == 0) return;
        if (m_index < 0 || m_index >= items.Count) m_index = 0;
        var label = ItemLabel(items[m_index], m_index, items.Count);
        if (!string.IsNullOrEmpty(label)) Speak(includeHeader ? CategoryName(m_category) : null, label!);
    }

    // Header (interrupting) then the item (queued), or the item alone (interrupting).
    private void Speak(string? header, string item)
    {
        if (!string.IsNullOrEmpty(header))
        {
            AccessibilityMgr.Output(this, header!, interrupt: true);
            AccessibilityMgr.Output(this, item, interrupt: false);
        }
        else
        {
            AccessibilityMgr.Output(this, item, interrupt: true);
        }
    }

    private AvatarGroupObjectsController? GroupObjects() => ReadField<AvatarGroupObjectsController>(m_controller, "_avatarGroupObjectsController");

    private void CloseCategoryPanel()
    {
        // Close the panel FIRST. SetState(None) → SetCurrentAvatarStateAndItemsMenu(Closed)
        // synchronously sets _currentUIState back to Closed and toggles _zoomOutIcon.SetActive
        // off; only the items-menu animator transition is async. Doing this before hiding
        // means Profile's rebuild on regaining focus sees the (mostly) correct outer-profile
        // state instead of the still-open one — otherwise ZoomOutIcon leaks into the catch-all.
        m_controller!.SetState(AvatarCustomizationType.None);
        Hide();
    }

    [HarmonyPatch(typeof(HUBProfileController), "SelectCategory")]
    private static class SelectCategoryHook
    {
        private static void Postfix(HUBProfileController __instance, AvatarCustomizationType category)
        {
            var self = s_instance;
            if (category == AvatarCustomizationType.None)
            {
                // Game closed the panel by setting None.
                Hide();
                return;
            }
            self.m_controller = __instance;
            var wasInTabs = self.m_section == Section.Tabs;
            self.m_category = category;
            // When the user activated a sub-tab (still in Tabs section), reset items focus to
            // the top of the new category — landing on the equipped item after a tab switch is
            // confusing because the position isn't predictable. Initial panel-open keeps the
            // equipped-item focus so the user immediately hears their current selection.
            self.m_index = self.m_shown && wasInTabs ? 0 : self.EquippedIndex();
            if (!self.m_shown)
            {
                self.m_shown = true;
                self.m_section = Section.Items;   // always land on items first
                self.m_announceHeader = true;
                AccessibilityMgr.ShowUI(self);
            }
            else
            {
                // Category switched while already shown (a sub-tab, or the game's own logic): re-announce
                // the new category.
                self.DiscoverAll();
                self.AnnounceCurrent(includeHeader: !wasInTabs);
            }
        }
    }

    [HarmonyPatch(typeof(HUBProfileController), "ResetFromClose")]
    private static class ResetFromCloseHook
    {
        private static void Postfix(HUBProfileController __instance)
        {
            if (s_instance.m_controller != __instance) return;
            Hide();
        }
    }
}
