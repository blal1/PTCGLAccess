using System.Linq;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using static Accessibility.GameReflect;

namespace Accessibility;

// The lobby's gear (BuildAndBattleSettingsPopup): the audio and display settings of an embedded
// HUBOverflowSettingsController, plus FORFEIT, the only way to abandon a run. Same keys as the HUB settings: Left/Right
// change a slider, a resolution or an on/off setting, Enter flips on/off or commits the previewed resolution.
internal sealed class AccessibleBnbSettings : AccessibleUI
{
    private static readonly AccessibleBnbSettings s_instance = new();

    private readonly KeyboardNavigator m_nav;
    private BuildAndBattleSettingsPopup? m_popup;
    private HUBOverflowSettingsController? m_settings;
    private Button? m_forfeit;
    private int m_resPreview = -1;

    private AccessibleBnbSettings()
    {
        m_nav = new KeyboardNavigator(this) { OnBack = CloseByPlayer };
    }

    [HarmonyPatch(typeof(OverlayScreenGroup), "Activate")]
    private static class ActivateHook
    {
        private static void Postfix(OverlayScreenGroup __instance)
        {
            if (__instance is not BuildAndBattleSettingsPopup popup) return;
            s_instance.m_popup = popup;
            AccessibilityMgr.ShowUI(s_instance);
        }
    }

    [HarmonyPatch(typeof(OverlayScreenGroup), "OnDeActivate")]
    private static class DeactivateHook
    {
        private static void Postfix(OverlayScreenGroup __instance)
        {
            if (s_instance.m_popup != __instance) return;
            s_instance.Close();
        }
    }

    public string GetHelp() => LocalizationUtils.Get(LocalizationKey.HUB_BNB_SETTINGS_HELP);

    public void OnGainedFocus()
    {
        if (m_popup == null) return;
        m_settings = ReadField<HUBOverflowSettingsController>(m_popup, "settingsController");
        m_forfeit = ReadField<Button>(m_popup, "forfeitButton");
        var res = Dropdown();
        m_resPreview = res != null ? res.value : -1;
        m_nav.SetItems(m_popup.GetComponentsInChildren<Selectable>(includeInactive: false)
            .Where(s => s.isActiveAndEnabled && s is Button or Toggle or Slider or TMP_Dropdown)
            .OrderBy(s => HubUi.ReadingOrder(s.transform)), Label);
        m_nav.ForceFlatVertical();
        AccessibilityMgr.Output(this, LocalizationUtils.GetGame("overflow_settings", LocalizationKey.SETTINGS_TITLE), interrupt: true);
        m_nav.FocusFirst(announce: true, interruptPrior: false);
    }

    public void HandleInput()
    {
        if (m_popup == null || !m_popup.gameObject.activeInHierarchy)
        {
            Close();
            return;
        }
        var cur = m_nav.Current;
        var left = AccessibleKey.READ_PREV_ITEM.IsPressed();
        var right = AccessibleKey.READ_NEXT_ITEM.IsPressed();
        if (left || right)
        {
            if (cur is Slider slider && slider.interactable)
            {
                var step = (slider.maxValue - slider.minValue) * 0.1f;
                slider.value = Mathf.Clamp(slider.value + (right ? step : -step), slider.minValue, slider.maxValue);
                m_nav.RefocusCurrent(announce: true);
            }
            else if (cur is TMP_Dropdown dd && dd.options.Count > 0)
            {
                m_resPreview = (m_resPreview + (right ? 1 : -1) + dd.options.Count) % dd.options.Count;
                AccessibilityMgr.Output(this, Label(dd), interrupt: true);
            }
            else if (cur is Toggle side && side.isOn != right)
            {
                side.isOn = right;
                m_nav.RefocusCurrent(announce: true);
            }
            return;
        }
        if (AccessibleKey.CONFIRM.IsPressed() || AccessibleKey.SPACE.IsPressed())
        {
            if (cur is TMP_Dropdown dd)
            {
                if (m_resPreview >= 0 && m_resPreview != dd.value) dd.value = m_resPreview;   // fires the game's confirmation
                return;
            }
            if (cur is Toggle toggle)
            {
                toggle.isOn = !toggle.isOn;
                m_nav.RefocusCurrent(announce: true);
                return;
            }
            if (cur != null && cur == m_forfeit)
            {
                if (!m_forfeit!.IsInteractable()) return;
                // Closes this popup and opens the confirmation (BuildAndBattleGenericOverlay → AccessibleBnbPopup).
                m_popup.ShowForfeitConfirmPopup();
                Close();
                return;
            }
        }
        m_nav.HandleInput();
    }

    // The same two calls ShowForfeitConfirmPopup makes; the popup is gone at the end of its hide animation.
    private void CloseByPlayer()
    {
        m_popup?.OnLandscapeCloseButton();
        m_popup?.AnimateHide();
        Close();
    }

    private void Close()
    {
        m_popup = null;
        AccessibilityMgr.HideUI(this);
    }

    private TMP_Dropdown? Dropdown() => ReadField<TMP_Dropdown>(m_settings, "_resolutionsSelector");

    private string Label(Selectable s)
    {
        if (s == ReadField<Slider>(m_settings, "_musicSlider")) return Join("settings_music", LocalizationKey.SETTINGS_MUSIC, Percent((Slider)s));
        if (s == ReadField<Slider>(m_settings, "_sfxSlider")) return Join("settings_sfx", LocalizationKey.SETTINGS_SFX, Percent((Slider)s));
        if (s == Dropdown())
        {
            var dd = (TMP_Dropdown)s;
            var i = m_resPreview >= 0 ? m_resPreview : dd.value;
            return Join("overflow_resolution", LocalizationKey.SETTINGS_RESOLUTION, i < dd.options.Count ? dd.options[i].text : "");
        }
        if (s == ReadField<Toggle>(m_settings, "_fullScreenToggle")) return Join("overflow_windowed", LocalizationKey.SETTINGS_WINDOWED, State(((Toggle)s).isOn));
        if (s == ReadField<Toggle>(m_settings, "_cardHoverEnabledToggle")) return Join("settings_hover_over", LocalizationKey.SETTINGS_CARD_HOVER, State(((Toggle)s).isOn));
        return HubUi.IconButtonLabel(s, LocalizationKey.HUB_INFORMATION);
    }

    private static string Join(string gameId, LocalizationKey fallback, string value) =>
        LocalizationUtils.GetGame(gameId, fallback) + ": " + value;

    private static string Percent(Slider s) =>
        LocalizationUtils.Format(LocalizationKey.SOCIAL_PERCENT_FORMAT, Mathf.RoundToInt(Mathf.InverseLerp(s.minValue, s.maxValue, s.value) * 100f));

    private static string State(bool on) =>
        LocalizationUtils.TryGetGame(BoolSettingBase.GetToggleLabel(on)) ?? LocalizationUtils.Get(on ? LocalizationKey.SETTINGS_ON : LocalizationKey.SETTINGS_OFF);
}
