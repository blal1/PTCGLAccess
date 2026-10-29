using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using TMPro;
using TPCI.Localization;
using UnityEngine;
using UnityEngine.UI;
using static Accessibility.GameReflect;

namespace Accessibility;

// The HUB settings overlay (⋮ menu → SETTINGS). The current client can show either the legacy flat panel or a
// tabbed Accessibility / General layout. All discovery is scoped to the hierarchy that is live now: the
// inactive legacy controller is never used as a source for the tabbed General panel.
//
// Open: HUBOverflowMenuController.OnClickSettings (it OpenOverlay's _oldSettingsController or
// _newSettingsController). Close: the shared OverlayScreenGroup.OnDeActivate with an identity guard.
//
// Keys: Up/Down move (and cross sections at a section's edge), Tab / Shift+Tab cycle sections, Left/Right
// change the focused slider, resolution or on/off setting, Enter flips an on/off setting or commits the
// previewed resolution, Escape closes.
internal sealed class AccessibleSettings : AccessibleUI
{
    private static readonly AccessibleSettings s_instance = new();

    private static readonly FieldInfo? s_currentSettingsType = AccessTools.Field(typeof(HUBTabbedSettingsController), "_currentSettingsType");

    private readonly List<KeyboardNavigator> m_groups = new();
    private readonly Dictionary<Toggle, PageDragTab> m_tabs = new();
    private readonly Dictionary<HUBTabbedSettingsController.SettingsType, PageDragTab> m_typedTabs = new();

    private int m_activeGroup = -1;
    private int m_tabGroupIndex = -1;
    private OverlayManager? m_overlayManager;
    private OverlayScreenGroup? m_group;

    // The live hierarchy identity. HandleInput compares this before and after input so a tab change is
    // rebuilt synchronously, while the controller's initial deferred panel activation is handled
    // by m_pendingContent below.
    private HUBTabbedSettingsController? m_tabbedController;
    private HUBTabbedSettingsController.SettingsType m_settingsType = HUBTabbedSettingsController.SettingsType.None;
    private GameObject? m_activePanel;
    private HUBOverflowSettingsController? m_generalController;
    private MonoBehaviour? m_accessibilityController;

    // General / legacy controls.
    private Button? m_back, m_migrate;
    private Slider? m_music, m_sfx;
    private TMP_Dropdown? m_res;
    private Toggle? m_windowed, m_hover;

    // Accessibility controls. Selector is not a Selectable, so one of its real arrow Buttons is
    // the focus proxy.
    private Toggle? m_frontEndVfx, m_battleVfx;
    private Selector? m_foilSelector;
    private Button? m_foilFocus;
    private LocText? m_foilDescription, m_foilValueLabel;
    private LocText? m_frontEndDescription, m_frontEndValueLabel;
    private LocText? m_battleDescription, m_battleValueLabel;

    // Resolution preview state: arrows preview without firing the confirmation modal; Enter commits.
    private int m_resCommitted, m_resPreview;
    private Selectable? m_lastSel;

    // HUBTabbedSettingsController.Start deliberately has a no-live-panel interval. Rebuild from the
    // live hierarchy until panel controls exist, bounded by the overlay's own settled-state signal.
    private bool m_pendingContent;

    public string GetHelp() => LocalizationUtils.Get(LocalizationKey.SETTINGS_HELP);

    public void OnGainedFocus() => Rebuild(announce: true);

