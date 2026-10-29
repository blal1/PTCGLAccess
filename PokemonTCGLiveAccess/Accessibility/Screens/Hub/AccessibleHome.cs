using System.Collections;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using MatchLogic;
using TMPro;
using TPCI.Coroutines;
using TPCI.UI.MenuCarousel;
using UnityEngine;
using UnityEngine.UI;
using static Accessibility.GameReflect;

namespace Accessibility;

// HOME screen — the "Play" screen. Driver: HUBPlayScreenController, a HUBScreenController like the Profile screen,
// so the lifecycle hook is OnActivate(HUBGroupController) / OnDeactivate(HUBGroupController).
//
// Layout, in visual top-to-bottom order (each section navigated left/right; Up/Down escapes between sections, Tab
// cycles them):
//   1. HUB top tab strip (HOME / DECKS / PROFILE / SHOP / BATTLE PASS / CARD DEX) + the overflow "⋮" button.
//   2. Game-mode sub-tabs (RANKED / CASUAL / LEARN / BUILD & BATTLE) — PageDragTab toggles under
//      gameModeController. Radio/tab semantics (Enter selects, never un-toggles).
//   3. Main content — PLAY button (HUBRankedController.playButton; interactable only with a valid active deck) plus
//      any other active buttons; the Learning Lab lessons under LEARN; the event buy-in under BUILD & BATTLE.
//   4. Bottom shortcuts (PROFILE / DECKS / CARD DEX) under gameModeController.BottomElements.
//
// Digits are left to the global HubTabShortcuts (see HubUi): nothing here reads number keys.
//
// FTUE (tutorial): when the tutorial highlights a button here, the screen collapses to that one button (all the
// game's input blocker lets a sighted player click), follows it when the highlight moves, and stays silent while a
// tutorial dialogue speaks — same model as AccessibleProfile, state from FtueTracker.
internal sealed class AccessibleHome : AccessibleScreen
{
    private static readonly AccessibleHome s_instance = new();

    private readonly List<INavSection> m_groups = new();
    private int m_activeGroup = -1;
    private HUBPlayScreenController? m_controller;

    // The LEARN game-mode tab's content is the Learning Lab lesson list — built as a section whenever it's showing,
    // so Down from the tabs / Enter on the active LEARN tab steps into it and Up returns to the tabs.
    // m_pendingEnterContent records an Enter-on-tab whose content is still loading (async) so we dive in once it
    // appears.
    private LearningLabSection? m_llSection;
    private bool m_pendingEnterContent;
    private string m_lastContentSig = string.Empty;
    private PageDragTab[]? m_modeTabs;

    private int m_tabGroupIndex = -1;
    private int m_mainGroupIndex = -1;

    private AccessibleHome()
    {
        // A drill-in / back inside the Learning Lab re-runs SetupDisplay; forward it to the live section.
        LearningLabSection.LevelChanged += OnLearningLabLevelChanged;
        FtueTracker.FocusedElementChanged += OnFtueFocusChanged;
    }

    // LEARN (the Learning Lab game-mode tab) is the selected mode. ChangeToLearningLabs sets this (GameplayType.Offline)
    // synchronously, but the lesson list's SetupDisplay — and so LearningLabSection.IsShowing — only follows a frame
    // or more later. The gap between "LEARN is selected" and "the lessons are showing" is the async-load window the
    // Enter/Down handling has to bridge so a quick keypress isn't lost.
    private static bool LearnModeSelected => HUBGameModeController.gameplayType == GameplayType.Offline;

    private HUBGameModeController? GameModes => m_controller != null ? ReadField<HUBGameModeController>(m_controller, "gameModeController") : null;

    #region Hooks

    [HarmonyPatch(typeof(HUBPlayScreenController), "OnActivate")]
    private static class ActivateHook
    {
        private static void Postfix(HUBPlayScreenController __instance)
        {
            if (s_instance.m_controller != __instance) s_instance.m_activeGroup = -1;   // fresh entry: default focus
            s_instance.m_controller = __instance;
            AccessibilityMgr.SetScreen(s_instance);
        }
    }

    [HarmonyPatch(typeof(HUBPlayScreenController), "OnDeactivate")]
    private static class DeactivateHook
    {
        private static void Postfix(HUBPlayScreenController __instance)
        {
            if (s_instance.m_controller != __instance) return;
            s_instance.CancelFtueSettlementWait();
            s_instance.m_controller = null;
            s_instance.m_activeGroup = -1;
            AccessibilityMgr.ClearScreen(s_instance);
        }
    }

