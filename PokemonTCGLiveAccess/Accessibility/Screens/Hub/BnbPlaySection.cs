using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using TPCI.Rainier.UI.DisplayModules.TimeRemaining;
using UnityEngine;
using UnityEngine.UI;
using static Accessibility.GameReflect;

namespace Accessibility;

// The BUILD & BATTLE tab's content, modeled as a SECTION of HOME (like the Learning Lab list). It's an event buy-in
// screen (HUBBnBBuyInScreenController → BnBBuyInScreenViewBindings): a captioned-image CAROUSEL of instruction slides,
// prev/next page buttons + page pips, the PLAY / buy-in button (EventBuyInButton, shows the entry cost or FREE), and
// an "ENDS IN …" countdown. The slide captions are info TEXT (CaptionedImagePagesUi.captionDisplay), not Selectables,
// so a generic sweep only surfaced the prev/next buttons — the player couldn't read what each slide says or hear
// the timer.
//
// Two rows: Instructions (Left/Right pages the carousel, reading each caption + "page X of N") and Play (Enter
// presses the buy-in button). Entry reads the event timer first. Refs are resolved live from the controller, so they
// always match the current event.
internal sealed class BnbPlaySection : AccessibleElement, INavSection
{
    private readonly Action m_escapeToParent;

    private TMP_Text? m_caption;       // current slide caption (updates when the page changes)
    private Button? m_prev;
    private Button? m_next;
    private Button? m_play;            // the buy-in / PLAY button
    private GameObject? m_buyInRoot;   // EventBuyInButton root (for the full PLAY + cost/FREE label)
    private Toggle[] m_pips = Array.Empty<Toggle>();   // page dots — count + which is on = page X of N
    private CaptionedImagePagesUi? m_carousel;
    private PagePipNavigationListener? m_pipNav;
    private TMP_Text? m_timerTitle;    // "ENDS IN"
    private TMP_Text? m_timerValue;    // "1W 4D"

    private int m_index;               // row: instructions first (when there is a caption), then play
    private int m_rows;
    private string m_lastCaption;      // last caption spoken — so the poll only fires on a real change

    // The section is recreated on every AccessibleHome rebuild, and paging can itself trigger a rebuild (prev/next
    // may disable at the first/last slide → the content fingerprint changes). Remember the row across rebuilds so
    // focus doesn't jump back, and seed m_lastCaption from the live caption so a rebuild never re-announces the
    // slide. A single B&B section exists at a time, so a static is safe.
    private static int s_rememberedRow;

    public BnbPlaySection(AccessibleComponent parent, Action escapeToParent) : base(parent)
    {
        m_escapeToParent = escapeToParent;
        Resolve();
        m_index = m_rows > 0 ? Mathf.Clamp(s_rememberedRow, 0, m_rows - 1) : 0;
        m_lastCaption = m_caption != null ? (m_caption.text?.Trim() ?? "") : "";
    }

    private void Resolve()
    {
        var ctrl = UnityEngine.Object.FindAnyObjectByType<HUBBnBBuyInScreenController>();
        if (ctrl == null) return;

        var view = ReadField<BnBBuyInScreenViewBindings>(ctrl, "buyInScreen");
        var carousel = view != null ? ReadField<CaptionedImagePagesUi>(view, "instructionCarousel") : null;
        m_carousel = carousel;
        m_caption = carousel != null ? ReadField<TextMeshProUGUI>(carousel, "captionDisplay") : null;

        var buyIn = view != null ? ReadField<EventBuyInButton>(view, "eventBuyInButton") : null;
        m_buyInRoot = buyIn != null ? buyIn.gameObject : null;
        m_play = buyIn != null ? ReadField<Button>(buyIn, "button") : null;

        var timer = ReadField<StandaloneTimeLeftDisplay>(ctrl, "timerDisplay");
        m_timerTitle = timer != null ? ReadField<TextMeshProUGUI>(timer, "titleText") : null;
        m_timerValue = timer != null ? ReadField<TextMeshProUGUI>(timer, "timeLeftText") : null;

        if (view != null)
        {
            var buttons = view.GetComponentsInChildren<Button>(includeInactive: false);
            m_prev = buttons.FirstOrDefault(b => b.gameObject.name.IndexOf("Prev", StringComparison.OrdinalIgnoreCase) >= 0);
            m_next = buttons.FirstOrDefault(b => b.gameObject.name.IndexOf("Next", StringComparison.OrdinalIgnoreCase) >= 0);
            // The pip listener knows its toggles; object names are only the fallback.
            m_pipNav = view.GetComponentInChildren<PagePipNavigationListener>(includeInactive: false);
            m_pips = ReadField<Toggle[]>(m_pipNav, "orderedToggles") ?? view.GetComponentsInChildren<Toggle>(includeInactive: false)
                .Where(t => t.gameObject.name.IndexOf("Pip", StringComparison.OrdinalIgnoreCase) >= 0)
                .ToArray();
        }

        m_rows = (m_caption != null ? 1 : 0) + (m_play != null ? 1 : 0);
    }

    // With no caption the only row is Play (index 0).
    private bool IsInstructionsRow => m_caption != null && m_index == 0;
    private bool IsPlayRow => m_play != null && m_index == m_rows - 1 && !IsInstructionsRow;

    public int Index => m_index;
    public int Count => m_rows;
    public bool IsAtTopRow => m_index <= 0;
    public bool IsAtBottomRow => m_index >= m_rows - 1;
    public int CurrentColumn => 0;

    public void FocusFirst(bool announce = true, bool interruptPrior = true) => FocusRow(0, announce, interruptPrior, entry: true);