    public void HandleInput()
    {
        // Back is handled HERE, before any content check, so the panel is always closable. Every
        // path below can legitimately return early (content not up yet, live hierarchy changed, no
        // groups built) and each of those used to swallow Escape, leaving the player sealed inside
        // a screen with no way out — the worst failure this screen can have.
        // Escape only: Backspace still goes through the navigator, which suppresses it while a text
        // field has focus so it can delete characters instead of closing the screen.
        if (AccessibleKey.ESCAPE.IsPressed()) { Close(); return; }

        if (m_pendingContent || LiveHierarchyChanged()) { Rebuild(announce: true); return; }
        if (m_activeGroup < 0 || m_activeGroup >= m_groups.Count) return;

        var nav = m_groups[m_activeGroup];
        var cur = nav.Current;

        // Restore an uncommitted resolution preview when focus leaves the dropdown.
        if (m_lastSel == (Selectable?)m_res && cur != (Selectable?)m_res) RestoreResolutionPreview();
        if (cur == (Selectable?)m_res && m_lastSel != (Selectable?)m_res && m_res != null) m_resCommitted = m_resPreview = m_res.value;
        m_lastSel = cur;

        bool left = AccessibleKey.READ_PREV_ITEM.IsPressed();
        bool right = AccessibleKey.READ_NEXT_ITEM.IsPressed();
        if (left || right)
        {
            if (cur is Slider slider && slider.interactable)
            {
                float step = (slider.maxValue - slider.minValue) * 0.1f;
                if (step <= 0f) step = 0.1f;
                slider.value = Mathf.Clamp(slider.value + (right ? step : -step), slider.minValue, slider.maxValue);
                nav.RefocusCurrent(announce: true);
                return;
            }
            if (cur == (Selectable?)m_res && m_res != null && m_res.options != null && m_res.options.Count > 0)
            {
                m_resPreview = (m_resPreview + (right ? 1 : -1) + m_res.options.Count) % m_res.options.Count;
                m_res.SetValueWithoutNotify(m_resPreview);
                m_res.RefreshShownValue();
                AccessibilityMgr.Output(this, LabelFor(m_res), interrupt: true);
                return;
            }
            // A two-state setting reads like every other value on this screen, so it is driven like
            // one: Right turns it on, Left turns it off. Deterministic rather than a flip, so the
            // same key always produces the same result. Assigning isOn fires the game's own
            // onValueChanged, which is what applies and saves the setting.
            if (IsSettingToggle(cur) && cur is Toggle sideToggle)
            {
                if (sideToggle.isOn != right)
                {
                    sideToggle.isOn = right;
                    nav.RefocusCurrent(announce: true);
                }
                return;
            }
        }

        bool enter = AccessibleKey.CONFIRM.IsPressed();
        bool space = AccessibleKey.SPACE.IsPressed();
        if (enter && cur == (Selectable?)m_res && m_res != null)
        {
            // Restore the committed value without notification, then assign the preview so exactly
            // one onValueChanged fires and the game's confirmation modal opens once.
            if (m_resPreview != m_resCommitted)
            {
                m_res.SetValueWithoutNotify(m_resCommitted);
                m_res.value = m_resPreview;
                m_resCommitted = m_resPreview;
            }
            return;
        }
        // The foil row is read-only (see FoilAnimationsLabel): swallow Enter / Space so its focus
        // proxy can never fire the arrow Button behind it.
        if ((enter || space) && cur == (Selectable?)m_foilFocus) return;

        // Enter flips a setting. KeyboardNavigator deliberately reserves checkbox toggling for Space
        // (the desktop convention for a form checkbox), but these rows are settings rather than form
        // fields: Enter is what a player reaches for. Space still works through the navigator.
        if (enter && IsSettingToggle(cur) && cur is Toggle enterToggle)
        {
            enterToggle.isOn = !enterToggle.isOn;
            nav.RefocusCurrent(announce: true);
            return;
        }

        // Tab cycles sections. The per-section navigators never consume Tab themselves.
        var nextSection = AccessibleKey.NEXT_SECTION.IsPressed();
        if (nextSection || AccessibleKey.PREV_SECTION.IsPressed())
        {
            if (m_groups.Count > 1)
            {
                m_activeGroup = (m_activeGroup + (nextSection ? 1 : -1) + m_groups.Count) % m_groups.Count;
                m_groups[m_activeGroup].FocusFirst(announce: true, interruptPrior: true);
            }
            return;
        }

        // Section escape: Up at a group's top row enters the previous section from below; Down at
        // the bottom row enters the next section from above.
        if (AccessibleKey.READ_PREV_MENU_OPTION.IsPressed() && m_activeGroup > 0 && nav.IsAtTopRow)
        {
            int column = nav.CurrentColumn;
            m_activeGroup--;
            m_groups[m_activeGroup].FocusLastRow(column, announce: true, interruptPrior: true);
            return;
        }
        if (AccessibleKey.READ_NEXT_MENU_OPTION.IsPressed() && m_activeGroup < m_groups.Count - 1 && nav.IsAtBottomRow)
        {
            int column = nav.CurrentColumn;
            m_activeGroup++;
            m_groups[m_activeGroup].FocusFirstRow(column, announce: true, interruptPrior: true);
            return;
        }

        nav.HandleInput();

        // RadioToggleMode just drove the PageDragTab's real Toggle event. The controller swaps the
        // mapped panel synchronously, so re-present the new live panel immediately.
        if (LiveHierarchyChanged()) Rebuild(announce: true);
    }

    private void Rebuild(bool announce)
    {
        try { RebuildCore(announce); }
        catch (Exception ex)
        {
            m_pendingContent = false;
            Log.Error($"Settings.Rebuild threw: {ex.Message}");
        }
    }

