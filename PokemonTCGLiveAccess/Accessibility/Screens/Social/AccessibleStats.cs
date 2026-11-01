using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using static Accessibility.GameReflect;

namespace Accessibility;

// Player stats, the second page of the Profile screen (HUBProfileScreenController.ShowStats / ShowCustomizations).
// PlayerStatsGameplayStatsView.statList only holds the values; their labels are separate texts in the prefab, so a
// line here is every visible text at the same height, read left to right. Up/Down read lines; Left/Right and Enter
// drive whatever controls the page has; Escape goes back through ShowCustomizations, the game's own method.
internal sealed class AccessibleStats : AccessibleUI
{
    private static readonly AccessibleStats s_instance = new();

    private readonly StatLines m_lines;
    private readonly KeyboardNavigator m_nav;
    private HUBProfileScreenController? m_controller;

    private AccessibleStats()
    {
        m_lines = new StatLines(this);
        m_nav = new KeyboardNavigator(this) { AllowTab = false };
    }

    private Component? Page => ReadField<Component>(m_controller, "_statScreenGroup");

    public void HandleInput()
    {
        if (m_controller == null || !m_controller.isActiveAndEnabled)
        {
            Close();
            return;
        }
        if (AccessibleKey.ESCAPE.IsPressed() || AccessibleKey.BACK.IsPressed()) m_controller.ShowCustomizations();
        else if (m_lines.HandleInput()) return;
        else if (AccessibleKey.READ_NEXT_ITEM.IsPressed()) m_nav.MoveNext();
        else if (AccessibleKey.READ_PREV_ITEM.IsPressed()) m_nav.MovePrev();
        else if (AccessibleKey.CONFIRM.IsPressed() || AccessibleKey.SPACE.IsPressed())
        {
            if (m_nav.Activate(AccessibleKey.SPACE.IsPressed())) m_lines.Reset();
        }
    }

    public string GetHelp() => LocalizationUtils.Get(LocalizationKey.SOCIAL_STATS_HELP);

    public void OnGainedFocus()
    {
        var page = Page;
        m_nav.SetItems(page != null
            ? page.GetComponentsInChildren<Selectable>(includeInactive: false).Where(s => s.isActiveAndEnabled && s is not Scrollbar).OrderBy(s => SocialUi.ReadingOrder(s.transform))
            : Enumerable.Empty<Selectable>());
        m_nav.ForceFlatHorizontal();
        m_lines.Reset();
        AccessibilityMgr.Output(this, LocalizationUtils.Get(LocalizationKey.SOCIAL_STATS), interrupt: true);
        AccessibilityMgr.Output(this, m_lines.GetLine(0), interrupt: false);
    }

    private void Close()
    {
        m_controller = null;
        AccessibilityMgr.HideUI(this);
    }

    private List<string> Lines()
    {
        var page = Page;
        var lines = new List<string>();
        if (page != null)
        {
            var rows = new List<(float y, float tolerance, List<TMP_Text> texts)>();
            foreach (var t in page.GetComponentsInChildren<TMP_Text>(includeInactive: false))
            {
                if (string.IsNullOrWhiteSpace(t.text) || t.GetComponentInParent<Selectable>() != null) continue;
                var y = t.transform.position.y;
                var tolerance = t.rectTransform.rect.height * t.transform.lossyScale.y * 0.5f;
                var row = rows.FirstOrDefault(r => Mathf.Abs(r.y - y) <= Mathf.Max(r.tolerance, tolerance));
                if (row.texts != null) row.texts.Add(t);
                else rows.Add((y, tolerance, new List<TMP_Text> { t }));
            }
            lines.AddRange(rows.OrderByDescending(r => r.y)
                .Select(r => string.Join(" ", r.texts.OrderBy(t => t.transform.position.x).Select(t => t.text.Trim()))));
        }
        if (lines.Count == 0) lines.Add(LocalizationUtils.Get(LocalizationKey.SOCIAL_STATS_EMPTY));
        return lines;
    }

    private sealed class StatLines : AccessibleItem
    {
        public StatLines(AccessibleStats parent) : base(parent) { }

        protected internal override List<string> GetLines() => s_instance.Lines();
    }

    // ShowStats does nothing when the page is already up; ShowUI ignores a repeat too.
    [HarmonyPatch(typeof(HUBProfileScreenController), "ShowStats")]
    private static class ShowHook
    {
        private static void Postfix(HUBProfileScreenController __instance)
        {
            s_instance.m_controller = __instance;
            AccessibilityMgr.ShowUI(s_instance);
        }
    }

    [HarmonyPatch(typeof(HUBProfileScreenController), "ShowCustomizations")]
    private static class HideHook
    {
        private static void Postfix(HUBProfileScreenController __instance)
        {
            if (s_instance.m_controller == __instance) s_instance.Close();
        }
    }

    // The screen resets to the avatar page when it next opens without going through ShowCustomizations.
    [HarmonyPatch(typeof(HUBProfileScreenController), "OnDeactivate", new[] { typeof(HUBGroupController) })]
    private static class ScreenHideHook
    {
        private static void Postfix(HUBProfileScreenController __instance)
        {
            if (s_instance.m_controller == __instance) s_instance.Close();
        }
    }
}