    public void FocusFirstRow(int preferredColumn = -1, bool announce = true, bool interruptPrior = true) => FocusRow(0, announce, interruptPrior, entry: true);

    public void FocusLastRow(int preferredColumn = -1, bool announce = true, bool interruptPrior = true) => FocusRow(Math.Max(0, m_rows - 1), announce, interruptPrior, entry: false);

    public void RefocusCurrent(bool announce = true, bool interrupt = true)
    {
        if (announce) AnnounceRow(interrupt);
    }

    public bool HandleInput()
    {
        // The carousel may update the caption a frame after the prev/next click (or via a slide animation), so
        // detect the change here and read the new slide — rather than reading it right after the click and catching
        // the stale text.
        PollCaption();
        // Up/Down at the section edges are consumed by AccessibleHome for section escape.
        if (AccessibleKey.READ_NEXT_MENU_OPTION.IsPressed()) Move(+1);
        else if (AccessibleKey.READ_PREV_MENU_OPTION.IsPressed()) Move(-1);
        else if (IsInstructionsRow && AccessibleKey.READ_NEXT_ITEM.IsPressed()) Page(m_next, +1);
        else if (IsInstructionsRow && AccessibleKey.READ_PREV_ITEM.IsPressed()) Page(m_prev, -1);
        else if (AccessibleKey.CONFIRM.IsPressed())
        {
            if (IsPlayRow) Press(m_play);
            else AnnounceRow(interrupt: true);   // re-read the instructions slide
        }
        else if (AccessibleKey.ESCAPE.IsPressed() || AccessibleKey.BACK.IsPressed()) m_escapeToParent();
        else return false;
        return true;
    }

    private void FocusRow(int row, bool announce, bool interrupt, bool entry)
    {
        m_index = row;
        s_rememberedRow = row;
        if (!announce) return;
        if (entry && TimerText() is { } timer)
        {
            Output(timer, interrupt);
            AnnounceRow(interrupt: false);
        }
        else AnnounceRow(interrupt);
    }

    private void Move(int dir)
    {
        var next = m_index + dir;
        if (next < 0 || next >= m_rows) return;
        m_index = next;
        s_rememberedRow = next;
        AnnounceRow(interrupt: true);
    }

    // No Prev/Next button found by name (the name search is a guess about the prefab): the carousel's own page call,
    // which is what those buttons end up doing. The poll then reads the new caption.
    private void Page(Button? button, int dir)
    {
        if (button != null) { Press(button); return; }
        var count = m_pips.Length;
        if (m_carousel == null || count == 0) return;
        var cur = Math.Max(0, Array.FindIndex(m_pips, t => t != null && t.isOn));
        var page = cur + dir;
        if (page < 0 || page >= count) return;
        m_carousel.RenderPage(page);
        m_pipNav?.EnablePipAtIndex(page);
    }

    // Turns the page / buys in through the game's own button.
    private static void Press(Button? btn)
    {
        if (btn != null && btn.isActiveAndEnabled && btn.IsInteractable()) btn.onClick.Invoke();
    }

    private void PollCaption()
    {
        if (!IsInstructionsRow || m_caption == null) return;
        var cur = m_caption.text?.Trim() ?? "";
        if (cur.Length > 0 && cur != m_lastCaption) AnnounceInstructions(interrupt: true, withRole: false);
    }

    private void AnnounceRow(bool interrupt)
    {
        if (IsInstructionsRow) AnnounceInstructions(interrupt, withRole: true);
        else AnnouncePlay(interrupt);
    }

    private void AnnounceInstructions(bool interrupt, bool withRole)
    {
        var caption = m_caption != null ? m_caption.text?.Trim() : null;
        m_lastCaption = caption ?? "";
        var parts = new List<string>();
        if (withRole) parts.Add(LocalizationUtils.Get(LocalizationKey.HUB_BNB_INSTRUCTIONS));
        if (PageText() is { } page) parts.Add(page);
        if (!string.IsNullOrWhiteSpace(caption)) parts.Add(caption!);
        Output(parts.Count > 0 ? string.Join(", ", parts) : LocalizationUtils.Get(LocalizationKey.HUB_BNB_INSTRUCTIONS), interrupt);
    }

    // The whole buy-in button (PLAY + the FREE / ticket cost shown on it), not just "PLAY".
    private void AnnouncePlay(bool interrupt)
    {
        string? label = null;
        if (m_buyInRoot != null)
        {
            label = string.Join(", ", m_buyInRoot.GetComponentsInChildren<TMP_Text>(includeInactive: false)
                .Where(t => t.gameObject.activeInHierarchy && !string.IsNullOrWhiteSpace(t.text) && t.text.Any(char.IsLetterOrDigit))
                .Select(t => t.text.Trim()));
        }
        if (string.IsNullOrWhiteSpace(label)) label = m_play != null ? KeyboardNavigator.ResolveBaseLabel(m_play) : LocalizationUtils.Get(LocalizationKey.HUB_PLAY);
        Output(label!, interrupt);
    }

    private string? PageText()
    {
        if (m_pips.Length == 0) return null;
        var cur = Math.Max(0, Array.FindIndex(m_pips, t => t != null && t.isOn));
        return LocalizationUtils.Format(LocalizationKey.HUB_PAGE_FORMAT, cur + 1, m_pips.Length);
    }

    private string? TimerText()
    {
        var parts = new[] { HubUi.Text(m_timerTitle), HubUi.Text(m_timerValue) }.Where(x => x != null).ToList();
        return parts.Count > 0 ? string.Join(" ", parts) : null;
    }
}
