using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;

namespace Accessibility;

// Booster odds — BoosterDropRatesScreenController (OverlayManager.boosterDropRatesOverlay), opened from a product's
// booster slot. Its rows arrive after a config download (LoadBoosterDropTable), so the text is announced when the first
// row appears. Up/Down read the rows; Enter opens the game's odds web page, as its own button does.
internal sealed class AccessibleBoosterDropRates : AccessibleUI
{
    private static readonly AccessibleBoosterDropRates s_instance = new();

    private readonly RateLines m_lines;
    private BoosterDropRatesScreenController? m_overlay;
    private bool m_announced;

    private AccessibleBoosterDropRates() => m_lines = new RateLines(this);

    public void HandleInput()
    {
        if (m_overlay == null || !m_overlay.gameObject.activeInHierarchy) { Hide(); return; }
        if (CollectionUi.BackPressed()) { m_overlay.Deactivate(); return; }
        if (!m_announced && m_overlay.cardRowHolder != null && m_overlay.cardRowHolder.GetComponentInChildren<DropRateCardRowController>(includeInactive: false) != null)
        {
            m_announced = true;
            m_lines.ReadFirstLine();
        }
        if (AccessibleKey.CONFIRM.IsPressed())
        {
            AccessibilityMgr.Output(this, LocalizationUtils.Format(LocalizationKey.SHOPUI_OPENS_BROWSER, LocalizationUtils.Get(LocalizationKey.SHOPUI_BOOSTER_ODDS)));
            m_overlay.OnClickBoosterOdds();
            return;
        }
        m_lines.HandleInput();
    }

    public string GetHelp() => LocalizationUtils.Get(LocalizationKey.SHOPUI_ODDS_HELP);

    public void OnGainedFocus()
    {
        m_announced = false;
        m_lines.Reset();
        AccessibilityMgr.Output(this, LocalizationUtils.Get(LocalizationKey.SHOPUI_BOOSTER_ODDS), interrupt: true);
    }

    private sealed class RateLines : AccessibleItem
    {
        private readonly AccessibleBoosterDropRates m_owner;

        public RateLines(AccessibleBoosterDropRates owner) : base(owner) => m_owner = owner;

        protected internal override List<string> GetLines()
        {
            var lines = new List<string>();
            var overlay = m_owner.m_overlay;
            if (overlay == null) return lines;
            if (CollectionUi.Text(overlay.boosterCountTxt) is { } count) lines.Add(count);
            if (overlay.cardRowHolder == null) return lines;
            foreach (var row in overlay.cardRowHolder.GetComponentsInChildren<DropRateCardRowController>(includeInactive: false))
            {
                var rates = row.dropRateHolder != null
                    ? row.dropRateHolder.GetComponentsInChildren<DropRateController>(includeInactive: false).Select(r => CollectionUi.Text(r.descriptionTxt)).Where(t => t != null)
                    : Enumerable.Empty<string?>();
                var extra = row.additionalRarityMessage != null && row.additionalRarityMessage.activeInHierarchy ? CollectionUi.FirstLetterText(row.additionalRarityMessage.transform) : null;
                var body = string.Join(", ", extra != null ? rates.Append(extra) : rates);
                lines.Add(LocalizationUtils.Format(LocalizationKey.COLLECTION_TITLE_BODY, CollectionUi.Text(row.titleTxt) ?? "", body));
            }
            return lines;
        }
    }

    private void Hide()
    {
        m_overlay = null;
        AccessibilityMgr.HideUI(this);
    }

    [HarmonyPatch(typeof(BoosterDropRatesScreenController), "OnActivate", new Type[] { })]
    private static class OnActivateHook
    {
        private static void Postfix(BoosterDropRatesScreenController __instance)
        {
            s_instance.m_overlay = __instance;
            if (AccessibilityMgr.IsFocused(s_instance)) s_instance.OnGainedFocus();
            else AccessibilityMgr.ShowUI(s_instance);
        }
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
