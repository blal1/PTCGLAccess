using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using TMPro;
using TPCI.Localization;
using TPCI.UI.News;
using UnityEngine;
using UnityEngine.UI;
using static Accessibility.GameReflect;

namespace Accessibility;

// News overlay — the post-login "what's new / expansion launch" carousel (driver: NewsOverlay, an
// OverlayScreenGroup in TPCI.UI.News). It's a sequence of news_pages: PreviousClicked/NextClicked flip
// pages, the prev/next buttons auto-show, and the CONTINUE button (`closeButton`) appears ONLY on the
// last page (→ CloseClicked marks news read + closes). The gift overlays literally wait on
// NewsDataManager.IsRead, so this is the first post-tutorial popup in the chain.
//
// Like the other overlays it pops OVER the HUB without pausing it, so without this the HUB keeps
// focus behind it. Model (matches the game): Left/Right = prev/next page, each page speaks (header +
// body) as you land on it; Enter pages forward too and, on the last page, presses CONTINUE to finish.
// A page is read on the frame after it changes, so the page-change methods have repopulated its content.
internal sealed class AccessibleNews : AccessibleUI
{
    private static readonly AccessibleNews s_instance = new();
    private static readonly FieldInfo? s_pageIndex = AccessTools.Field(typeof(NewsOverlay), "_currentPageIndex");
    private static readonly MethodInfo? s_totalPages = AccessTools.PropertyGetter(typeof(NewsOverlay), "TotalPageCount");

    private NewsOverlay? m_overlay;
    private int m_lastReadPage = -1;

    public void HandleInput()
    {
        if (m_overlay == null || !m_overlay.gameObject.activeInHierarchy)
        {
            Close();
            return;
        }

        var page = CurrentPage();
        if (page != m_lastReadPage)
        {
            m_lastReadPage = page;
            ReadPage();
        }

        if (AccessibleKey.READ_NEXT_ITEM.IsPressed()) m_overlay.NextClicked();
        else if (AccessibleKey.READ_PREV_ITEM.IsPressed() || AccessibleKey.BACK.IsPressed()) m_overlay.PreviousClicked();
        else if (AccessibleKey.CONFIRM.IsPressed())
        {
            if (OnLastPage()) m_overlay.CloseClicked();   // finish (marks news read + closes)
            else m_overlay.NextClicked();
        }
    }

    public string GetHelp() => LocalizationUtils.Get(LocalizationKey.POPUP_NEWS_HELP);

    // The page is (re)read by the next HandleInput, once its content has settled.
    public void OnGainedFocus() => m_lastReadPage = -1;

    private void Close()
    {
        m_overlay = null;
        AccessibilityMgr.HideUI(this);
    }

    private void ReadPage()
    {
        var parts = new List<string>();
        var total = TotalPages();
        if (total > 1) parts.Add(LocalizationUtils.Format(LocalizationKey.POPUP_NEWS_PAGE_FORMAT, CurrentPage() + 1, total));

        if (PopupUtils.Text(ReadField<LocText>(m_overlay, "headerLoc")) is { } header) parts.Add(header);
        if (ReadField<Component>(m_overlay, "newsPage") is { } page)
            foreach (var t in page.GetComponentsInChildren<TMP_Text>(includeInactive: false))
                if (!string.IsNullOrWhiteSpace(t.text)) parts.Add(t.text);

        // On the last page the CONTINUE button is shown — read it by its label (the affordance), so
        // the player knows the carousel ends here and Enter finishes.
        if (OnLastPage() && PopupUtils.Text(ReadField<Button>(m_overlay, "closeButton")) is { } label) parts.Add(label);

        AccessibilityMgr.Output(this, parts.Count > 0 ? string.Join(". ", parts) : LocalizationUtils.Get(LocalizationKey.POPUP_NEWS), interrupt: true);
    }

    private bool OnLastPage() => ReadField<Button>(m_overlay, "closeButton") is { } close && close.gameObject.activeInHierarchy;

    private int CurrentPage() => m_overlay != null && s_pageIndex?.GetValue(m_overlay) is int i ? i : 0;

    private int TotalPages()
    {
        try { return m_overlay != null && s_totalPages?.Invoke(m_overlay, null) is int n ? n : 0; }
        catch { return 0; }
    }

    [HarmonyPatch(typeof(NewsOverlay), "Activate")]
    private static class ActivateHook
    {
        private static void Postfix(NewsOverlay __instance)
        {
            s_instance.m_overlay = __instance;
            PopupUtils.ShowOrRefresh(s_instance);
        }
    }

    [HarmonyPatch(typeof(NewsOverlay), "OnDeActivate")]
    private static class DeactivateHook
    {
        private static void Postfix(NewsOverlay __instance)
        {
            if (s_instance.m_overlay == __instance) s_instance.Close();
        }
    }
}