    #endregion

    public string GetHelp()
    {
        var help = LocalizationUtils.Get(LocalizationKey.HUB_HOME_HELP);
        return LocalizationUtils.Format(LocalizationKey.HUB_HELP_WITH_GLOBAL_FORMAT, help, LocalizationUtils.Get(LocalizationKey.HUB_GLOBAL_KEYS_HELP));
    }

    // Shown, a popup above closed, or the window came back: rebuild (the screen may have changed meanwhile), keep the
    // player's place, and say "Home" then the focused item.
    public void OnGainedFocus()
    {
        CancelFtueSettlementWait();
        // FTUE-focused element wins: if the tutorial has highlighted a specific button on us, point our nav at just
        // that (mirrors AccessibleProfile).
        if (FtueTracker.FtueIsRunning && FtueTracker.FocusedElement is { } target && target.isActiveAndEnabled)
        {
            RebuildForFtue();
            return;
        }

        var prevIndex = m_activeGroup >= 0 && m_activeGroup < m_groups.Count ? m_groups[m_activeGroup].Index : -1;
        RebuildGroups();
        if (FtueTracker.FtueIsRunning)
        {
            // Land silently, then let the FTUE state machine settle: a speech-claiming state means the tutorial
            // dialogue owns speech; otherwise WaitForFtueToSettle announces.
            if (m_groups.Count == 0) return;
            if (m_activeGroup < 0 || m_activeGroup >= m_groups.Count) m_activeGroup = PickDefaultGroup();
            FocusDefaultGroup(announce: false, interruptPrior: true);
            m_ftueWait = AnywhereCoroutine.Instance.unmanaged.StartCoroutine(WaitForFtueToSettle());
            return;
        }
        AnnounceFocus(prevIndex);
    }

    private void AnnounceFocus(int prevIndex)
    {
        AccessibilityMgr.Output(this, LocalizationUtils.Get(LocalizationKey.HUB_HOME), interrupt: true);
        if (m_groups.Count == 0) return;
        if (m_activeGroup < 0 || m_activeGroup >= m_groups.Count)
        {
            m_activeGroup = PickDefaultGroup();
            FocusDefaultGroup(announce: true, interruptPrior: false);
            return;
        }
        if (m_groups[m_activeGroup] is KeyboardNavigator kn && prevIndex >= 0 && prevIndex < kn.Count)
            kn.FocusIndex(prevIndex, announce: true, interrupt: false);
        else
            m_groups[m_activeGroup].RefocusCurrent(announce: true, interrupt: false);
    }