    private void RebuildCore(bool announce)
    {
        if (m_group == null) return;

        RestoreResolutionPreview();

        if (m_group.transform is RectTransform rect)
        {
            Canvas.ForceUpdateCanvases();
            LayoutRebuilder.ForceRebuildLayoutImmediate(rect);
        }

        ClearDiscoveredState();

        // Fixed stack position 1: Back. Escape is also wired to the same close path.
        m_back = m_group.GetComponentsInChildren<Button>(includeInactive: false)
            .FirstOrDefault(b => b != null && b.isActiveAndEnabled && b.gameObject.name.IndexOf("Back", StringComparison.OrdinalIgnoreCase) >= 0);
        if (m_back != null) AddGroup(new List<Selectable> { m_back }, groupName: null);

        // Fixed stack position 2: the tab strip, if the tabbed controller is actually live.
        m_tabbedController = FindLiveTabbedController();
        if (m_tabbedController != null)
        {
            var tabItems = DiscoverTabs(m_tabbedController);
            if (tabItems.Count > 0)
            {
                m_tabGroupIndex = m_groups.Count;
                // No group name: the HUB top tab strip announces only the tab itself, and these tabs
                // are the same kind of control, so they read the same way.
                var tabNav = AddGroup(tabItems.Cast<Selectable>().ToList(), groupName: null, horizontal: true, radioMode: true);
                int selected = tabItems.FindIndex(t => t.isOn);
                if (selected >= 0) tabNav.FocusIndex(selected, announce: false, interrupt: false);
            }
            m_activePanel = FindActiveTabbedPanel(m_tabbedController, out m_settingsType);
        }

        int panelStart = m_groups.Count;
        if (m_tabbedController != null)
        {
            if (m_activePanel != null)
            {
                if (m_settingsType == HUBTabbedSettingsController.SettingsType.General)
                {
                    m_generalController = FindLiveGeneralController(m_activePanel);
                    if (m_generalController != null) BuildGeneralSections(m_generalController, m_activePanel);
                }
                else if (m_settingsType == HUBTabbedSettingsController.SettingsType.Accessibility)
                {
                    BuildAccessibilitySection(m_activePanel);
                }
            }
        }
        else
        {
            // Legacy route: only an active-and-enabled controller is eligible. This excludes the
            // dead _origSettings controller when the switcher chose the tabbed layout.
            m_generalController = FindLiveGeneralController(m_group.gameObject);
            if (m_generalController != null) BuildGeneralSections(m_generalController, FindLegacyPanelRoot(m_generalController));
        }

        int panelSectionCount = m_groups.Count - panelStart;
        bool contentExpected = m_tabbedController != null || m_generalController != null;
        m_pendingContent = contentExpected && panelSectionCount == 0 && !ShowSettled();
        if (m_pendingContent) return;

        // Start at the first live settings section, while Back and the tab strip remain one Up-arrow
        // away. If content is unavailable, fall back to the tab strip and then Back rather than leaving
        // the screen unfocused.
        if (panelSectionCount > 0) m_activeGroup = panelStart;
        else if (m_tabGroupIndex >= 0) m_activeGroup = m_tabGroupIndex;
        else m_activeGroup = m_groups.Count > 0 ? 0 : -1;

        if (!announce || m_activeGroup < 0) return;
        AccessibilityMgr.Output(this, LocalizationUtils.GetGame("overflow_settings", LocalizationKey.SETTINGS_TITLE), interrupt: true);
        m_groups[m_activeGroup].FocusFirst(announce: true, interruptPrior: false);
    }

    private void ClearDiscoveredState()
    {
        foreach (var g in m_groups) g.Detach();
        m_groups.Clear();
        m_tabs.Clear();
        m_typedTabs.Clear();
        m_activeGroup = -1;
        m_tabGroupIndex = -1;
        m_tabbedController = null;
        m_settingsType = HUBTabbedSettingsController.SettingsType.None;
        m_activePanel = null;
        m_generalController = null;
        m_accessibilityController = null;

        m_back = m_migrate = null;
        m_music = m_sfx = null;
        m_res = null;
        m_windowed = m_hover = null;

        m_frontEndVfx = m_battleVfx = null;
        m_foilSelector = null;
        m_foilFocus = null;
        m_foilDescription = m_foilValueLabel = null;
        m_frontEndDescription = m_frontEndValueLabel = null;
        m_battleDescription = m_battleValueLabel = null;
        m_lastSel = null;
    }

    private HUBTabbedSettingsController? FindLiveTabbedController() =>
        m_group == null ? null : m_group.GetComponentsInChildren<HUBTabbedSettingsController>(includeInactive: false).FirstOrDefault(c => c != null && c.isActiveAndEnabled);

