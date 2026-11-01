using System;
using HarmonyLib;
using TPCI.Localization;
using static Accessibility.GameReflect;

namespace Accessibility;

// "Set complete" celebration — CardDexSetCompleteController, a plain MonoBehaviour shown over the Card Dex by
// SetCardDexContent. No code path closes it (CloseOverlay has no caller in source, so it hangs on a prefab control or
// an animation event). So: speak it, take the keyboard while it is up, let go as soon as it is gone, and let Enter or
// Escape close it. Never before the animation has finished: disabling the object would stop the animation event
// that runs the game's completion callback and stall the rewards flow behind it.
internal sealed class AccessibleCardDexSetComplete : AccessibleUI
{
    private static readonly AccessibleCardDexSetComplete s_instance = new();

    private CardDexSetCompleteController? m_controller;

    public void HandleInput()
    {
        if (m_controller == null || !m_controller.gameObject.activeInHierarchy) { Hide(); return; }
        if ((AccessibleKey.CONFIRM.IsPressed() || CollectionUi.BackPressed()) && ReadField<Action>(m_controller, "_onAnimationCompleteCallback") == null)
            m_controller.CloseOverlay();
    }

    public string GetHelp() => LocalizationUtils.Get(LocalizationKey.COLLECTION_SET_COMPLETE_HELP);

    public void OnGainedFocus()
    {
        if (CollectionUi.Text(ReadField<LocText>(m_controller, "textSetComplete")) is { } text) AccessibilityMgr.Output(this, text, interrupt: true);
    }

    private void Hide()
    {
        m_controller = null;
        AccessibilityMgr.HideUI(this);
    }

    [HarmonyPatch(typeof(CardDexSetCompleteController), "SetCardDexContent")]
    private static class SetContentHook
    {
        private static void Postfix(CardDexSetCompleteController __instance)
        {
            if (!__instance.gameObject.activeInHierarchy) return;   // the game skipped it (missing art)
            s_instance.m_controller = __instance;
            if (AccessibilityMgr.IsFocused(s_instance)) s_instance.OnGainedFocus();
            else AccessibilityMgr.ShowUI(s_instance);
        }
    }
}
