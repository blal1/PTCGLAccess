using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using TMPro;
using UnityEngine.UI;
using static Accessibility.GameReflect;

namespace Accessibility;

// First-launch Select Language screen. Driver: LanguageSelect (TPCI.RainierClient).
//
// LoginManager.InitializeLanguageSelect() instantiates a prefab, calls LanguageSelect.Initialize(),
// which only opens the screen when PlayerPrefs key "LoginManager.hasPlayerSelectedLanguage" is 0.
// The 7 language buttons are children of that prefab — plain uGUI Buttons whose onClick
// UnityEvents are wired in the prefab to call LanguageSelect.SelectLanguage("en"|"es"|...).
// We discover them, focus them via Selectable.Select(), and activate via onClick.Invoke().
//
// Note: the title text comes from a separate "_startupLoc" table that LocalizationManager.Instance
// doesn't see — asking the main manager for "startup_select_language" yields "(Missing)<…>".
// So we read selectLanguageTitle.text directly (set by Initialize via _startupLoc).
//
// A UI, not a screen: it is a prefab shown over whatever startup screen is current (loading), and the
// confirmation SimpleMessage stacks over it.
internal sealed class AccessibleLanguageSelect : AccessibleUI, ITitledComponent
{
    private static readonly AccessibleLanguageSelect s_instance = new();

    private readonly KeyboardNavigator m_nav;
    private LanguageSelect? m_ls;

    private AccessibleLanguageSelect()
    {
        m_nav = new KeyboardNavigator(this);
    }

    public string? Title => (m_ls != null ? ReadField<TextMeshProUGUI>(m_ls, "selectLanguageTitle")?.text : null)
        ?? LocalizationUtils.Get(LocalizationKey.POPUP_SELECT_LANGUAGE);

    public void HandleInput()
    {
        // Real exit conditions only: the prefab was destroyed, or the player confirmed a language (the PlayerPrefs
        // flag flips inside ConfirmLanguageSelection). Do NOT check languageAnimation.isOpen — it's false during
        // the opening animation, which would close us on the very next frame.
        if (IsGone())
        {
            AccessibilityMgr.HideUI(this);
            return;
        }
        m_nav.HandleInput();
    }

    public string GetHelp() => LocalizationUtils.Get(LocalizationKey.POPUP_LANGUAGE_HELP);

    public void OnGainedFocus()
    {
        if (IsGone())
        {
            AccessibilityMgr.HideUI(this);
            return;
        }
        // First time: discover the buttons. Back from the confirmation (cancelled): keep the list and the focus.
        if (m_nav.Count == 0) Build();
        AccessibilityMgr.Output(this, Title!, interrupt: true);
        PopupUtils.FocusQueued(m_nav);
    }

    private bool IsGone() => m_ls == null || m_ls.hasPlayerSelectedLanguage;

    private void Build()
    {
        // skipDebugButton and disablePTCSButton are serialized fields on LanguageSelect — debug-only
        // entries we don't want in the language list.
        var debug = new HashSet<Button?> { ReadField<Button>(m_ls, "skipDebugButton"), ReadField<Button>(m_ls, "disablePTCSButton") };
        var buttons = m_ls!.GetComponentsInChildren<Button>(includeInactive: false)
            .Where(b => !debug.Contains(b))
            .Cast<Selectable>()
            .OrderBy(s => PopupUtils.ReadingOrder(s.transform))   // row-major: grid Up/Down/Left/Right must line up with the visual
            .ToList();
        if (buttons.Count == 0) Log.Warn("AccessibleLanguageSelect: no language buttons under LanguageSelect");

        m_nav.SetItems(buttons);
        // Navigate as the true 2-column grid it visually is: Up/Down between rows, Left/Right
        // between columns (SetItems auto-detected Columns = 2 from the layout). Tab off — single group.
        // Earlier this was forced to a flat vertical list, but with Columns = 2 the column-preserving
        // vertical move only ever walked the left column, leaving the right column (Español, Français,
        // Português) completely unreachable. A grid matches what a sighted player sees.
        m_nav.AllowVertical = true;
        m_nav.AllowHorizontal = true;
        m_nav.AllowTab = false;
    }

    // Initialize runs every launch; the screen is only actually shown on first launch.
    [HarmonyPatch(typeof(LanguageSelect), "Initialize")]
    private static class InitializeHook
    {
        private static void Postfix(LanguageSelect __instance)
        {
            if (__instance.hasPlayerSelectedLanguage) return;
            s_instance.m_ls = __instance;
            s_instance.m_nav.SetItems(new List<Selectable>());
            AccessibilityMgr.ShowUI(s_instance);
        }
    }

    [HarmonyPatch(typeof(LanguageSelect), "CloseLanguageSelect")]
    private static class CloseHook
    {
        private static void Postfix(LanguageSelect __instance)
        {
            if (s_instance.m_ls == __instance) AccessibilityMgr.HideUI(s_instance);
        }
    }
}