    private static HUBOverflowSettingsController? FindLiveGeneralController(GameObject root) =>
        root.GetComponentsInChildren<HUBOverflowSettingsController>(includeInactive: false).FirstOrDefault(c => c != null && c.isActiveAndEnabled);

    private List<Toggle> DiscoverTabs(HUBTabbedSettingsController controller)
    {
        var result = new List<Toggle>();
        var options = ReadMap<PageDragTab>(controller, "_options");
        if (options == null) return result;

        foreach (var pair in options)
        {
            var tab = pair.Value;
            if (tab == null || !tab.gameObject.activeInHierarchy) continue;
            var toggle = tab.toggle;
            if (toggle == null || !toggle.isActiveAndEnabled) continue;
            m_tabs[toggle] = tab;
            m_typedTabs[pair.Key] = tab;
            result.Add(toggle);
        }
        result.Sort((a, b) => SocialUi.ReadingOrder(a.transform).CompareTo(SocialUi.ReadingOrder(b.transform)));
        return result;
    }

    private static GameObject? FindActiveTabbedPanel(HUBTabbedSettingsController controller, out HUBTabbedSettingsController.SettingsType type)
    {
        type = s_currentSettingsType?.GetValue(controller) is HUBTabbedSettingsController.SettingsType t ? t : HUBTabbedSettingsController.SettingsType.None;
        var panels = ReadMap<GameObject>(controller, "_panels");
        if (panels == null) return null;

        foreach (var pair in panels)
            if (pair.Key == type && pair.Value != null && pair.Value.activeInHierarchy)
                return pair.Value;

        // Fail closed on a stale _currentSettingsType: only accept a panel that is actually live.
        foreach (var pair in panels)
            if (pair.Value != null && pair.Value.activeInHierarchy)
            {
                type = pair.Key;
                return pair.Value;
            }
        return null;
    }

    // The tab controller's _options / _panels are TPCI `ReorderableDictionary`s. They implement the
    // NON-generic IDictionary too, but their explicit `IDictionary.GetEnumerator()` returns the
    // inner `Dictionary<K,V>`'s own struct enumerator — whose IEnumerator.Current yields a boxed
    // KeyValuePair, not a DictionaryEntry. So `foreach (DictionaryEntry e in map)` throws
    // InvalidCastException ("Specified cast is not valid") on the first element, every frame.
    // Enumerate the GENERIC interface instead, which is what the object actually yields.
    private static IEnumerable<KeyValuePair<HUBTabbedSettingsController.SettingsType, TValue>>? ReadMap<TValue>(HUBTabbedSettingsController instance, string name) =>
        name switch
        {
            "_options" => ReadField<object>(instance, "_options"),
            _ => ReadField<object>(instance, "_panels"),
        } as IEnumerable<KeyValuePair<HUBTabbedSettingsController.SettingsType, TValue>>;

    // The toggles on this screen that represent a SETTING. Deliberately by reference: the tab strip
    // is made of Toggles too, and it must keep its radio behaviour rather than being flipped.
    private bool IsSettingToggle(Selectable? s) =>
        s != null && (ReferenceEquals(s, m_frontEndVfx) || ReferenceEquals(s, m_battleVfx) || ReferenceEquals(s, m_hover) || ReferenceEquals(s, m_windowed));

    private bool LiveHierarchyChanged()
    {
        if (m_group == null) return false;
        try
        {
            var tabbed = FindLiveTabbedController();
            if (tabbed != m_tabbedController) return true;
            if (tabbed != null)
            {
                var panel = FindActiveTabbedPanel(tabbed, out var type);
                return panel != m_activePanel || type != m_settingsType;
            }
            return FindLiveGeneralController(m_group.gameObject) != m_generalController;
        }
        catch (Exception ex)
        {
            Log.Error($"Settings: live-state check failed: {ex.Message}");
            return false;
        }
    }

    private GameObject FindLegacyPanelRoot(HUBOverflowSettingsController controller)
    {
        foreach (var switcher in m_group!.GetComponentsInChildren<HUBOverflowSettingsSwitcher>(includeInactive: true))
        {
            var root = ReadField<GameObject>(switcher, "_origSettings");
            if (root != null && root.activeInHierarchy && (controller.transform == root.transform || controller.transform.IsChildOf(root.transform)))
                return root;
        }
        return m_group.gameObject;
    }

