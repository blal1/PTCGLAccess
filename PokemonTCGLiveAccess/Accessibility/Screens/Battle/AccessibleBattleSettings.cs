using System;
using System.Linq;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Accessibility;

// The in-battle settings / "pause" menu — the panel the gear icon (top-right of the board) opens:
// music & SFX volume sliders, resolution dropdown, fullscreen + card-hover toggles, and the
// CONCEDE button + back. Driver: the game's own `BattleSettings` MonoBehaviour (Show/Hide). The gear
// click is wired entirely in the prefab (no code path), so we find the object and call Show() ourselves.
//
// Opened from AccessibleBattleScreen on Escape while in the field with no selection overlay up (those
// overlays put their own UI over the battle screen, so Escape only reaches here when the board is clear).
// We present its real Selectables for keyboard nav; Enter activates buttons/toggles, Left/Right adjust
// the focused slider or dropdown, Escape/back closes (BattleSettings.Hide).
//
// Concede routes through the game's own confirmation (MatchManager.Confirm.ConfirmMatchAction → a
// ModalElementContainer with "I give up" / "never mind"), which the mod's modal UI already speaks and
// navigates — so we don't special-case it. During the FTUE tutorial the game hides the concede button
// (DefaultBattleSettingsController), so it simply won't appear in the list there.
internal sealed class AccessibleBattleSettings : AccessibleUI
{
    private static readonly AccessibleBattleSettings s_instance = new();

    private readonly KeyboardNavigator m_nav;
    private BattleSettings? m_settings;

    private AccessibleBattleSettings()
    {
        m_nav = new KeyboardNavigator(this) { AllowTab = false };
    }

    // The game hid the panel itself (concede confirmed, or any other path): drop the UI wherever it is.
    [HarmonyPatch(typeof(BattleSettings), "Hide")]
    private static class HideHook
    {
        private static void Postfix(BattleSettings __instance)
        {
            if (ReferenceEquals(__instance, s_instance.m_settings)) AccessibilityMgr.HideUI(s_instance);
        }
    }

    // Called by AccessibleBattleScreen on Escape. Returns false (and says so) if the menu can't be found.
    internal static bool TryOpen() => s_instance.Open();

    private bool Open()
    {
        // Include inactive: the panel is disabled while closed.
        var all = UnityEngine.Object.FindObjectsByType<BattleSettings>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        var settings = all.Length > 0 ? all[0] : null;
        if (settings == null)
        {
            AccessibilityMgr.OutputNotification(LocalizationUtils.Get(LocalizationKey.BATTLE_MENU_UNAVAILABLE), interrupt: true);
            return false;
        }
        m_settings = settings;
        try { settings.Show(); } catch (Exception ex) { Log.Error($"BattleSettings.Show threw: {ex.Message}"); }
        if (AccessibilityMgr.IsFocused(this)) Rebuild();
        else AccessibilityMgr.ShowUI(this);
        return true;
    }

    public string GetHelp() => LocalizationUtils.Get(LocalizationKey.BATTLE_MENU_HELP);

    // Shown, or a child (e.g. the concede confirmation modal) closed. If the panel is still open,
    // re-present it; if it was closed (concede confirmed/cancelled both hide it), hide.
    public void OnGainedFocus()
    {
        if (!IsOpen()) { AccessibilityMgr.HideUI(this); return; }
        Rebuild();
    }

    public void HandleInput()
    {
        if (!IsOpen()) { AccessibilityMgr.HideUI(this); return; }

        // Left/Right adjust the focused slider or dropdown in place (nav itself is vertical-only).
        bool left = AccessibleKey.READ_PREV_ITEM.IsPressed();
        bool right = AccessibleKey.READ_NEXT_ITEM.IsPressed();
        if (left || right)
        {
            var cur = m_nav.Current;
            if (cur is Slider sl && sl.interactable)
            {
                float step = (sl.maxValue - sl.minValue) * 0.1f;
                if (step <= 0f) step = 0.1f;
                sl.value = Mathf.Clamp(sl.value + (right ? step : -step), sl.minValue, sl.maxValue);
                m_nav.RefocusCurrent(announce: true);
                return;
            }
            if (cur is TMP_Dropdown dd && dd.interactable && dd.options != null && dd.options.Count > 0)
            {
                dd.value = (dd.value + (right ? 1 : -1) + dd.options.Count) % dd.options.Count;
                m_nav.RefocusCurrent(announce: true);
                return;
            }
        }

        m_nav.HandleInput();
    }

    private bool IsOpen() => m_settings != null && m_settings.gameObject.activeInHierarchy;

    private void Rebuild()
    {
        if (m_settings == null) return;

        var items = m_settings.GetComponentsInChildren<Selectable>(includeInactive: false)
            .Where(s => s != null && s.gameObject.activeInHierarchy && s is not Scrollbar)
            .OrderBy(s => ReadingOrder(s.transform))
            .ToList();

        m_nav.SetItems(items, LabelFor);
        m_nav.AllowVertical = true;            // a settings panel is a vertical list…
        m_nav.AllowHorizontal = false;         // …and we own Left/Right for slider / dropdown values
        m_nav.OnBack = Close;                  // Escape / Backspace closes the menu

        AccessibilityMgr.Output(this, LocalizationUtils.Get(LocalizationKey.BATTLE_MENU), interrupt: true);
        if (items.Count > 0) m_nav.FocusFirst(announce: true, interruptPrior: false);
        else AccessibilityMgr.Output(this, LocalizationUtils.Get(LocalizationKey.BATTLE_MENU_NO_OPTIONS), interrupt: false);
        Log.Info($"BattleSettings: {items.Count} items — {string.Join(", ", items.Take(12).Select(s => s.gameObject.name))}");
    }

    private void Close()
    {
        var settings = m_settings;
        m_settings = null;
        try { settings?.Hide(); } catch (Exception ex) { Log.Error($"BattleSettings.Hide threw: {ex.Message}"); }
        AccessibilityMgr.HideUI(this);
    }

    // Slider / dropdown read their live value; everything else uses the shared default label
    // (which already appends "checked/unchecked" for toggles and "unavailable" when disabled).
    private static string LabelFor(Selectable s)
    {
        if (s is Slider sl)
        {
            int pct = Mathf.RoundToInt(Mathf.InverseLerp(sl.minValue, sl.maxValue, sl.value) * 100f);
            return LocalizationUtils.Format(LocalizationKey.BATTLE_MENU_PERCENT, KeyboardNavigator.DefaultLabel(s), pct);
        }
        if (s is TMP_Dropdown dd)
        {
            string v = dd.options != null && dd.value >= 0 && dd.value < dd.options.Count ? dd.options[dd.value].text : "";
            return string.IsNullOrEmpty(v) ? KeyboardNavigator.DefaultLabel(s) : LocalizationUtils.Format(LocalizationKey.COMMON_STATE_FORMAT, KeyboardNavigator.DefaultLabel(s), v);
        }
        return KeyboardNavigator.DefaultLabel(s);
    }

    private static float ReadingOrder(Transform t) { var p = t.position; return -p.y * 10000 + p.x; }
}
