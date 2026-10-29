using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using TMPro;
using TPCI.Localization;
using UnityEngine.UI;
using static Accessibility.GameReflect;

namespace Accessibility;

// Generic modal popup — ModalDrawer / ModalPopup, both subclasses of abstract ModalElementContainer.
// Built via RainierManager.genericModalDrawer / genericModalPopup with SetupModalDrawerBlur(...).
// Examples: "SKIP MIGRATION?" with CANCEL / DON'T MIGRATE + "DON'T SHOW THIS AGAIN" toggle;
// in-settings language-change confirm; misc action drawers; match-action confirmations.
//
// We split the modal's Selectables into two Tab groups:
//   1. Content area — toggles, custom Selectables in the body (e.g. "DON'T SHOW THIS AGAIN").
//   2. Bottom action buttons — from the `bottomButtons` serialized list (CANCEL / CONFIRM / etc.).
// Tab cycles between them so a toggle's state is clearly separate from a button press.
//
// Excluded from nav:
//   - backgroundBlockerButton (tap-outside affordance, not a real action — handled via OnBack)
//   - Scrollbar instances (scroll handles aren't menu items; they'd otherwise show up because
//     ModalElementContainer has a ScrollRect for content overflow)
//
// Open runs inside the game's own call stack (a match coroutine opens these): an exception thrown from here
// once escaped through ModalElementContainer.Open and killed InputRequestOperation.RunOperation(), hard-freezing
// an online match. AccessibilityMgr guards every component call for that reason.
internal sealed class AccessibleModalContainer : AccessibleUI, ITitledComponent
{
    private static readonly AccessibleModalContainer s_instance = new();
    private static readonly FieldInfo? s_blockerStateField = AccessTools.Field(typeof(ModalElementContainer), "backgroundBlockerState");

    private readonly List<KeyboardNavigator> m_groups = new();
    private int m_activeGroup;
    private ModalElementContainer? m_modal;
    private AccessibleComponent? m_parent;

    public string? Title => m_modal != null ? PopupUtils.Text(ReadField<LocText>(m_modal, "titleText")) : null;

