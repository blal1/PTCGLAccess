using System.Text.RegularExpressions;
using HarmonyLib;
using TMPro;
using static Accessibility.GameReflect;

namespace Accessibility;

// Startup loading ("Catching wild Pokémon 49/53"). Driver: StartupScreenText.UpdateLoadString(step, final), called
// once per game-manager initialisation (~53 times). The text rotates between phase phrases (loading_screen_1..8)
// formatted "<phrase> {0}/{1}". Only a new phrase is spoken: the counter ticks too fast to be useful, and phrases
// are queued, not interrupting, because phase changes come close together.
//
// Reference shape for every screen: a singleton component, a nested [HarmonyPatch] hook class that hands it to
// AccessibilityMgr, and nothing that speaks or reads keys outside HandleInput / OnGainedFocus / the hooks.
internal sealed class AccessibleLoadingScreen : AccessibleScreen
{
    private static readonly AccessibleLoadingScreen s_instance = new();
    private static readonly Regex s_trailingCounter = new(@"\s*\d+\s*[/\\]\s*\d+\s*$", RegexOptions.Compiled);

    private string? m_lastPhrase;

    public void HandleInput() { }

    public string GetHelp() => LocalizationUtils.Get(LocalizationKey.LOADING_HELP);

    public void OnGainedFocus()
    {
        if (m_lastPhrase != null) AccessibilityMgr.Output(this, m_lastPhrase, interrupt: false);
    }

    private void OnProgress(StartupScreenText text, int step, bool final)
    {
        if (step > 0 || final) AccessibilityMgr.SetScreen(this);

        var phrase = s_trailingCounter.Replace(ReadField<TMP_Text>(text, "loadingText")?.text ?? "", "").Trim();
        if (phrase.Length > 0 && phrase != m_lastPhrase)
        {
            m_lastPhrase = phrase;
            AccessibilityMgr.Output(this, phrase, interrupt: false);
        }

        if (final)
        {
            m_lastPhrase = null;
            AccessibilityMgr.ClearScreen(this);
        }
    }

    [HarmonyPatch(typeof(StartupScreenText), "UpdateLoadString")]
    private static class UpdateLoadStringHook
    {
        private static void Postfix(StartupScreenText __instance, int step, bool final) => s_instance.OnProgress(__instance, step, final);
    }
}
