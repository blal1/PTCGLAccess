using HarmonyLib;
using TPCI.Localization;
using static Accessibility.GameReflect;

namespace Accessibility;

// Welcome / tutorial speech bubble shown by the Professor NPC (e.g. "Welcome to Pokémon Trading
// Card Game Live! Let's customize your look a little. I assigned you a random avatar...").
// Driver: ProfessorTooltip — subclass of OverlayScreenGroup.
//
// Activation: ProfessorTooltip.ActivateWithText(string locID) sets the tooltipText LocText
// via SetTextByID and calls Activate(). We hook that and speak the now-rendered text.
//
// Deactivation: OverlayScreenGroup.OnDeActivate fires when the overlay closes (via Deactivate
// or any internal animation-driven close). We patch the base method and filter by instance.
//
// The bubble itself has no Selectable controls — there's just a small triangle indicator and
// the player taps anywhere to dismiss / advance. For keyboard, any of Enter / Space / Esc /
// Backspace dismisses (ProfessorTooltip.Deactivate). If the FTUE chains messages (prof1
// → prof2 → ...) each new ActivateWithText call updates the text and we re-announce.
internal sealed class AccessibleProfessorTooltip : AccessibleUI
{
    private static readonly AccessibleProfessorTooltip s_instance = new();

    private ProfessorTooltip? m_tooltip;

    public void HandleInput()
    {
        if (m_tooltip == null)
        {
            AccessibilityMgr.HideUI(this);
            return;
        }
        if (AccessibleKey.CONFIRM.IsPressed() || AccessibleKey.SPACE.IsPressed() || AccessibleKey.ESCAPE.IsPressed() || AccessibleKey.BACK.IsPressed())
            m_tooltip.Deactivate();   // the OnDeActivate hook hides us
    }

    public string GetHelp() => LocalizationUtils.Get(LocalizationKey.POPUP_TOOLTIP_HELP);

    public void OnGainedFocus()
    {
        var text = PopupUtils.Text(ReadField<LocText>(m_tooltip, "tooltipText"));
        if (text != null) AccessibilityMgr.Output(this, text, interrupt: true);
    }

    [HarmonyPatch(typeof(ProfessorTooltip), "ActivateWithText")]
    private static class ActivateHook
    {
        // Same tooltip, new message (FTUE step advanced): re-announce without re-stacking.
        private static void Postfix(ProfessorTooltip __instance)
        {
            s_instance.m_tooltip = __instance;
            PopupUtils.ShowOrRefresh(s_instance);
        }
    }

    [HarmonyPatch(typeof(OverlayScreenGroup), "OnDeActivate")]
    private static class DeactivateHook
    {
        private static void Postfix(OverlayScreenGroup __instance)
        {
            if (s_instance.m_tooltip == null || s_instance.m_tooltip != __instance) return;
            s_instance.m_tooltip = null;
            AccessibilityMgr.HideUI(s_instance);
        }
    }
}