    public void HandleInput()
    {
        if (m_controller == null)
        {
            AccessibilityMgr.ClearScreen(this);
            return;
        }

        // React to a game-mode tab switch / the Learning Lab loading or hiding. A mode switch settles
        // ASYNCHRONOUSLY: the content signature changes every frame while the old mode's panel animates out and the
        // new one animates in (PlayMode_In). So on every change we rebuild + keep focus SILENTLY, and only once the
        // content has stopped changing AND the animation has settled do we complete a pending Enter-on-tab dive —
        // never landing on a transient (the "casual speaks info" bug). Gated off during FTUE, where focus is
        // tutorial-driven (RebuildForFtue) and a rebuild would drop the coached button.
        if (!FtueTracker.FtueIsRunning)
        {
            var sig = ContentSig();
            if (sig != m_lastContentSig)
            {
                m_lastContentSig = sig;
                OnContentChanged();
                return;
            }
            if (m_pendingEnterContent && TryCompleteDive()) return;
        }

        if (m_groups.Count == 0) return;

        if (AccessibleKey.NEXT_SECTION.IsPressed() || AccessibleKey.PREV_SECTION.IsPressed())
        {
            m_pendingEnterContent = false;
            if (m_groups.Count > 1)
            {
                var dir = AccessibleKey.PREV_SECTION.IsPressed() ? -1 : 1;
                m_activeGroup = (m_activeGroup + dir + m_groups.Count) % m_groups.Count;
                m_groups[m_activeGroup].FocusFirst(announce: true, interruptPrior: true);
            }
            return;
        }

        // Section escape: groups are built in visual top-to-bottom order. Up at the top row of a group jumps to the
        // section above and lands on its bottom row (preserving column); Down at the bottom row jumps to the section
        // below and lands on its top row. Escaping *into* the game-mode tabs lands on the active mode.
        var nav = m_groups[m_activeGroup];
        var up = AccessibleKey.READ_PREV_MENU_OPTION.IsPressed();
        var down = AccessibleKey.READ_NEXT_MENU_OPTION.IsPressed();
        if (up && m_activeGroup > 0 && nav.IsAtTopRow)
        {
            m_pendingEnterContent = false;
            var col = nav.CurrentColumn;
            m_activeGroup--;
            if (m_activeGroup == m_tabGroupIndex) FocusActiveTab(announce: true);
            else m_groups[m_activeGroup].FocusLastRow(preferredColumn: col, announce: true, interruptPrior: true);
            return;
        }
        // Down from the game-mode tabs while LEARN is the selected mode drops into the Learning Lab lessons. They load
        // asynchronously, so if they aren't showing yet, arm the dive and let TryCompleteDive finish it when the list
        // appears — rather than escaping to the transient/empty section that briefly sits below the tabs mid-load.
        if (down && nav.IsAtBottomRow && m_activeGroup == m_tabGroupIndex && LearnModeSelected && !LearningLabSection.IsShowing)
        {
            m_pendingEnterContent = true;
            return;
        }
        if (down && m_activeGroup < m_groups.Count - 1 && nav.IsAtBottomRow)
        {
            m_pendingEnterContent = false;
            var col = nav.CurrentColumn;
            m_activeGroup++;
            m_groups[m_activeGroup].FocusFirstRow(preferredColumn: col, announce: true, interruptPrior: true);
            return;
        }

        // On the game-mode tabs, Enter dives into that tab's content section (rather than just re-speaking) — the
        // same step-into-the-section behaviour for every mode:
        //  - already on this mode with its content present and settled → step straight into the first item;
        //  - otherwise select the mode and ARM the dive — the content may load async, so TryCompleteDive finishes
        //    the dive once it appears.
        if (m_activeGroup == m_tabGroupIndex && AccessibleKey.CONFIRM.IsPressed() && nav is KeyboardNavigator tabs)
        {
            var wasOn = tabs.Current is Toggle { isOn: true };
            if (wasOn && ContentSectionReady() && TransitionSettled())
            {
                m_pendingEnterContent = false;
                m_activeGroup = m_mainGroupIndex;
                m_groups[m_activeGroup].FocusFirstRow(announce: true, interruptPrior: true);
                return;
            }
            tabs.Activate(isSpaceKey: false);   // select the mode (no-op if already selected)
            m_pendingEnterContent = true;
            return;
        }

        nav.HandleInput();
    }

    #region Group discovery