    private void BuildGeneralSections(HUBOverflowSettingsController controller, GameObject panelRoot)
    {
        var sections = new List<(string? name, List<Selectable> items)>();

        m_migrate = ReadField<Button>(controller, "_migrateButton");
        var migrateGroup = ReadField<GameObject>(controller, "_migrateGroup");
        if (Active(m_migrate) && (migrateGroup == null || migrateGroup.activeInHierarchy))
        {
            var account = SocialUi.TextOf(migrateGroup != null ? migrateGroup.GetComponentInChildren<LocText>(includeInactive: false) : null);
            if (string.IsNullOrWhiteSpace(account)) account = LocalizationUtils.GetGame("startup_tcgomigrate_title", LocalizationKey.SETTINGS_MIGRATE);
            sections.Add((account, new List<Selectable> { m_migrate! }));
        }

        m_music = ReadField<Slider>(controller, "_musicSlider");
        m_sfx = ReadField<Slider>(controller, "_sfxSlider");
        var sound = LiveItems(m_music, m_sfx);
        if (sound.Count > 0) sections.Add((LocalizationUtils.GetGame("settings_sound", LocalizationKey.SETTINGS_SOUND), sound));

        m_res = ReadField<TMP_Dropdown>(controller, "_resolutionsSelector");
        m_windowed = ReadField<Toggle>(controller, "_fullScreenToggle");
        var video = LiveItems(m_res, m_windowed);
        if (video.Count > 0) sections.Add((LocalizationUtils.GetGame("overflow_video_options", LocalizationKey.SETTINGS_VIDEO), video));

        m_hover = ReadField<Toggle>(controller, "_cardHoverEnabledToggle");
        // The game removed the reduced-effects switch from this tab (its replacements are the Accessibility tab options).
        var battle = LiveItems(m_hover);
        if (battle.Count > 0) sections.Add((LocalizationUtils.GetGame("settings_battle_options", LocalizationKey.SETTINGS_BATTLE), battle));

        var languages = panelRoot.GetComponentsInChildren<LanguageSelectionButton>(includeInactive: false)
            .Select(lb => (Selectable?)(ReadField<Button>(lb, "_myButton") ?? lb.GetComponentInChildren<Button>(true)))
            .Where(s => s != null && s.isActiveAndEnabled)
            .Select(s => s!)
            .OrderBy(s => SocialUi.ReadingOrder(s.transform))
            .ToList();
        if (languages.Count > 0) sections.Add((LocalizationUtils.GetGame("settings_language", LocalizationKey.SETTINGS_LANGUAGE), languages));

        sections.Sort((a, b) => SectionKey(a.items).CompareTo(SectionKey(b.items)));
        foreach (var section in sections) AddGroup(section.items, section.name);
    }

    // Two controllers carry this panel (both with the exact serialized field names below), so each field is read
    // from whichever is live. The previous client's "game board animations" row no longer exists in either.
    private void BuildAccessibilitySection(GameObject panel)
    {
        var battleCtrl = panel.GetComponentsInChildren<BattleAccessibilitySettingsController>(includeInactive: false).FirstOrDefault(c => c != null && c.isActiveAndEnabled);
        var hubCtrl = battleCtrl != null ? null
            : panel.GetComponentsInChildren<HUBOverflowAccessibilitySettingsController>(includeInactive: false).FirstOrDefault(c => c != null && c.isActiveAndEnabled);
        m_accessibilityController = battleCtrl != null ? battleCtrl : hubCtrl;
        if (m_accessibilityController == null) return;

        m_foilSelector = ReadField<Selector>(battleCtrl, "_cardFoilAnimsSelector") ?? ReadField<Selector>(hubCtrl, "_cardFoilAnimsSelector");
        m_foilDescription = ReadField<LocText>(battleCtrl, "_cardFoilAnimsDescription") ?? ReadField<LocText>(hubCtrl, "_cardFoilAnimsDescription");
        if (m_foilSelector != null)
        {
            var leftArrow = ReadField<Button>(m_foilSelector, "leftButton");
            var rightArrow = ReadField<Button>(m_foilSelector, "rightButton");
            m_foilValueLabel = ReadField<LocText>(m_foilSelector, "locText");
            m_foilFocus = Active(rightArrow) ? rightArrow : Active(leftArrow) ? leftArrow : null;
        }

        m_frontEndVfx = ReadField<Toggle>(battleCtrl, "_useAmbientFrontEndVFXToggle") ?? ReadField<Toggle>(hubCtrl, "_useAmbientFrontEndVFXToggle");
        m_frontEndDescription = ReadField<LocText>(battleCtrl, "_useAmbientFrontEndVFXDescription") ?? ReadField<LocText>(hubCtrl, "_useAmbientFrontEndVFXDescription");
        m_frontEndValueLabel = ReadField<LocText>(battleCtrl, "_useAmbientFrontEndVFXLabel") ?? ReadField<LocText>(hubCtrl, "_useAmbientFrontEndVFXLabel");

        m_battleVfx = ReadField<Toggle>(battleCtrl, "_useCardBattleVFXToggle") ?? ReadField<Toggle>(hubCtrl, "_useCardBattleVFXToggle");
        m_battleDescription = ReadField<LocText>(battleCtrl, "_useCardBattleVFXDescription") ?? ReadField<LocText>(hubCtrl, "_useCardBattleVFXDescription");
        m_battleValueLabel = ReadField<LocText>(battleCtrl, "_useCardBattleVFXLabel") ?? ReadField<LocText>(hubCtrl, "_useCardBattleVFXLabel");

        var items = LiveItems(m_foilFocus, m_frontEndVfx, m_battleVfx);
        if (items.Count > 0) AddGroup(items, PanelName(m_settingsType));
    }

