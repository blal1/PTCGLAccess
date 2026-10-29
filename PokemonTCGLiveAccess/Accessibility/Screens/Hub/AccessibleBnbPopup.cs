using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using TMPro;
using UnityEngine.UI;

namespace Accessibility;

// The Build & Battle popup family — the dialog-style OverlayScreenGroups the buy-in / event flow throws up
// (BuildAndBattlePopupManager). All are title/body/buttons over a marketing splash, so one component reads them
// generically instead of one per popup:
//   • BuildAndBattlePurchaseOverlay     — "CONFIRM EXCHANGE" buy-in (title/body/cost/BACK/EXCHANGE)
//   • BuildAndBattleGenericOverlay      — event closed / no active event / forfeit confirm / almost over
//   • BuildAndBattleBoxOpeningPopup     — box-opening reveal; its proceed button appears AFTER the animation
//   • BuildAndBattleDeckInspectorController — the starter-deck inspector (advance-able; full card review TODO)
// Booster opening uses ContentItemGrantingOverlay → the item grant component.
//
// Opened by the base OverlayScreenGroup.Activate (none of the four overrides it), filtered on the type; closed by
// the base OnDeActivate (none overrides it either). We read every non-button text (title, body, cost, revealed card
// name) and present every active button; a per-frame button check catches buttons that only appear after an
// animation, and the first announce waits one frame so values set right after Activate (the cost) are populated.
internal sealed class AccessibleBnbPopup : AccessibleUI
{
    private static readonly AccessibleBnbPopup s_instance = new();

    private readonly KeyboardNavigator m_nav;
    private OverlayScreenGroup? m_overlay;
    private bool m_announcePending;
    private string m_lastButtonSig = "";

    private AccessibleBnbPopup()
    {
        m_nav = new KeyboardNavigator(this) { OnBack = Cancel };
    }

    private static bool IsHandled(OverlayScreenGroup o) =>
        o is BuildAndBattlePurchaseOverlay or BuildAndBattleGenericOverlay or BuildAndBattleBoxOpeningPopup or BuildAndBattleDeckInspectorController;

    [HarmonyPatch(typeof(OverlayScreenGroup), "Activate")]
    private static class ActivateHook
    {
        private static void Postfix(OverlayScreenGroup __instance)
        {
            if (!IsHandled(__instance)) return;
            var reopened = s_instance.m_overlay != null && AccessibilityMgr.IsFocused(s_instance);
            s_instance.m_overlay = __instance;
            if (reopened) s_instance.m_announcePending = true;   // another popup of the family replaced this one
            else AccessibilityMgr.ShowUI(s_instance);
        }
    }

    [HarmonyPatch(typeof(OverlayScreenGroup), "OnDeActivate")]
    private static class DeactivateHook
    {
        private static void Postfix(OverlayScreenGroup __instance)
        {
            if (s_instance.m_overlay != __instance) return;
            s_instance.Close();
        }
    }

    public string GetHelp() => LocalizationUtils.Get(LocalizationKey.HUB_POPUP_HELP);

    public void OnGainedFocus()
    {
        Rebuild();
        m_lastButtonSig = ButtonSig();
        m_announcePending = true;
    }

    public void HandleInput()
    {
        if (m_overlay == null || !m_overlay.gameObject.activeInHierarchy)
        {
            Close();
            return;
        }
        // Deferred announce (so the cost — set right after Activate — is ready).
        if (m_announcePending)
        {
            m_announcePending = false;
            Rebuild();
            Announce();
            return;
        }
        // Buttons can appear after an animation (the box-opening proceed button), so re-scan; when an action first
        // becomes available, re-read the full content + that action (by then the card is revealed).
        var sig = ButtonSig();
        if (sig != m_lastButtonSig)
        {
            var hadNone = m_nav.Count == 0;
            m_lastButtonSig = sig;
            Rebuild();
            if (hadNone && m_nav.Count > 0) Announce();
            return;
        }
        m_nav.HandleInput();
    }

    private void Close()
    {
        m_overlay = null;
        AccessibilityMgr.HideUI(this);
    }

    // Escape: an explicit close/cancel/back button if there is one; otherwise nothing (never auto-proceed a buy-in).
    private void Cancel() => Buttons().FirstOrDefault(b => IsCloseName(b.gameObject.name))?.onClick.Invoke();

    private void Rebuild()
    {
        m_nav.SetItems(Buttons().Cast<Selectable>().OrderBy(s => HubUi.ReadingOrder(s.transform)));
        // Dialogs vary (side-by-side or stacked).
        m_nav.AllowVertical = true;
        m_nav.AllowHorizontal = true;
    }

    private void Announce()
    {
        var text = BodyText();
        AccessibilityMgr.Output(this, string.IsNullOrWhiteSpace(text) ? LocalizationUtils.Get(LocalizationKey.HUB_BNB) : text, interrupt: true);
        m_nav.FocusFirst(announce: true, interruptPrior: false);
    }

    private List<Button> Buttons() =>
        m_overlay == null ? new List<Button>() : m_overlay.GetComponentsInChildren<Button>(includeInactive: false).Where(b => b.isActiveAndEnabled).ToList();

    private string ButtonSig() => string.Join(",", Buttons().Select(b => b.gameObject.name));

    // Every readable text that isn't a button label (title, body, cost, revealed card name), in reading order.
    // Skipping text under a Selectable keeps button labels out (they're presented as buttons) so they aren't read
    // twice.
    private string BodyText()
    {
        if (m_overlay == null) return "";
        var parts = new List<string>();
        foreach (var t in m_overlay.GetComponentsInChildren<TMP_Text>(includeInactive: false).OrderBy(t => HubUi.ReadingOrder(t.transform)))
        {
            if (t.GetComponentInParent<Selectable>() != null) continue;
            var s = HubUi.Text(t);
            if (s != null && s.Any(char.IsLetterOrDigit) && !parts.Contains(s)) parts.Add(s);
        }
        return string.Join(". ", parts);
    }

    private static bool IsCloseName(string name)
    {
        var n = name.ToLowerInvariant();
        return n.Contains("close") || n.Contains("cancel") || n.Contains("back") || n.Contains("deny") || n.Contains("exit");
    }
}