    private void RebuildGroups()
    {
        m_groups.Clear();
        m_tabGroupIndex = -1;
        m_mainGroupIndex = -1;
        m_llSection = null;
        if (m_controller == null)
        {
            m_lastContentSig = ContentSig();
            return;
        }

        var gmc = GameModes;
        m_modeTabs = gmc != null ? gmc.GetComponentsInChildren<PageDragTab>(includeInactive: false) : null;
        var known = new HashSet<Selectable>();

        // 1. HUB top tab strip + overflow "⋮".
        var top = HubUi.TopStrip();
        if (top.Count > 0)
        {
            known.UnionWith(top);
            AddGroup(top, resolver: UiLabels.TopNavLabel);
        }

        // 2. Game-mode sub-tabs. Each PageDragTab sits next to a real Toggle (PageDragTab.toggle); they share a
        // ToggleGroup, so radio/tab semantics apply.
        if (m_modeTabs != null)
        {
            var tabs = m_modeTabs.Select(t => t.toggle).Where(t => t != null && t.isActiveAndEnabled)
                .Cast<Selectable>().OrderBy(s => HubUi.ReadingOrder(s.transform)).ToList();
            known.UnionWith(tabs);
            if (tabs.Count > 0)
            {
                AddGroup(tabs, resolver: TabLabel, radioMode: true, groupName: LocalizationUtils.Get(LocalizationKey.HUB_GAME_MODE));
                m_tabGroupIndex = m_groups.Count - 1;
            }
        }

        // Bottom-shortcuts subtree (Profile / Decks / Card Dex) — its own section, excluded from the main sweep.
        // Hidden in Learn / Build & Battle.
        var bottom = gmc != null ? ReadField<GameObject>(gmc, "BottomElements") : null;
        var bottomT = bottom != null && bottom.activeInHierarchy ? bottom.transform : null;

        // 3. Main content.
        // Gate the Learning Lab on LearnModeSelected too, not just IsShowing: when switching AWAY from LEARN the
        // gameplayType flips synchronously but the lessons GameObject (→ IsShowing) stays active a frame or more, so
        // building the LL section purely on IsShowing would make it the main content of the mode we just left — and
        // the dive would re-read it ("spoke Learn Lab after pressing Ranked").
        BnbPlaySection? bnb = null;
        if (LearningLabSection.IsShowing && LearnModeSelected)
        {
            m_llSection = new LearningLabSection(this, EscapeToTabs);
            m_groups.Add(m_llSection);
            m_mainGroupIndex = m_groups.Count - 1;
        }
        else if (HUBGameModeController.gameplayType == GameplayType.BuildAndBattle && (bnb = new BnbPlaySection(this, EscapeToTabs)).Count > 0)
        {
            // Build & Battle content is an event buy-in carousel (instruction slides whose captions are info text, not
            // Selectables), so it gets its own section. Only used when its screen resolves; otherwise the generic sweep.
            m_groups.Add(bnb);
            m_mainGroupIndex = m_groups.Count - 1;
        }
        else
        {
            // Sweep the play screen and the game-mode controller (it may be a sibling rather than a child), plus
            // belt-and-suspenders lookups for the PLAY button, which is wired on HUBRankedController and parented
            // under HUBGameModeController.PlayButtonContainer.
            var candidates = new List<Selectable>(m_controller.GetComponentsInChildren<Selectable>(includeInactive: false));
            if (gmc != null)
            {
                candidates.AddRange(gmc.GetComponentsInChildren<Selectable>(includeInactive: false));
                var playContainer = ReadField<GameObject>(gmc, "PlayButtonContainer");
                if (playContainer != null && playContainer.activeInHierarchy)
                    candidates.AddRange(playContainer.GetComponentsInChildren<Selectable>(includeInactive: false));
            }
            var ranked = Object.FindAnyObjectByType<HUBRankedController>();
            if (ranked != null && ReadField<Button>(ranked, "playButton") is { } play) candidates.Add(play);

            var main = candidates.Where(s => s != null).Distinct()
                .Where(s => !known.Contains(s) && (s is Button || s is Toggle) && s.isActiveAndEnabled)
                .Where(s => bottomT == null || !s.transform.IsChildOf(bottomT))
                .OrderBy(s => HubUi.ReadingOrder(s.transform))
                .ToList();
            if (main.Count > 0)
            {
                known.UnionWith(main);
                if (HUBGameModeController.gameplayType == GameplayType.Casual)
                {
                    // Casual content is the format CAROUSEL — the Standard / Expanded / Custom hexes (each a real
                    // Button) + the info ⓘ. The hexes sit a little lower than the info button, so auto-detect strands
                    // it as a 1-column list and Left/Right can't move (the "info, 1 of 6, nothing else" bug). Drop the
                    // redundant carousel rotation arrows (the hexes are directly selectable), order the formats
                    // left-to-right with info last, and force a flat horizontal strip.
                    var casual = main.Where(s => !IsCarouselArrow(s))
                        .OrderBy(s => IsInfo(s) ? 1 : 0)
                        .ThenBy(s => s.transform.position.x)
                        .ToList();
                    AddGroup(casual, resolver: CasualLabel, flatHorizontal: true);
                }
                else AddGroup(main);
                m_mainGroupIndex = m_groups.Count - 1;
            }
        }

        // 4. Bottom shortcuts (Profile / Decks / Card Dex).
        if (bottomT != null)
        {
            var bottomItems = bottomT.GetComponentsInChildren<Selectable>(includeInactive: false)
                .Where(s => (s is Button || s is Toggle) && s.isActiveAndEnabled && !known.Contains(s))
                .OrderBy(s => HubUi.ReadingOrder(s.transform))
                .ToList();
            if (bottomItems.Count > 0) AddGroup(bottomItems);
        }

        m_lastContentSig = ContentSig();
    }