    private string? PanelName(HUBTabbedSettingsController.SettingsType type) =>
        m_typedTabs.TryGetValue(type, out var tab) && tab != null ? TabName(tab) : null;

    private static List<Selectable> LiveItems(params Selectable?[] items) => items.Where(Active).Select(i => i!).ToList();

    private KeyboardNavigator AddGroup(List<Selectable> items, string? groupName, bool horizontal = false, bool radioMode = false)
    {
        var nav = new KeyboardNavigator(this)
        {
            AllowTab = false,
            RadioToggleMode = radioMode,
            GroupName = groupName,
            OnBack = Close,
        };
        nav.SetItems(items, LabelFor);
        nav.AllowVertical = !horizontal;
        nav.AllowHorizontal = horizontal;
        m_groups.Add(nav);
        return nav;
    }

    private static float SectionKey(List<Selectable> items) =>
        items.Where(i => i != null).Select(i => SocialUi.ReadingOrder(i.transform)).DefaultIfEmpty(float.MaxValue).Min();

    private void RestoreResolutionPreview()
    {
        if (m_res == null || m_resPreview == m_resCommitted) return;
        m_res.SetValueWithoutNotify(m_resCommitted);
        m_res.RefreshShownValue();
        m_resPreview = m_resCommitted;
    }

    private void Close()
    {
        m_pendingContent = false;
        RestoreResolutionPreview();
        try
        {
            if (m_overlayManager != null && m_group != null && m_overlayManager.IsOverlayOpen(m_group))
            {
                m_overlayManager.CloseOverlay(m_group);
                return;   // OnDeActivate hides us
            }
        }
        catch (Exception ex) { Log.Error($"Settings.Close threw: {ex.Message}"); }
        m_group = null;
        AccessibilityMgr.HideUI(this);
    }

    private bool ShowSettled()
    {
        try
        {
            var animated = m_group != null ? m_group.GetComponent<AnimatedSetActive>() : null;
            return animated == null || animated.IsAnimationVisibleAndIdle();
        }
        catch { return true; }
    }

    private string LabelFor(Selectable selectable)
    {
        if (selectable == (Selectable?)m_back) return LocalizationUtils.GetGame("global_back", LocalizationKey.SOCIAL_BACK);

        if (selectable is Toggle tabToggle && m_tabs.TryGetValue(tabToggle, out var tab)) return TabLabel(tabToggle, tab);

        if (selectable == (Selectable?)m_migrate)
        {
            var text = m_migrate!.GetComponentInChildren<TMP_Text>(includeInactive: false);
            return text != null && !string.IsNullOrWhiteSpace(text.text) ? text.text : LocalizationUtils.GetGame("startup_tcgomigrate_title", LocalizationKey.SETTINGS_MIGRATE);
        }
        if (selectable == (Selectable?)m_music) return SocialUi.Join(LocalizationUtils.GetGame("settings_music", LocalizationKey.SETTINGS_MUSIC), Percent(m_music!));
        if (selectable == (Selectable?)m_sfx) return SocialUi.Join(LocalizationUtils.GetGame("settings_sfx", LocalizationKey.SETTINGS_SFX), Percent(m_sfx!));
        if (selectable == (Selectable?)m_res && m_res != null)
        {
            var value = m_res.options != null && m_res.value >= 0 && m_res.value < m_res.options.Count ? m_res.options[m_res.value].text : "";
            return SocialUi.Join(LocalizationUtils.GetGame("overflow_resolution", LocalizationKey.SETTINGS_RESOLUTION), value);
        }
        if (selectable == (Selectable?)m_windowed) return SocialUi.Join(LocalizationUtils.GetGame("overflow_windowed", LocalizationKey.SETTINGS_WINDOWED), State(m_windowed!.isOn));
        if (selectable == (Selectable?)m_hover) return SocialUi.Join(LocalizationUtils.GetGame("settings_hover_over", LocalizationKey.SETTINGS_CARD_HOVER), State(m_hover!.isOn));

        if (selectable == (Selectable?)m_foilFocus) return FoilAnimationsLabel();
        if (selectable == (Selectable?)m_frontEndVfx) return FrontEndVfxLabel();
        if (selectable == (Selectable?)m_battleVfx) return BattleVfxLabel();

        var language = selectable.GetComponent<LanguageSelectionButton>();
        if (language != null)
        {
            var name = LocalizationUtils.TryGetGame(language.localeLabel);
            if (string.IsNullOrWhiteSpace(name))
            {
                var text = selectable.GetComponentInChildren<TMP_Text>(includeInactive: false);
                name = text != null && !string.IsNullOrWhiteSpace(text.text) ? text.text : language.localeTag;
            }
            bool current = false;
            try { current = language.localeTag == LocalizationManager.Instance?.GetCurrentLanguage(); } catch { }
            return current ? SocialUi.Join(name, LocalizationUtils.Get(LocalizationKey.SETTINGS_CURRENT)) : name!;
        }

        return KeyboardNavigator.DefaultLabel(selectable);
    }

