using System.Collections.Generic;
using System.Linq;
using System.Text;
using HarmonyLib;
using TMPro;
using TPCI.Localization;
using UnityEngine;
using UnityEngine.UI;
using static Accessibility.GameReflect;

namespace Accessibility;

// Deck validation drawer (enhanced in 1.5.0) — driver: DeckValidityOverlay (an OverlayScreenGroup) shown on Test, or
// on Exit when the deck is invalid (HUBEditDeckController.OpenDeckValidationPopupWithMode / CanExit). Previously a
// blind player only heard "invalid"; this reads WHY — the bottom summary plus the per-rule / per-card detail rows the
// drawer spawns (DeckValidationRowTextBuilder, the 1.5.0 addition). Buttons: a left cancel/close and (on
// exit-invalid) a red Exit button.
//
// DeckValidityOverlay overrides OnActivate (patched directly); close = the shared base OnDeActivate, identity-checked.
internal sealed class AccessibleDeckValidity : AccessibleUI
{
    private static readonly AccessibleDeckValidity s_instance = new();

    private readonly KeyboardNavigator m_nav;
    private DeckValidityOverlay? m_overlay;

    private AccessibleDeckValidity()
    {
        m_nav = new KeyboardNavigator(this) { AllowTab = false, OnBack = Close };
    }

    public void HandleInput()
    {
        if (m_overlay == null || !m_overlay.gameObject.activeInHierarchy) { Hide(); return; }
        m_nav.HandleInput();
    }

    public string GetHelp() => LocalizationUtils.Get(LocalizationKey.COLLECTION_POPUP_HELP);

    public void OnGainedFocus()
    {
        if (m_overlay == null) return;
        var buttons = Buttons(m_overlay);
        m_nav.SetItems(buttons);
        m_nav.AllowHorizontal = true;
        AccessibilityMgr.Output(this, Message(), interrupt: true);
        if (buttons.Count > 0) m_nav.FocusFirst(announce: true, interruptPrior: false);
    }

    // Escape = cancel (stay in the editor); the red Exit button (when present) is reached by arrow + Enter.
    private void Close() => m_overlay?.Deactivate();

    private void Hide()
    {
        m_overlay = null;
        AccessibilityMgr.HideUI(this);
    }

    // The popup's prefab carries up to three buttons — a left Cancel/Close, the red Exit, and a redundant corner
    // close — and the greyed one stays active-but-not-interactable. A blind player heard all three (one dead, plus
    // "close" and "cancel" doing the same thing). Surface only the game's two CANONICAL actions, each only when
    // usable: the left button (Cancel/Close, text driven by leftCancelCloseLocText) and the red exitButton
    // (Exit-anyway, shown only on exit-with-errors). Escape still cancels via OnBack, so a lone dismiss is never
    // stranded.
    private static List<Selectable> Buttons(DeckValidityOverlay overlay)
    {
        var buttons = new List<Selectable>();
        void TryAdd(Button? b)
        {
            if (b != null && b.isActiveAndEnabled && b.interactable && !buttons.Contains(b)) buttons.Add(b);
        }
        var left = ReadField<LocText>(overlay, "leftCancelCloseLocText");
        TryAdd(left != null ? left.GetComponentInParent<Button>() : null);
        TryAdd(ReadField<Button>(overlay, "exitButton"));
        if (buttons.Count > 0) return buttons;

        // Fallback if the game renames those fields: any interactable button, deduped by label.
        var seen = new HashSet<string>();
        foreach (var b in CollectionUi.Buttons(overlay).Where(b => b.interactable))
            if (seen.Add(KeyboardNavigator.ResolveBaseLabel(b))) buttons.Add(b);
        return buttons;
    }

    // The high-level summary ("This deck isn't legal… please address the following") plus the detail rows the
    // drawer spawns (each invalid rule / card). Both are the game's own localized text.
    private string Message()
    {
        var title = LocalizationUtils.Get(LocalizationKey.COLLECTION_DECK_VALIDATION);
        if (m_overlay == null) return title;
        var sb = new StringBuilder();
        Append(sb, CollectionUi.Text(ReadField<LocText>(m_overlay, "bottomSummary")));
        Component? drawer = m_overlay.deckValidityDrawer;
        if (drawer != null)
            foreach (var t in drawer.GetComponentsInChildren<TMP_Text>(includeInactive: false))
                Append(sb, CollectionUi.Text(t));
        return sb.Length == 0 ? title : LocalizationUtils.Format(LocalizationKey.COLLECTION_TITLE_BODY, title, sb.ToString());
    }

    private static void Append(StringBuilder sb, string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return;
        if (sb.Length > 0) sb.Append(". ");
        sb.Append(s);
    }

    private void Open(DeckValidityOverlay overlay)
    {
        m_overlay = overlay;
        if (AccessibilityMgr.IsFocused(this)) OnGainedFocus();
        else AccessibilityMgr.ShowUI(this);
    }

    [HarmonyPatch(typeof(DeckValidityOverlay), "OnActivate", new System.Type[] { })]
    private static class OnActivateHook
    {
        private static void Postfix(DeckValidityOverlay __instance) => s_instance.Open(__instance);
    }

    [HarmonyPatch(typeof(OverlayScreenGroup), "OnDeActivate")]
    private static class OnDeActivateHook
    {
        private static void Postfix(OverlayScreenGroup __instance)
        {
            if (s_instance.m_overlay != null && __instance == s_instance.m_overlay) s_instance.Hide();
        }
    }
}