    private void AddGroup(List<Selectable> items, KeyboardNavigator.LabelResolver? resolver = null,
                          bool radioMode = false, string? groupName = null, bool flatHorizontal = false)
    {
        var g = new KeyboardNavigator(this) { RadioToggleMode = radioMode, GroupName = groupName };
        g.SetItems(items, resolver);
        // A row of controls that aren't perfectly Y-aligned (the casual format carousel) would auto-detect as a
        // 1-column list and strand Left/Right; force a flat horizontal strip so every item is reachable.
        if (flatHorizontal) g.ForceFlatHorizontal();
        // Escape on any flat HOME section opens the overflow (⋮) menu. The Learning Lab / B&B sections keep Escape
        // for going back to the tabs.
        g.OnBack = HubUi.OpenOverflowMenu;
        m_groups.Add(g);
        Log.Info($"Home group {m_groups.Count}: {items.Count} items — {string.Join(", ", items.Take(20).Select(s => s.gameObject.name))}");
    }

    // Default focus: the game-mode tab strip if present (tells the player their current mode), else the main content
    // (PLAY), else the first group.
    private int PickDefaultGroup()
    {
        if (m_tabGroupIndex >= 0 && m_tabGroupIndex < m_groups.Count) return m_tabGroupIndex;
        if (m_mainGroupIndex >= 0 && m_mainGroupIndex < m_groups.Count) return m_mainGroupIndex;
        return 0;
    }

    // For the game-mode tab strip the natural item is the ACTIVE mode, not the first tab — so entering HOME announces
    // "Casual, selected" (the mode you're actually in) rather than always "Ranked".
    private void FocusDefaultGroup(bool announce, bool interruptPrior)
    {
        if (m_activeGroup == m_tabGroupIndex) FocusActiveTab(announce, interruptPrior);
        else m_groups[m_activeGroup].FocusFirst(announce, interruptPrior);
    }

    #endregion

    #region Content changes and the dive into a mode's content

    // Signature of "what content should exist": the active game-mode tab, whether the Learning Lab list is showing,
    // AND a fingerprint of the play-area's interactive elements. Changes on a tab switch, the LL loading/hiding, OR
    // the game filling in async content — so comparing it each frame tells us when to rebuild.
    private string ContentSig()
    {
        var tab = "none";
        if (m_modeTabs != null)
        {
            foreach (var pdt in m_modeTabs)
            {
                if (pdt != null && pdt.toggle != null && pdt.toggle.isOn)
                {
                    tab = pdt.gameObject.name;
                    break;
                }
            }
        }
        return tab + "|" + (LearningLabSection.IsShowing ? "LL" : "-") + "|" + MainContentFingerprint();
    }

    // Fingerprint (count + order-independent name hash) of the play-area's active interactive elements. The game
    // populates this screen ASYNCHRONOUSLY after its data loads: on a cold start the rank hex can appear a few frames
    // AFTER the first build, so a one-shot discovery finds only PLAY — the reported "first load misses the rank, but
    // deck→home reads it fine". Folding this into ContentSig makes the per-frame check notice the new content and
    // rebuild. The format-hex carousel isn't a Selectable, so it doesn't churn this.
    private string MainContentFingerprint()
    {
        if (m_controller == null) return "";
        int count = 0, hash = 0;
        void Acc(Component? root)
        {
            if (root == null) return;
            foreach (var s in root.GetComponentsInChildren<Selectable>(includeInactive: false))
            {
                if ((s is Button || s is Toggle) && s.isActiveAndEnabled)
                {
                    count++;
                    hash ^= s.gameObject.name.GetHashCode();
                }
            }
        }
        Acc(m_controller);
        Acc(GameModes);
        return count + ":" + hash;
    }

    // The selected mode's content section exists AND matches the selected mode — not a transient mid-transition
    // state. LEARN's real content is the lessons list (IsShowing == true); every other mode requires the lessons to
    // have fully torn down first (IsShowing == false). Because IsShowing lags the synchronous gameplayType flip,
    // gating non-LEARN dives on !IsShowing is what stops us diving into the still-present Learn Lab when switching
    // away from it (the stale-speech bug).
    private bool ContentSectionReady()
    {
        if (m_mainGroupIndex < 0 || m_mainGroupIndex >= m_groups.Count) return false;
        return LearnModeSelected ? LearningLabSection.IsShowing : !LearningLabSection.IsShowing;
    }