    // The accessibility controller exposes only each row's DESCRIPTION and its value label — the
    // row's NAME is a separate LocText it never references. Read it off the row itself: start at the
    // description's own container and take the first LocText there that isn't one of the LocTexts we
    // already know about, climbing at most two levels so we can never wander into a neighbouring
    // row. Every known description and value label is excluded, so a stray match can't be another
    // row's text. This is the game's own text; nothing is invented, and an empty result just means
    // the row reads as it did before.
    private string RowName(LocText? description)
    {
        if (description == null) return "";
        try
        {
            var known = new HashSet<LocText>(new[] { m_foilDescription, m_foilValueLabel, m_frontEndDescription, m_frontEndValueLabel, m_battleDescription, m_battleValueLabel }
                .Where(t => t != null).Select(t => t!));
            var scope = description.transform.parent;
            for (int level = 0; level < 2 && scope != null; level++, scope = scope.parent)
            {
                foreach (var candidate in scope.GetComponentsInChildren<LocText>(includeInactive: false))
                {
                    if (candidate == null || known.Contains(candidate)) continue;
                    var text = SocialUi.TextOf(candidate);
                    if (!string.IsNullOrWhiteSpace(text)) return text;
                }
            }
        }
        catch { }
        return "";
    }

    private string FrontEndVfxLabel()
    {
        try
        {
            var accessibility = ManagerSingleton<GameManager>.instance?.Accessibility;
            if (accessibility != null)
            {
                bool value = accessibility.UseFrontEndVFX.Value;
                return SocialUi.Join(RowName(m_frontEndDescription),
                    SocialUi.Join(State(value), LocalizationUtils.TryGetGame(UseFrontEndVFXBoolSetting.GetDescriptionForFrontEndVFX(value))));
            }
        }
        catch { }
        return SocialUi.Join(RowName(m_frontEndDescription), SocialUi.Join(SocialUi.TextOf(m_frontEndValueLabel), SocialUi.TextOf(m_frontEndDescription)));
    }

    private string BattleVfxLabel()
    {
        try
        {
            var accessibility = ManagerSingleton<GameManager>.instance?.Accessibility;
            if (accessibility != null)
            {
                bool value = accessibility.UseCardBattleVFX.Value;
                return SocialUi.Join(RowName(m_battleDescription),
                    SocialUi.Join(State(value), LocalizationUtils.TryGetGame(UseCardBattleVFXBoolSetting.GetDescriptionForCardBattleVFX(value))));
            }
        }
        catch { }
        return SocialUi.Join(RowName(m_battleDescription), SocialUi.Join(SocialUi.TextOf(m_battleValueLabel), SocialUi.TextOf(m_battleDescription)));
    }

