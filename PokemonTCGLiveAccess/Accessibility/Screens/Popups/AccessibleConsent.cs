using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using static Accessibility.GameReflect;

namespace Accessibility;

// ConsentFlow drives multiple sub-screens (one per ConsentStep: Terms / Analytics / Migration /
// MigrationProcessing) — each is a MenuGroup under the same ConsentFlow GameObject. The game
// shows exactly one at a time by calling MenuGroup.Open() on the active step and Close() on
// the others.
//
// MenuGroup.Close() does NOT GameObject.SetActive(false) — it just sets isOpen=false and
// disables blocksRaycasts on the CanvasGroup. So `GetComponentsInChildren(includeInactive:false)`
// from ConsentFlow.gameObject keeps returning Selectables from *every* step (migrate / agree /
// etc.) even when only one step is visible. We take the Selectables of the open MenuGroup only.
//
// Activation: MenuGroup.Open() of a step under a ConsentFlow (the previous mod polled for the flow's
// canvasGroup instead). Each step change opens the next MenuGroup, so the same hook rebuilds the groups
// and announces the new step. When consent is fully done, ConsentFlow.Close() runs and the root
// canvasGroup.blocksRaycasts flips to false (checked as a backstop too).
internal sealed class AccessibleConsent : AccessibleUI, ITitledComponent
{
    private static readonly AccessibleConsent s_instance = new();

    private readonly List<KeyboardNavigator> m_groups = new();
    private int m_activeGroup;
    private ConsentFlow? m_consent;
    private MenuGroup? m_step;

    public string? Title { get; private set; }

    public void HandleInput()
    {
        var canvasGroup = ReadField<CanvasGroup>(m_consent, "canvasGroup");
        if (m_consent == null || canvasGroup == null || !canvasGroup.blocksRaycasts)
        {
            Close();
            return;
        }

        // Step transition detection backstop — Terms → Analytics → Migration etc.
        var open = FindOpenMenuGroup();
        if (open != null && open != m_step)
        {
            OnGainedFocus();
            return;
        }

        if (m_groups.Count == 0) return;
        if (AccessibleKey.NEXT_SECTION.IsPressed() || AccessibleKey.PREV_SECTION.IsPressed())
        {
            // Tab is a no-op when there's only one group — nothing to cycle to.
            if (m_groups.Count > 1)
            {
                var delta = AccessibleKey.PREV_SECTION.IsPressed() ? -1 : 1;
                m_activeGroup = (m_activeGroup + delta + m_groups.Count) % m_groups.Count;
                m_groups[m_activeGroup].FocusFirst(announce: true, interruptPrior: true);
            }
            return;
        }
        m_groups[m_activeGroup].HandleInput();
    }

    public string GetHelp() => LocalizationUtils.Get(m_groups.Count > 1 ? LocalizationKey.POPUP_GROUPS_HELP : LocalizationKey.POPUP_BUTTONS_HELP);

    public void OnGainedFocus()
    {
        var open = FindOpenMenuGroup();
        if (open == null) return;
        if (open != m_step) Build(open);
        if (!string.IsNullOrWhiteSpace(Title)) AccessibilityMgr.Output(this, Title!, interrupt: true);
        if (m_groups.Count > 0) PopupUtils.FocusQueued(m_groups[m_activeGroup]);
    }

    private void Close()
    {
        foreach (var g in m_groups) g.Detach();
        m_groups.Clear();
        m_step = null;
        AccessibilityMgr.HideUI(this);
    }

    // The MenuGroup that's currently displayed. ConsentFlow opens exactly one at a time.
    private MenuGroup? FindOpenMenuGroup()
    {
        if (m_consent == null) return null;
        foreach (var mg in m_consent.GetComponentsInChildren<MenuGroup>(includeInactive: false))
            if (mg.IsOpen) return mg;
        return null;
    }

    // Build the focus groups for the ConsentStep now visible, and its description (header + body).
    private void Build(MenuGroup step)
    {
        m_step = step;
        foreach (var g in m_groups) g.Detach();
        m_groups.Clear();
        m_activeGroup = 0;

        var all = step.GetComponentsInChildren<Selectable>(includeInactive: false)
            .Where(s => s.gameObject.activeInHierarchy)
            .OrderBy(s => PopupUtils.ReadingOrder(s.transform))
            .ToList();

        // Detect "is this the Terms step?" by checking whether ConsentFlow's known fields
        // (privacy / tos / tosConfirm) live inside this MenuGroup. If yes → 3 sub-groups.
        var privacy = ReadField<Toggle>(m_consent, "privacy");
        var tos = ReadField<Toggle>(m_consent, "tos");
        var tosConfirm = ReadField<Button>(m_consent, "tosConfirm");
        if (privacy != null && tos != null && tosConfirm != null && privacy.GetComponentInParent<MenuGroup>() == step)
        {
            // The review buttons sit side-by-side → horizontal (axes auto-detected from positions).
            AddGroup(all.Where(s => s is Button && s != tosConfirm));
            AddGroup(new Selectable[] { tos, privacy });   // stacked → vertical
            AddGroup(new Selectable[] { tosConfirm });
        }
        else
        {
            AddGroup(all);
        }

        Title = BuildDescription(step.gameObject);
    }

    private void AddGroup(IEnumerable<Selectable> items)
    {
        var list = items.ToList();
        if (list.Count == 0) return;
        var g = new KeyboardNavigator(this) { AllowTab = false };
        g.SetItems(list);
        m_groups.Add(g);
    }

    // The header cluster: every plain (non-control) text at least 40% the size of the biggest one.
    private static string BuildDescription(GameObject scope)
    {
        var texts = scope.GetComponentsInChildren<TMP_Text>(includeInactive: false)
            .Where(t => !string.IsNullOrWhiteSpace(t.text))
            .Where(t => t.GetComponentInParent<Selectable>() == null)
            .ToList();
        if (texts.Count == 0) return string.Empty;
        var maxFont = texts.Max(t => t.fontSize);
        return string.Join(". ", texts
            .Where(t => t.fontSize >= maxFont * 0.4f)
            .OrderBy(t => PopupUtils.ReadingOrder(t.transform))
            .Select(t => t.text.Trim()));
    }

    [HarmonyPatch(typeof(MenuGroup), "Open")]
    private static class StepOpenHook
    {
        private static void Postfix(MenuGroup __instance)
        {
            var flow = __instance.GetComponentInParent<ConsentFlow>();
            if (flow == null) return;
            s_instance.m_consent = flow;
            s_instance.m_step = null;   // force a rebuild for the step just opened
            PopupUtils.ShowOrRefresh(s_instance);
        }
    }

    [HarmonyPatch(typeof(ConsentFlow), "Close")]
    private static class CloseHook
    {
        private static void Postfix(ConsentFlow __instance)
        {
            if (s_instance.m_consent == __instance) s_instance.Close();
        }
    }
}