    // The content signature changed — one frame of an async mode switch, a tab swap, or the Learning Lab
    // loading/hiding. Rebuild so the section list matches the live UI, but keep the player where they are SILENTLY.
    // We deliberately do NOT dive here: a transition emits many intermediate signatures, and diving on any of them
    // lands on a half-built state (the casual carousel with only its info button realized, or the old mode's
    // leftover content). m_pendingEnterContent survives these intermediate rebuilds.
    private void OnContentChanged()
    {
        var prev = m_activeGroup;
        RebuildGroups();
        if (m_groups.Count == 0)
        {
            m_activeGroup = -1;
            return;
        }
        m_activeGroup = Mathf.Clamp(prev < 0 ? PickDefaultGroup() : prev, 0, m_groups.Count - 1);
        if (m_activeGroup == m_tabGroupIndex) FocusActiveTab(announce: false);
        else m_groups[m_activeGroup].RefocusCurrent(announce: false);
    }

    // Finish a pending dive — but only once BOTH native signals say the new mode is fully in: its content section is
    // present and matches the mode (ContentSectionReady) AND the mode-switch animation has settled
    // (TransitionSettled). Called each frame while the content signature is stable.
    private bool TryCompleteDive()
    {
        if (!ContentSectionReady() || !TransitionSettled()) return false;
        m_pendingEnterContent = false;
        m_activeGroup = m_mainGroupIndex;
        m_groups[m_activeGroup].FocusFirstRow(announce: true, interruptPrior: true);
        return true;
    }

    // The mode-switch animation has finished. Each gameplay type has its own animator; switching plays the
    // "PlayMode_In" state on the newly-current one, and the game itself waits on exactly this (DelayLearningLabUpdate)
    // before finishing its own setup. So the content isn't stable until the current animator leaves PlayMode_In (and
    // any blend into it).
    private bool TransitionSettled()
    {
        var gmc = GameModes;
        var animator = gmc != null ? ReadField<Animator>(gmc, "currentGameplayTypeAnimator") : null;
        if (animator == null || !animator.isActiveAndEnabled) return true;
        return !animator.GetCurrentAnimatorStateInfo(0).IsName("PlayMode_In") && !animator.IsInTransition(0);
    }

    // Focus the currently-selected game-mode tab (the toggle that's on).
    private void FocusActiveTab(bool announce, bool interruptPrior = true)
    {
        if (m_tabGroupIndex < 0 || m_tabGroupIndex >= m_groups.Count || m_groups[m_tabGroupIndex] is not KeyboardNavigator kn) return;
        for (var i = 0; i < kn.Items.Count; i++)
        {
            if (kn.Items[i] is Toggle { isOn: true })
            {
                kn.FocusIndex(i, announce, interruptPrior);
                return;
            }
        }
        kn.FocusFirst(announce, interruptPrior);
    }

    // The Learning Lab (at its top level) and Build & Battle sections hand Escape back up to the game-mode tabs.
    private void EscapeToTabs()
    {
        if (m_tabGroupIndex < 0 || m_tabGroupIndex >= m_groups.Count) return;
        m_activeGroup = m_tabGroupIndex;
        FocusActiveTab(announce: true);
    }

    // A drill-in / back inside the lessons re-ran SetupDisplay — if the lessons are the active section, read the
    // new level.
    private void OnLearningLabLevelChanged()
    {
        if (m_llSection != null && m_activeGroup >= 0 && m_activeGroup < m_groups.Count && ReferenceEquals(m_groups[m_activeGroup], m_llSection))
            m_llSection.OnLevelChanged();
    }

    #endregion

    #region FTUE

    private Coroutine? m_ftueWait;
    private Selectable? m_lastFtueTarget;

    private void CancelFtueSettlementWait()
    {
        if (m_ftueWait == null) return;
        AnywhereCoroutine.Instance.unmanaged.StopCoroutine(m_ftueWait);
        m_ftueWait = null;
    }

    // Wait for the FTUE state machine to commit after we gained focus (see AccessibleProfile.WaitForFtueToSettle):
    // a speech-claiming state takes over (stay silent, the tutorial owns speech) or the animator settles with nothing
    // claiming (announce normally). No frame counting — yield until a native condition resolves.
    private IEnumerator WaitForFtueToSettle()
    {
        var mgr = FTUEManager.instance;
        var animator = mgr != null ? mgr.GetComponent<Animator>() : null;
        while (FtueTracker.FtueIsRunning && animator != null && !animator.enabled) yield return null;
        yield return null;
        m_ftueWait = null;
        if (FtueTracker.FtueSpeechClaimingStateActive)
        {
            Log.Info("Home: FTUE speech-claim active, staying silent");
            yield break;
        }
        if (m_controller != null && AccessibilityMgr.IsFocused(this)) AnnounceFocus(-1);
    }

