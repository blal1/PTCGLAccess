using System.Collections.Generic;
using HarmonyLib;
using TMPro;
using TPCI.Localization;
using UnityEngine.UI;
using static Accessibility.GameReflect;

namespace Accessibility;

// Confirmation popup used across the game's startup flows (SimpleMessage in TPCI.RainierClient), e.g. the
// language confirmation over the language picker, the PTC login retry, errors.
//
// Both public OpenNewMessage overloads (string args and loc-id args) funnel into the private Open(confirm,
// closeAction, close), so that one hook covers them; Close() hides us. While we're on top the component
// underneath gets no keys.
//
// Selectables exposed:
//   - acceptButton (the big colored CTA, e.g. "CONTINUE")
//   - closeButton (the small ✕, only present when a cancel callback was supplied)
internal sealed class AccessibleSimpleMessage : AccessibleUI, ITitledComponent
{
    private static readonly AccessibleSimpleMessage s_instance = new();

    private readonly KeyboardNavigator m_nav;
    private SimpleMessage? m_msg;
    private AccessibleComponent? m_parent;

    private AccessibleSimpleMessage()
    {
        m_nav = new KeyboardNavigator(this);
    }

    public string? Title => m_msg != null ? ReadLocText(m_msg, "messageTitleLoc") : null;

    public void HandleInput()
    {
        if (m_msg == null)
        {
            AccessibilityMgr.HideUI(this);
            return;
        }
        m_nav.HandleInput();
    }

    public string GetHelp() => LocalizationUtils.Get(LocalizationKey.POPUP_BUTTONS_HELP);

    // Title + body barging in, then the focused button QUEUED after it as its own announce — two separate
    // utterances, so the button reads as a distinct, independently-navigable item. Interrupting the body with
    // the button was the old bug (FocusFirst() defaults to interruptPrior:true).
    public void OnGainedFocus()
    {
        if (m_msg == null) return;
        var preamble = PopupUtils.Preamble(Title, ReadLocText(m_msg, "messageTextLoc"), m_parent);
        if (!string.IsNullOrWhiteSpace(preamble)) AccessibilityMgr.Output(this, preamble!, interrupt: true);
        PopupUtils.FocusQueued(m_nav);
    }

    private void Open(SimpleMessage msg)
    {
        m_msg = msg;
        if (AccessibilityMgr.Focused != this) m_parent = AccessibilityMgr.Focused;

        var items = new List<Selectable>();
        var accept = ReadField<Button>(msg, "acceptButton");
        var close = ReadField<Button>(msg, "closeButton");
        if (accept != null && accept.gameObject.activeInHierarchy) items.Add(accept);
        var hasClose = close != null && close.gameObject.activeInHierarchy;
        if (hasClose) items.Add(close!);
        m_nav.SetItems(items);

        // Escape / Backspace = invoke the X. SimpleMessage hides closeButton when the caller
        // didn't supply a closeAction (OK-only dialogs); in that case OnBack stays null so
        // back is a no-op, matching the game's mouse behavior.
        m_nav.OnBack = hasClose ? () => close!.onClick?.Invoke() : null;

        // The same SimpleMessage instance is reused for different messages: re-announce when already up.
        PopupUtils.ShowOrRefresh(this);
    }

    private static string? ReadLocText(SimpleMessage m, string fieldName)
    {
        var loc = ReadField<LocText>(m, fieldName);
        if (loc == null) return null;

        // Prefer the live TMP text, but the loc-args overload (SetTextByIDFormat — used by the logout
        // confirm) may not have pushed to the TMP yet when our Open postfix runs. Fall back to the
        // LocText's own CurrentText, then resolve its resourceId through the loc table, so the body is
        // never silently empty. (Without this the logout body read blank because only the title — set
        // via SetTextByID — had rendered.)
        var tmp = loc.GetComponent<TMP_Text>();
        if (tmp != null && !string.IsNullOrWhiteSpace(tmp.text)) return tmp.text;
        if (!string.IsNullOrWhiteSpace(loc.CurrentText)) return loc.CurrentText;
        return LocalizationUtils.TryGetGame(loc.resourceId);
    }

    [HarmonyPatch(typeof(SimpleMessage), "Open")]
    private static class OpenHook
    {
        private static void Postfix(SimpleMessage __instance) => s_instance.Open(__instance);
    }

    [HarmonyPatch(typeof(SimpleMessage), "Close")]
    private static class CloseHook
    {
        private static void Postfix(SimpleMessage __instance)
        {
            if (s_instance.m_msg != __instance) return;
            s_instance.m_msg = null;
            AccessibilityMgr.HideUI(s_instance);
        }
    }
}
