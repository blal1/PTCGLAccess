using System.Collections.Generic;
using HarmonyLib;
using TPCI.Localization;
using UnityEngine;
using UnityEngine.UI;
using static Accessibility.GameReflect;

namespace Accessibility;

// Gift-granting overlays — the launch / seasonal "ACCEPT GIFT" popups (e.g. the Mega Evolution Chaos
// Rising launch gift: floral card sleeves + Build-and-Battle event tokens). Driver: GiftGrantingOverlay
// (abstract OverlayScreenGroup; concrete e.g. ExpansionChallengeSeasonalStarterGiftOverlay).
// ActivateGiftPopup wires the super-title / title / body / gift-name LocTexts, the reward slots, and
// the `claimButton` ("ACCEPT GIFT", loc global_accept_gift) whose onClick claims the gift, closes this
// overlay, and chains into the ContentItemGrantingOverlay reveal (AccessibleItemGrant).
//
// Like the reward overlays, these pop OVER the HUB without pausing it, so without this the HUB
// keeps focus behind the modal and ACCEPT GIFT is unreachable. We hook ActivateGiftPopup (present) /
// OnDeActivate (dismiss), read the title + body + reward count, and claim on Enter.
internal sealed class AccessibleGift : AccessibleUI
{
    private static readonly AccessibleGift s_instance = new();

    private readonly GiftLines m_lines;
    private GiftGrantingOverlay? m_overlay;

    private AccessibleGift()
    {
        m_lines = new GiftLines(this);
    }

    public void HandleInput()
    {
        if (m_overlay == null || !m_overlay.gameObject.activeInHierarchy)
        {
            Close();
            return;
        }
        if (AccessibleKey.CONFIRM.IsPressed())
        {
            var btn = ReadField<Button>(m_overlay, "claimButton");
            if (btn != null) btn.onClick.Invoke();
            else AccessibilityMgr.Output(this, LocalizationUtils.Get(LocalizationKey.POPUP_GIFT_CANT_CLAIM), interrupt: true);
            return;
        }
        m_lines.HandleInput();
    }

    public string GetHelp() => LocalizationUtils.Get(LocalizationKey.POPUP_GIFT_HELP);

    // Read the whole gift on arrival (title + body + reward count) — no control hint; the
    // design speaks for itself. Down/Up re-read line by line; Enter accepts.
    public void OnGainedFocus()
    {
        m_lines.Reset();
        var lines = Lines();
        AccessibilityMgr.Output(this, lines.Count > 0 ? string.Join(". ", lines) : LocalizationUtils.Get(LocalizationKey.POPUP_GIFT), interrupt: true);
    }

    private void Close()
    {
        m_overlay = null;
        AccessibilityMgr.HideUI(this);
    }

    private List<string> Lines()
    {
        var lines = new List<string>();
        if (m_overlay == null) return lines;
        var texts = new[]
        {
            ReadField<LocText>(m_overlay, "superTitleLoc"),
            ReadField<LocText>(m_overlay, "titleLoc"),
            ReadField<LocText>(m_overlay, "bodyLoc"),
            ReadField<LocText>(m_overlay, "giftNameLoc"),
        };
        foreach (var t in texts)
            if (PopupUtils.Text(t) is { } s) lines.Add(s);
        var n = RewardCount();
        if (n > 0) lines.Add(n == 1 ? LocalizationUtils.Get(LocalizationKey.POPUP_ONE_REWARD) : LocalizationUtils.Format(LocalizationKey.POPUP_REWARDS_FORMAT, n));
        return lines;
    }

    private int RewardCount()
    {
        if (ReadField<Transform>(m_overlay, "rewardHolder") is not { } holder) return 0;
        var n = 0;
        foreach (Transform c in holder) if (c != null && c.gameObject.activeSelf) n++;
        return n;
    }

    private sealed class GiftLines : AccessibleItem
    {
        public GiftLines(AccessibleGift parent) : base(parent) { }

        protected internal override List<string> GetLines() => s_instance.Lines();
    }

    [HarmonyPatch(typeof(GiftGrantingOverlay), "ActivateGiftPopup")]
    private static class ActivateHook
    {
        private static void Postfix(GiftGrantingOverlay __instance)
        {
            s_instance.m_overlay = __instance;
            PopupUtils.ShowOrRefresh(s_instance);
        }
    }

    [HarmonyPatch(typeof(GiftGrantingOverlay), "OnDeActivate")]
    private static class DeactivateHook
    {
        private static void Postfix(GiftGrantingOverlay __instance)
        {
            if (s_instance.m_overlay == __instance) s_instance.Close();
        }
    }
}