    // Read-only. Selector.Next / Previous in this client only accept SelectorModel<GameBoardAnimOption>
    // while this row binds SelectorModel<CardFoilAnimOption>, so the arrows either side of it do
    // nothing for anyone — the setting is genuinely unchangeable in the shipped game. We report that
    // rather than repairing it: the mod's job is to give the same access a sighted player has, not
    // more. If a later client fixes the widget, delete this note and the trailing state below.
    private string FoilAnimationsLabel()
    {
        string label;
        try
        {
            var value = m_foilSelector != null
                ? m_foilSelector.GetValue<CardFoilAnimOption>()
                : ManagerSingleton<GameManager>.instance.Accessibility.CardFoilAnim.Value;
            label = SocialUi.Join(RowName(m_foilDescription),
                SocialUi.Join(LocalizationUtils.TryGetGame(value.ToString()) ?? value.ToString(), LocalizationUtils.TryGetGame(CardFoilAnimSetting.GetDescription(value))));
        }
        catch
        {
            label = SocialUi.Join(RowName(m_foilDescription), SocialUi.Join(SocialUi.TextOf(m_foilValueLabel), SocialUi.TextOf(m_foilDescription)));
        }
        return string.IsNullOrWhiteSpace(label) ? label : Announce.WithState(label, LocalizationKey.GLOBAL_DISABLED);
    }

    private static string TabName(PageDragTab tab)
    {
        var id = tab.GetLocTextID();
        var label = LocalizationUtils.TryGetGame(id);
        if (string.IsNullOrWhiteSpace(label)) label = SocialUi.TextOf(ReadField<LocText>(tab, "locTextObj"));
        return string.IsNullOrWhiteSpace(label) ? id : label!;
    }

    private static string TabLabel(Toggle toggle, PageDragTab tab)
    {
        var label = SocialUi.Join(TabName(tab), LocalizationUtils.Get(toggle.isOn ? LocalizationKey.GLOBAL_SELECTED : LocalizationKey.SETTINGS_NOT_SELECTED));
        return toggle.interactable || toggle.isOn ? label : Announce.WithState(label, LocalizationKey.GLOBAL_DISABLED);
    }

    private static string Percent(Slider slider) =>
        LocalizationUtils.Format(LocalizationKey.SOCIAL_PERCENT_FORMAT, Mathf.RoundToInt(Mathf.InverseLerp(slider.minValue, slider.maxValue, slider.value) * 100f));

    // The game's own words for a two-state setting (common_on / common_off), so these rows read the
    // same as the Accessibility ones and localize with the client. Deliberately not "checked" /
    // "unchecked": these are settings switches rather than form checkboxes, and the game already
    // names their states — no reason to speak our words over the client's own text.
    private static string State(bool on) =>
        LocalizationUtils.TryGetGame(BoolSettingBase.GetToggleLabel(on)) ?? LocalizationUtils.Get(on ? LocalizationKey.SETTINGS_ON : LocalizationKey.SETTINGS_OFF);

    private static bool Active(Selectable? selectable) => selectable != null && selectable.isActiveAndEnabled;

    private static OverlayScreenGroup? ResolveOpenGroup(OverlayManager? manager, OverlayScreenGroup? oldGroup, OverlayScreenGroup? newGroup)
    {
        try
        {
            if (manager != null && oldGroup != null && manager.IsOverlayOpen(oldGroup)) return oldGroup;
            if (manager != null && newGroup != null && manager.IsOverlayOpen(newGroup)) return newGroup;
        }
        catch { }
        if (oldGroup != null && oldGroup.gameObject.activeInHierarchy) return oldGroup;
        if (newGroup != null && newGroup.gameObject.activeInHierarchy) return newGroup;
        return newGroup ?? oldGroup;
    }

    [HarmonyPatch(typeof(HUBOverflowMenuController), "OnClickSettings")]
    private static class OnClickSettingsHook
    {
        private static void Postfix(HUBOverflowMenuController __instance, OverlayManager overlayManager)
        {
            var manager = overlayManager ?? ReadField<OverlayManager>(__instance, "overlayManager");
            var group = ResolveOpenGroup(manager, ReadField<OverlayScreenGroup>(__instance, "_oldSettingsController"), ReadField<OverlayScreenGroup>(__instance, "_newSettingsController"));
            if (group == null) { Log.Warn("Settings: no live settings overlay was found"); return; }
            s_instance.m_overlayManager = manager;
            s_instance.m_group = group;
            if (AccessibilityMgr.IsFocused(s_instance)) s_instance.Rebuild(announce: true);
            else AccessibilityMgr.ShowUI(s_instance);
        }
    }

    [HarmonyPatch(typeof(OverlayScreenGroup), "OnDeActivate")]
    private static class OnDeActivateHook
    {
        private static void Postfix(OverlayScreenGroup __instance)
        {
            if (s_instance.m_group == null || !ReferenceEquals(s_instance.m_group, __instance)) return;
            s_instance.RestoreResolutionPreview();
            s_instance.m_pendingContent = false;
            s_instance.m_group = null;
            AccessibilityMgr.HideUI(s_instance);
        }
    }
}