    public void HandleInput()
    {
        if (m_modal == null)
        {
            AccessibilityMgr.HideUI(this);
            return;
        }
        if (m_groups.Count == 0) return;
        if (AccessibleKey.NEXT_SECTION.IsPressed() || AccessibleKey.PREV_SECTION.IsPressed())
        {
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

    // Speak the body (barging in), then QUEUE the focused button after it as its own announce — two separate
    // utterances, so the button reads as a distinct, independently-navigable item.
    public void OnGainedFocus()
    {
        if (m_modal == null) return;
        var titleText = ReadField<LocText>(m_modal, "titleText");
        var preamble = PopupUtils.Preamble(PopupUtils.Text(titleText), BuildBodyText(m_modal, titleText), m_parent);
        if (!string.IsNullOrWhiteSpace(preamble)) AccessibilityMgr.Output(this, preamble!, interrupt: true);
        if (m_groups.Count > 0) PopupUtils.FocusQueued(m_groups[m_activeGroup]);
    }

    private void Open(ModalElementContainer modal)
    {
        m_modal = modal;
        if (AccessibilityMgr.Focused != this) m_parent = AccessibilityMgr.Focused;

        var blocker = ReadField<Button>(modal, "backgroundBlockerButton");
        var bottom = modal.BottomButtons ?? new List<Button>();
        var bottomSet = new HashSet<Selectable>(bottom.Cast<Selectable>());

        var content = modal.GetComponentsInChildren<Selectable>(includeInactive: false)
            .Where(s => s != blocker && s is not Scrollbar && s.gameObject.activeInHierarchy && !bottomSet.Contains(s))
            // Skip card-graphic selectables: the Mulligans reveal (and any modal embedding card images)
            // puts clickable CardUI cards in the body. They're click-to-zoom graphics, NOT menu controls
            // — navigating them read "Card UI" junk and buried the real DONE button (the player couldn't
            // tell there was a Done or how to proceed). The mulligan reveal announces the cards itself,
            // so drop them here; the bottom DONE/action buttons remain.
            .Where(s => s.GetComponentInParent<CardUI>() == null)
            .OrderBy(s => PopupUtils.ReadingOrder(s.transform));
        var buttons = bottom
            .Where(b => b != null && b.gameObject.activeInHierarchy)
            .Cast<Selectable>()
            .OrderBy(s => PopupUtils.ReadingOrder(s.transform));

        foreach (var g in m_groups) g.Detach();
        m_groups.Clear();
        m_activeGroup = 0;
        // Esc / Backspace = dismiss only when the game's design intent allows it (tap outside closes).
        var canClose = s_blockerStateField?.GetValue(modal) is ModalElementContainer.BlockerState state && state == ModalElementContainer.BlockerState.CLOSE;
        // Reading order: content first (typically toggles/text near the top), buttons at the bottom.
        foreach (var items in new[] { content.ToList(), buttons.ToList() })
        {
            if (items.Count == 0) continue;
            var g = new KeyboardNavigator(this) { AllowTab = false, OnBack = canClose ? modal.Close : null };
            g.SetItems(items);   // axes auto-detected from positions
            m_groups.Add(g);
        }

        PopupUtils.ShowOrRefresh(this);
    }

    // The modal's message text, gathered from two sources and deduped by GameObject:
    //  1. Plain TMP children of the container — excludes button labels via the Selectable-ancestor filter.
    //  2. The modal's CurrentOptions — the spawned message options (SimpleMessageModalOption, etc.). The
    //     match-action confirmations ("you still have selections", concede warning) and the
    //     language/resolution confirms put their body in such an option, and the container scan misses it
    //     because the whole modal sits under the blocker Button. For PASSIVE options (no Selectable on the
    //     option root = not a button/toggle) we take the text directly, ignoring that ancestor filter.
    //  In every case, if a LocText hasn't pushed to its TMP yet we resolve it from its resourceId, and
    //  hidden text is never read — see Consider.
    private static string BuildBodyText(ModalElementContainer modal, LocText? titleText)
    {
        var seen = new HashSet<int>();
        var parts = new List<(float order, string text)>();

        void Consider(TMP_Text t, bool allowUnderSelectable)
        {
            if (t == null) return;
            // A hidden text isn't on screen, so it isn't part of the modal's message. The CurrentOptions
            // pass reaches inactive children by design (it looks for text the option never activated), and
            // an option routinely hides whole sub-widgets — an AttackOption with no energy cost to show
            // does energyCostDisplay.SetActive(false). Speaking those would invent content the sighted
            // player can't see.
            if (!t.gameObject.activeInHierarchy) return;
            if (titleText != null && t.gameObject == titleText.gameObject) return;
            // Filter BEFORE marking seen: the container pass (allowUnderSelectable:false) rejects a
            // message that sits under the blocker Button, and the CurrentOptions pass must still be able
            // to pick it up — so a rejected text must NOT be recorded as already-seen.
            if (!allowUnderSelectable && t.GetComponentInParent<Selectable>() != null) return;
            if (!seen.Add(t.GetInstanceID())) return;
            var txt = t.text;
            if (string.IsNullOrWhiteSpace(txt))
            {
                // The loc id is the only fallback worth trying. NOT LocText.CurrentText: it lives on this
                // same GameObject, so it returns this very TMP's .text — the empty string we already have —
                // and when its TMP field is unset it dereferences a `uiText` that only LocText.Initialize()
                // assigns. On a modal option that was Instantiated this frame and never had text pushed
                // into it, both fields are null and the getter throws. That NRE unwound out of the old
                // handler, through ModalElementContainer.Open, and killed the match coroutine that was
                // opening the overlay.
                var loc = t.GetComponent<LocText>();
                txt = loc != null ? LocalizationUtils.TryGetGame(loc.resourceId) ?? "" : "";
            }
            if (!string.IsNullOrWhiteSpace(txt)) parts.Add((PopupUtils.ReadingOrder(t.transform), txt.Trim()));
        }

        foreach (var t in modal.GetComponentsInChildren<TMP_Text>(includeInactive: false))
            Consider(t, allowUnderSelectable: false);

        if (modal.CurrentOptions != null)
            foreach (var opt in modal.CurrentOptions)
            {
                if (opt == null || opt.GetComponent<Selectable>() != null) continue;   // skip buttons / toggles
                foreach (var t in opt.GetComponentsInChildren<TMP_Text>(includeInactive: true))
                    Consider(t, allowUnderSelectable: true);
            }

        return string.Join(". ", parts.OrderBy(p => p.order).Select(p => p.text));
    }

    [HarmonyPatch(typeof(ModalElementContainer), "Open")]
    private static class OpenHook
    {
        private static void Postfix(ModalElementContainer __instance) => s_instance.Open(__instance);
    }

    [HarmonyPatch(typeof(ModalElementContainer), "Close")]
    private static class CloseHook
    {
        private static void Postfix(ModalElementContainer __instance)
        {
            if (s_instance.m_modal != __instance) return;
            s_instance.m_modal = null;
            foreach (var g in s_instance.m_groups) g.Detach();
            AccessibilityMgr.HideUI(s_instance);
        }
    }
}