    // FTUE has highlighted a specific button on this screen: collapse to a single-item nav of just that Selectable,
    // matching what the tutorial's input blocker allows clicking. When the highlight is cleared, go back to the
    // normal sections, silently (the tutorial is talking).
    private void RebuildForFtue()
    {
        CancelFtueSettlementWait();
        var target = FtueTracker.FocusedElement;
        m_lastFtueTarget = target;
        if (target == null || !target.isActiveAndEnabled)
        {
            OnContentChanged();
            return;
        }
        m_groups.Clear();
        m_tabGroupIndex = -1;
        m_mainGroupIndex = -1;
        m_llSection = null;
        m_pendingEnterContent = false;
        // Keep the content signature current (the game-mode tabs persist) so leaving FTUE doesn't trigger a spurious
        // content-change rebuild on the first normal frame.
        var gmc = GameModes;
        m_modeTabs = gmc != null ? gmc.GetComponentsInChildren<PageDragTab>(includeInactive: false) : null;
        m_lastContentSig = ContentSig();
        // A coached Toggle is a tab, so Enter must SELECT it — see FtueTracker.
        AddGroup(new List<Selectable> { target }, radioMode: target is Toggle);
        m_activeGroup = 0;
        m_groups[0].FocusFirst(announce: true, interruptPrior: true);
    }

    private static void OnFtueFocusChanged()
    {
        var self = s_instance;
        // Only react while we're the focused screen; if a UI covers us, our next OnGainedFocus picks it up.
        if (self.m_controller == null || AccessibilityMgr.CurrentScreen != self || !AccessibilityMgr.IsFocused(self)) return;
        if (FtueTracker.FocusedElement == self.m_lastFtueTarget) return;
        if (FtueTracker.FtueIsRunning) self.RebuildForFtue();
    }

    #endregion

    #region Labels

    private static bool IsInfo(Selectable s) => s.gameObject.name.IndexOf("Info", System.StringComparison.OrdinalIgnoreCase) >= 0;

    // The carousel rotation arrows (ArrowButtonLeft / ArrowButtonRight).
    private static bool IsCarouselArrow(Selectable s) => s.gameObject.name.IndexOf("Arrow", System.StringComparison.OrdinalIgnoreCase) >= 0;

    // Casual format carousel: the info ⓘ reads as the casual mode information; each format hex (Standard / Expanded /
    // Custom) reads its name + "selected" when it's the active format.
    private static string CasualLabel(Selectable s)
    {
        if (IsInfo(s)) return LocalizationUtils.Get(LocalizationKey.HUB_CASUAL_INFO);
        var label = KeyboardNavigator.ResolveBaseLabel(s);
        var elem = s.GetComponentInParent<MenuCarouselElementUi>();
        return elem != null && elem.GameMode == HUBGameModeController.gameMode ? Announce.WithState(label, LocalizationKey.GLOBAL_SELECTED) : label;
    }

    // Tab label: the game's tab name + selected state, plus "unavailable" for a feature-gated tab (e.g. Build & Battle
    // with no active event). A selected tab is disabled by the game so it can't be re-picked — that's "selected", not
    // "unavailable".
    private static string TabLabel(Selectable s)
    {
        var tab = s.GetComponent<PageDragTab>();
        var label = LocalizationUtils.TryGetGame(tab != null ? tab.GetLocTextID() : null);
        if (string.IsNullOrEmpty(label))
        {
            var tmp = s.GetComponentInChildren<TMP_Text>(includeInactive: false);
            label = tmp != null && !string.IsNullOrEmpty(tmp.text) ? tmp.text : s.gameObject.name;
        }
        if (s is Toggle { isOn: true }) return Announce.WithState(label!, LocalizationKey.GLOBAL_SELECTED);
        var notSelected = Announce.WithState(label!, LocalizationKey.HUB_NOT_SELECTED);
        return s.interactable ? notSelected : Announce.WithState(notSelected, LocalizationKey.GLOBAL_DISABLED);
    }

    #endregion
}
