using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using TMPro;
using TPCI.Localization;
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
    private AccessibilitySelect? m_accessibility;

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
        // The game rewrites a toggle's description when it changes; the navigator only says checked or unchecked.
        var nav = m_groups[m_activeGroup];
        var toggle = m_accessibility != null ? nav.Current as Toggle : null;
        var wasOn = toggle != null && toggle.isOn;
        nav.HandleInput();
        if (toggle != null && toggle == nav.Current && toggle.isOn != wasOn) AccessibilityMgr.Output(this, ToggleDescription(toggle), interrupt: false);
    }

    public string GetHelp() => LocalizationUtils.Get(m_accessibility != null ? LocalizationKey.POPUP_CONSENT_ACCESSIBILITY_HELP
        : m_groups.Count > 1 ? LocalizationKey.POPUP_GROUPS_HELP : LocalizationKey.POPUP_BUTTONS_HELP);

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

        m_accessibility = step.GetComponentInChildren<AccessibilitySelect>(includeInactive: false);
        if (m_accessibility != null)
        {
            BuildAccessibilityStep(step, all);
            return;
        }

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

    // Selector is not a Selectable, so its row is focused through one of its arrows. Those are the only buttons of
    // the selector and they say nothing: the value, its description and the row's name are read instead.
    private void BuildAccessibilityStep(MenuGroup step, List<Selectable> all)
    {
        var selector = ReadField<Selector>(m_accessibility, "_cardFoilAnimsSelector");
        var left = ReadField<Button>(selector, "leftButton");
        var right = ReadField<Button>(selector, "rightButton");
        var foil = right != null && right.isActiveAndEnabled ? right : left != null && left.isActiveAndEnabled ? left : null;
        var ambient = ReadField<Toggle>(m_accessibility, "_useAmbientFrontEndVFXToggle");
        var battle = ReadField<Toggle>(m_accessibility, "_useCardBattleVFXToggle");

        var items = all.Where(s => s != left && s != right).ToList();
        if (foil != null) items.Insert(0, foil);
        AddGroup(items, s => s == foil ? FoilLabel(selector) : s == ambient || s == battle ? ToggleLabel((Toggle)s) : KeyboardNavigator.DefaultLabel(s));

        var own = new HashSet<LocText?>(AccessibilityTexts());
        Title = BuildDescription(step.gameObject, t => !own.Contains(t.GetComponent<LocText>()));
    }

    private LocText? AccessibilityText(string field) => ReadField<LocText>(m_accessibility, field);

    private LocText?[] AccessibilityTexts() => new[]
    {
        AccessibilityText("_cardFoilAnimsDescription"), ReadField<LocText>(ReadField<Selector>(m_accessibility, "_cardFoilAnimsSelector"), "locText"),
        AccessibilityText("_useAmbientFrontEndVFXDescription"), AccessibilityText("_useAmbientFrontEndVFXLabel"),
        AccessibilityText("_useCardBattleVFXDescription"), AccessibilityText("_useCardBattleVFXLabel"),
    };

    // Read-only for everyone: see AccessibleSettings.FoilAnimationsLabel, Selector.Next ignores this model type.
    private string FoilLabel(Selector? selector)
    {
        var description = AccessibilityText("_cardFoilAnimsDescription");
        var name = SocialUi.RowName(description, AccessibilityTexts());
        string state;
        try
        {
            var value = selector!.GetValue<CardFoilAnimOption>();
            state = SocialUi.Join(LocalizationUtils.TryGetGame(value.ToString()) ?? value.ToString(), LocalizationUtils.TryGetGame(CardFoilAnimSetting.GetDescription(value)));
        }
        catch
        {
            state = SocialUi.Join(SocialUi.TextOf(ReadField<LocText>(selector, "locText")), SocialUi.TextOf(description));
        }
        return Announce.WithState(SocialUi.Join(name, state), LocalizationKey.GLOBAL_DISABLED);
    }

    private LocText? DescriptionOf(Toggle toggle) =>
        AccessibilityText(toggle == ReadField<Toggle>(m_accessibility, "_useAmbientFrontEndVFXToggle") ? "_useAmbientFrontEndVFXDescription" : "_useCardBattleVFXDescription");

    private string ToggleDescription(Toggle toggle) => SocialUi.TextOf(DescriptionOf(toggle));

    private string ToggleLabel(Toggle toggle)
    {
        var name = SocialUi.RowName(DescriptionOf(toggle), AccessibilityTexts());
        var state = LocalizationUtils.Get(toggle.isOn ? LocalizationKey.GLOBAL_CHECKED : LocalizationKey.GLOBAL_UNCHECKED);
        return SocialUi.Join(SocialUi.Join(name, state), ToggleDescription(toggle));
    }

    private void AddGroup(IEnumerable<Selectable> items, KeyboardNavigator.LabelResolver? resolver = null)
    {
        var list = items.ToList();
        if (list.Count == 0) return;
        var g = new KeyboardNavigator(this) { AllowTab = false };
        g.SetItems(list, resolver);
        if (resolver != null) g.ForceFlatVertical();
        m_groups.Add(g);
    }

    // The header cluster: every plain (non-control) text at least 40% the size of the biggest one.
    private static string BuildDescription(GameObject scope, System.Func<TMP_Text, bool>? include = null)
    {
        var texts = scope.GetComponentsInChildren<TMP_Text>(includeInactive: false)
            .Where(t => !string.IsNullOrWhiteSpace(t.text))
            .Where(t => t.GetComponentInParent<Selectable>() == null)
            .Where(t => include == null || include(t))
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
