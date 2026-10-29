using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using MatchLogic;
using RainierClientSDK;
using TPCI.Rainier.Match.Cards;
using TPCI.Rainier.Match.Cards.Ownership;
using UnityEngine;
using UnityEngine.InputSystem;
using static Accessibility.GameReflect;

namespace Accessibility;

// The in-battle screen. Serves BOTH FTUE tutorial battles and normal/online matches (they share the same
// MatchManager). Lifecycle hooks: StartGame (SetScreen), SetState (phase/turn announcements),
// LoadEndBattleScreen (ClearScreen).
//
// Board cards aren't uGUI Selectables, so we synthesize navigation: HS-style review keys jump
// focus into a zone, Left/Right walk cards, Down/Up read the focused card line-by-line. Enter
// opens a menu of ONLY the legal actions for the focused card, invoked through the game's own
// MatchUserOperations (the same op the mouse path sends). E ends the turn.
//
// Reading never mutates the game — only Enter (→ legal-action menu) and E change anything.
internal sealed class AccessibleBattleScreen : AccessibleScreen
{
    private static readonly AccessibleBattleScreen s_instance = new();

    private MatchManager? m_manager;
    private MatchManager.MatchState m_phase = MatchManager.MatchState.None;
    // StartGame happened and the opening line ("Battle started. Your turn") hasn't been spoken yet. The screen
    // may start under a UI (a tutorial dialogue): AccessibilityMgr then gives it focus when that UI closes.
    private bool m_pendingOpening;

    private enum Mode { Nav, ActionMenu, CardView, ConfirmPlay }
    private Mode m_mode = Mode.Nav;

    // Card-view reading state (locked on one card, reading its printed lines).
    private Card3D? m_cvCard;
    private List<string>? m_cvLines;
    private int m_cvLine = -1;

    // Nav state
    private BattleBoardReader.Zone m_zone = BattleBoardReader.Zone.MyActive;
    private List<Card3D>? m_zoneCards;   // null for count-only zones
    private int m_card;
    private int m_line = -1;             // -1 = on the card name; 0.. = detail line index

    // Action-menu state
    private sealed class BattleAction
    {
        public string Label = "";
        // LIVE, not a snapshot. The game re-evaluates option interactivity asynchronously — the FTUE
        // arms its wanted attack only once the overlay is open and settled (FTUEActionIndicator waits
        // on `!overlay.isOpen || overlay.isAnimating`, THEN calls Card3D.FocusAttack), and
        // RefreshActionUsability defers its repopulate by framesToWaitOnRefresh. So the interactable
        // flag we'd read while building this menu is always the pre-arm one.
        public Func<bool> CanUse = () => true;
        public Action Invoke = () => { };
    }
    // Usability of the option we last spoke, so the game's repopulate can re-announce only on a change.
    private bool m_spokenActionUsable;
    private List<BattleAction> m_actions = new();
    private int m_actionIdx;

    // Native hand-card placement state ("hold and drop" — drives the game's CardTargetingHandler).
    private readonly KbPlacementInitiator m_placeInit = new();
    private bool m_holding;
    private Card3D? m_heldCard;

    // A no-target card awaiting the player's "Use X?" confirmation (Enter plays, Backspace cancels).
    private Card3D? m_confirmCard;

    // True while a LOCAL selection request is open (prize / discard / search / choose-active / …),
    // tracked off the game's own SelectionStateChanged event. We block turn-ending and new actions
    // while it's set so we can't desync the match — even for selection families we don't drive yet.
    private bool m_localSelecting;
    private MatchManager? m_selSubManager;   // manager we've hooked SelectionStateChanged on
    private HandController? m_handSub;        // local hand we've hooked OnCardAdded/OnCardRemoved on

    // An active attach-distribution selection (e.g. Oricorio ex Excited Turbo: "attach Basic Fire Energy
    // to your Benched Fire Pokémon"). It's a placement, not a list — so instead of a mod overlay the
    // player attaches with the NORMAL board drag: pick up a hand energy (Enter in your hand), navigate to
    // a benched Pokémon (B + arrows), Enter to drop; Tab = Done. Set by AccessibleBattlePrompt, which gives
    // the attach selection NO overlay. Everything below is gated on this being non-null, so normal play
    // and all other prompts are byte-for-byte unchanged.
    private AttachmentDistributionSelectionMenu? m_attachSel;
    // Reparent (move-energy) first-press pickup waiting for the just-distributed energy to surface in the
    // temp hand. Non-null = retrying the pickup from HandleInput; see TickReparentPickup. Almost always
    // resolves on frame 1 (the game's own distribute refreshes targetability), so this is belt-and-suspenders.
    private Card3D? m_reparentPickupPending;
    private int m_reparentPickupTicks;

    // Tutorial guidance: the entityID of the FTUE-spotlighted card we last announced.
    private string m_ftueCardId = "";

    // True once we've announced the screen-space message bar's "Done" button (setup confirm etc.).
    private bool m_doneBarShown;
    // True between Tab (move focus to Done) and the confirming Enter — see the Done handling in HandleNav.
    private bool m_doneFocused;

    #region Hooks

    [HarmonyPatch(typeof(MatchManager), "StartGame")]
    private static class StartGameHook
    {
        private static void Postfix(MatchManager __instance) => s_instance.OnStartGame(__instance);
    }

    [HarmonyPatch(typeof(MatchManager), "SetState")]
    private static class SetStateHook
    {
        private static void Postfix(MatchManager __instance, MatchManager.MatchState newState) => s_instance.OnSetState(__instance, newState);
    }

    [HarmonyPatch(typeof(MatchManager), "LoadEndBattleScreen")]
    private static class LoadEndBattleScreenHook
    {
        private static void Postfix(MatchManager __instance) => s_instance.OnEndBattle(__instance);
    }

    // The overlay rebuilds its option widgets — and with them each button's interactable flag —
    // whenever the game re-evaluates action usability. RefreshActionUsability only SCHEDULES that
    // (AnywhereCoroutine.YieldForFrames(framesToWaitOnRefresh)), so PopulateOptions is the moment
    // the answer actually changes. The tutorial's attack step arms its wanted attack this way, well
    // after we built the menu, so this is how an "unavailable" option becomes usable under the
    // player's cursor without them having to close and reopen the menu.
    [HarmonyPatch(typeof(AttackOverlay), "PopulateOptions")]
    private static class PopulateOptionsHook
    {
        private static void Postfix(AttackOverlay __instance) => s_instance.OnPopulateOptions(__instance);
    }

    // Coin flips (starting flip, attack effects) only show their result visually — speak it. FlipCoin
    // returns the per-coin result (isHeads) and fires once per coin in a multi-flip. Queued so a
    // multi-flip reads "Heads. Tails. …" in order without cutting off, and so it never clips the
    // prompt/turn speech around it.
    [HarmonyPatch(typeof(CoinFlipOverlay), "FlipCoin")]
    private static class FlipCoinHook
    {
        private static void Postfix(bool __result) =>
            AccessibilityMgr.OutputNotification(LocalizationUtils.Get(__result ? LocalizationKey.BATTLE_COIN_HEADS : LocalizationKey.BATTLE_COIN_TAILS));
    }

    private void OnStartGame(MatchManager mgr)
    {
        m_manager = mgr;
        m_phase = mgr.currentState;
        Log.Info($"Battle start: ftueGoverns={FtueTracker.FtueGovernsMatch(mgr)}");
        HookSelectionState(mgr);
        HookHandChanges(mgr);
        m_mode = Mode.Nav;
        m_holding = false;
        m_confirmCard = null;
        m_ftueCardId = "";
        m_pendingOpening = true;
        // A tutorial Dialogue bubble can already be showing as the battle starts (the final FTUE battle does
        // this). It must stay the focused, only-interactable thing so the player can advance it: SetScreen
        // leaves the screen silent underneath any UI and gives it focus when that UI closes.
        AccessibilityMgr.SetScreen(this);
    }

    private void OnSetState(MatchManager mgr, MatchManager.MatchState newState)
    {
        if (m_manager != mgr || newState == m_phase) return;
        m_phase = newState;
        // Queued so it appends after any in-flight speech rather than cutting it off.
        if (PhaseText(newState) is { } text) AccessibilityMgr.OutputNotification(text);
    }

    private void OnEndBattle(MatchManager mgr)
    {
        if (m_manager != mgr) return;
        AccessibilityMgr.OutputNotification(LocalizationUtils.Get(LocalizationKey.BATTLE_ENDED));
        EndBattle();
    }

    private void EndBattle()
    {
        UnhookHandChanges();
        m_manager = null;
        m_attachSel = null;
        m_holding = false;
        m_heldCard = null;
        AccessibilityMgr.ClearScreen(this);
    }

    // The game just recomputed which options are clickable. If the player is sitting in our action menu
    // on that card, the option they're focused on may have flipped — re-announce it, but only on a real
    // change (PopulateOptions also runs on every open, and on unrelated refreshes).
    private void OnPopulateOptions(AttackOverlay overlay)
    {
        if (m_mode != Mode.ActionMenu || m_manager == null || !ReferenceEquals(overlay, m_manager.attackOverlay)) return;
        if (m_actionIdx < 0 || m_actionIdx >= m_actions.Count) return;
        if (m_actions[m_actionIdx].CanUse() == m_spokenActionUsable) return;
        SayAction(m_actionIdx);
    }

    // Subscribe (once per match) to the game's selection-state signal. InputRequestOperation raises
    // this (LOCAL, true) before opening ANY local selection overlay and (LOCAL, false) when it
    // resolves, so it's a reliable "a choice is pending" flag across every selection family.
    private void HookSelectionState(MatchManager mgr)
    {
        if (m_selSubManager == mgr) return;
        m_selSubManager = mgr;
        m_localSelecting = false;
        try { mgr.AddSelectionStateChangedCallback(OnSelectionStateChanged); }
        catch (Exception ex) { Log.Error($"Battle: SelectionStateChanged subscribe threw: {ex.Message}"); }
    }

    private void OnSelectionStateChanged(PlayerID who, bool selecting)
    {
        if (who == PlayerID.LOCAL) m_localSelecting = selecting;
    }

    // Subscribe to the LOCAL hand's own add/remove events (HandController.OnCardAdded/OnCardRemoved,
    // fired from ProcessCardGainedResult / ProcessCardRemovalResult). This is the game's native
    // "your hand changed" signal — it lets us (1) announce draws the battle log doesn't cover (your
    // per-turn / effect draws are private, so they never appear there), and (2) keep the focused hand
    // list live so a drawn card shows up and a played/attached card stops being navigable while you're
    // still standing in the hand (re-entering the zone already re-reads; this covers staying put).
    private void HookHandChanges(MatchManager mgr)
    {
        HandController? hand = null;
        try { hand = mgr.board?.GetPlayer(PlayerID.LOCAL)?.hand; } catch (Exception ex) { Log.Warn($"Battle: hand lookup threw: {ex.Message}"); }
        if (ReferenceEquals(hand, m_handSub)) return;
        UnhookHandChanges();
        m_handSub = hand;
        if (m_handSub != null)
        {
            try { m_handSub.OnCardAdded += OnHandCardAdded; m_handSub.OnCardRemoved += OnHandCardRemoved; }
            catch (Exception ex) { Log.Error($"Battle: hand subscribe threw: {ex.Message}"); }
        }
    }

    private void UnhookHandChanges()
    {
        if (m_handSub == null) return;
        try { m_handSub.OnCardAdded -= OnHandCardAdded; m_handSub.OnCardRemoved -= OnHandCardRemoved; } catch { }
        m_handSub = null;
    }

    // Runs inside the game's own event: guarded so a speech bug never unwinds into the match.
    private void OnHandCardAdded(Card3D card)
    {
        try
        {
            if (card != null)
            {
                // Your own hand identity is always knowable — read the name directly rather than via
                // CardName (which would say "face-down card" if IsPublic hasn't flipped yet mid-draw).
                var nm = card.cardDataRow?.LocalizedCardName;
                if (string.IsNullOrEmpty(nm)) nm = card.cardInfo?.cardName;
                AccessibilityMgr.OutputNotification(string.IsNullOrEmpty(nm)
                    ? LocalizationUtils.Get(LocalizationKey.BATTLE_DREW_CARD)
                    : LocalizationUtils.Format(LocalizationKey.BATTLE_DREW_NAMED, nm));
            }
            if (m_manager != null && m_zone == BattleBoardReader.Zone.MyHand) RefreshZoneCards();   // appended card now navigable
        }
        catch (Exception ex) { Log.Error($"Battle.OnHandCardAdded threw: {ex.Message}"); }
    }

    private void OnHandCardRemoved(Card3D card)
    {
        // A card left the hand (played / attached / discarded). If we're standing in the hand, drop it
        // from the live list so it can't be navigated to or targeted as a phantom ("Can't play X now").
        try { if (m_manager != null && m_zone == BattleBoardReader.Zone.MyHand) RefreshZoneCards(); }
        catch (Exception ex) { Log.Error($"Battle.OnHandCardRemoved threw: {ex.Message}"); }
    }

    #endregion

    #region AccessibleComponent

    public string GetHelp() => LocalizationUtils.Get(m_mode switch
    {
        Mode.ActionMenu => LocalizationKey.BATTLE_HELP_ACTION_MENU,
        Mode.CardView => LocalizationKey.BATTLE_HELP_CARD_VIEW,
        Mode.ConfirmPlay => LocalizationKey.BATTLE_HELP_CONFIRM_PLAY,
        _ => m_holding ? LocalizationKey.BATTLE_HELP_HOLDING : LocalizationKey.BATTLE_HELP,
    });

    public void OnGainedFocus()
    {
        if (m_pendingOpening)
        {
            m_pendingOpening = false;
            var phase = PhaseText(m_phase);
            AccessibilityMgr.Output(this, phase == null
                ? LocalizationUtils.Get(LocalizationKey.BATTLE_STARTED)
                : LocalizationUtils.Format(LocalizationKey.BATTLE_STARTED_PHASE, phase), interrupt: false);
            return;
        }
        // A UI above us closed (a decision prompt resolved, the battle menu closed) or the window came back.
        // Don't re-announce the phase (prompts open and close after every tutorial coaching line — the genuine
        // turn announcement comes only from SetState). But DO two things the closed UI may have changed:
        //  • Re-arm tutorial guidance so the next frame re-announces what the FTUE now wants (a forced
        //    card step often becomes active right after a search/discard resolves).
        //  • Re-read the focused zone — playing a card / resolving a search mutates the board, so the
        //    cached card list is stale (this is the "left/right shows old hand cards" report).
        // Then say where the player is, queued, so the battle log narration is never cut.
        m_ftueCardId = "";
        RefreshZoneCards();
        AccessibilityMgr.Output(this, WhereAmI(), interrupt: false);
    }

    // "Your hand. Pikachu, 2 of 5" / "Your deck": the focused zone and card, without changing anything.
    private string WhereAmI()
    {
        var zoneName = BattleBoardReader.ZoneName(m_zone);
        if (m_zoneCards == null || m_card < 0 || m_card >= m_zoneCards.Count) return zoneName;
        return LocalizationUtils.Format(LocalizationKey.BATTLE_ZONE_AND_CARD, zoneName, CardLabel(m_card));
    }

    public void HandleInput()
    {
        // MatchManager is a MonoBehaviour: Unity-null once the match scene is gone.
        if (m_manager == null)
        {
            EndBattle();
            return;
        }
        // The attach-distribution selection resolved/closed → leave attach mode.
        if (m_attachSel != null && !m_attachSel.isOpen) EndAttach();
        // A move-energy pickup is waiting on the just-lifted energy to become targetable — finish it.
        if (m_reparentPickupPending != null) { TickReparentPickup(); return; }

        if (m_mode == Mode.CardView) { HandleCardView(); return; }
        if (m_mode == Mode.ActionMenu) { HandleActionMenu(); return; }
        if (m_mode == Mode.ConfirmPlay) { HandleConfirmPlay(); return; }   // "Use X?" — Enter plays, Backspace cancels
        if (!m_holding) TutorialGuidanceTick();   // keep the player on the tutorial's spotlighted card
        HandleNav();
        if (m_holding) RefreshHeldChosen();   // keep the placement loop aimed at the current focus
    }

    private void Say(string text, bool interrupt = true) => AccessibilityMgr.Output(this, text, interrupt);

    private void Say(LocalizationKey key, bool interrupt = true) => Say(LocalizationUtils.Get(key), interrupt);

    private void Say(LocalizationKey key, params object?[] args) => Say(LocalizationUtils.Format(key, args));

    #endregion

    #region Navigation mode

    private void HandleNav()
    {
        bool back = AccessibleKey.BACK.IsPressed() || AccessibleKey.ESCAPE.IsPressed();

        // Backspace/Escape closes the game's card viewer (big-card, stack, or pile inspection) when open.
        // This is the backstop for a viewer opened OUTSIDE card-view mode (e.g. by the FTUE); a viewer the
        // mod opened is handled in HandleCardView. Must run before the Escape→battle-menu fall-through below.
        if (back && InspectionOpen()) { CloseInspection(); return; }

        // While holding a picked-up card, Backspace cancels the drag and returns it to hand.
        if (m_holding && AccessibleKey.BACK.IsPressed()) { CancelPickup(); return; }

        // Escape opens the battle menu (settings + Concede) — the panel the gear icon shows. We only
        // reach here with the field clear: selection prompts push their own UI over us, and the
        // big-card viewer was handled just above. Not while holding a card (Backspace cancels that).
        if (AccessibleKey.ESCAPE.IsPressed() && !m_holding && AccessibleBattleSettings.TryOpen()) return;

        // A "Done" / confirm is available when the screen-space message bar shows one (setup confirm,
        // e.g. "Choose your starting Pokémon") or an attach-distribution is in progress (finish
        // attaching). Tab MOVES FOCUS to Done and announces it — it does NOT auto-confirm; Enter then
        // confirms, the same focus-then-Enter model as the selection prompts. Any other board key
        // releases that focus so Enter goes back to picking up / placing cards.
        bool doneAvailable = MessageBarDoneAvailable();
        if (doneAvailable) { if (!m_doneBarShown) { m_doneBarShown = true; AnnounceDoneBar(); } }
        else m_doneBarShown = false;
        // Focusable even when the game has it greyed out — the desktop convention this mod follows is
        // "you can tab to a dimmed control, you just can't activate it". Say which it is.
        if (doneAvailable && AccessibleKey.NEXT_SECTION.IsPressed())
        {
            m_doneFocused = true;
            Say(MessageBarDoneUsable() ? LocalizationKey.BATTLE_DONE : LocalizationKey.BATTLE_DONE_UNAVAILABLE);
            return;
        }
        if (doneAvailable && m_doneFocused && AccessibleKey.CONFIRM.IsPressed()) { m_doneFocused = false; ConfirmDone(); return; }
        if (!doneAvailable) m_doneFocused = false;
        else if (m_doneFocused && Keyboard.current?.anyKey.wasPressedThisFrame == true && !AccessibleKey.NEXT_SECTION.IsPressed()) m_doneFocused = false;

        // Zone jump keys (HS "look at" style). Shift = opponent's equivalent where it exists.
        if (AccessibleKey.BATTLE_MY_ACTIVE.IsPressed()) { FocusZone(BattleBoardReader.Zone.MyActive); return; }
        if (AccessibleKey.BATTLE_OPP_ACTIVE.IsPressed()) { FocusZone(BattleBoardReader.Zone.OppActive); return; }
        if (AccessibleKey.BATTLE_MY_BENCH.IsPressed()) { FocusZone(BattleBoardReader.Zone.MyBench); return; }
        if (AccessibleKey.BATTLE_OPP_BENCH.IsPressed()) { FocusZone(BattleBoardReader.Zone.OppBench); return; }
        if (AccessibleKey.BATTLE_MY_HAND.IsPressed()) { FocusZone(BattleBoardReader.Zone.MyHand); return; }
        if (AccessibleKey.BATTLE_OPP_HAND.IsPressed()) { FocusZone(BattleBoardReader.Zone.OppHand); return; }
        if (AccessibleKey.BATTLE_MY_DISCARD.IsPressed()) { FocusZone(BattleBoardReader.Zone.MyDiscard); return; }
        if (AccessibleKey.BATTLE_OPP_DISCARD.IsPressed()) { FocusZone(BattleBoardReader.Zone.OppDiscard); return; }
        if (AccessibleKey.BATTLE_MY_DECK.IsPressed()) { FocusZone(BattleBoardReader.Zone.MyDeck); return; }
        if (AccessibleKey.BATTLE_OPP_DECK.IsPressed()) { FocusZone(BattleBoardReader.Zone.OppDeck); return; }
        if (AccessibleKey.BATTLE_MY_PRIZES.IsPressed()) { FocusZone(BattleBoardReader.Zone.MyPrize); return; }
        if (AccessibleKey.BATTLE_OPP_PRIZES.IsPressed()) { FocusZone(BattleBoardReader.Zone.OppPrize); return; }
        if (AccessibleKey.BATTLE_STADIUM.IsPressed()) { FocusZone(BattleBoardReader.Zone.Stadium); return; }
        if (AccessibleKey.BATTLE_STATUS.IsPressed()) { AnnounceStatus(); return; }
        if (AccessibleKey.BATTLE_INSPECT.IsPressed()) { EnterCardView(); return; }
        if (AccessibleKey.BATTLE_END_TURN.IsPressed()) { EndTurn(); return; }

        // Within-zone navigation
        if (AccessibleKey.READ_NEXT_ITEM.IsPressed()) { MoveCard(1); return; }
        if (AccessibleKey.READ_PREV_ITEM.IsPressed()) { MoveCard(-1); return; }
        if (AccessibleKey.READ_NEXT_LINE.IsPressed()) { ReadLine(1, shift: false); return; }
        if (AccessibleKey.READ_PREV_LINE.IsPressed()) { ReadLine(-1, shift: false); return; }
        if (AccessibleKey.READ_TO_END.IsPressed()) { ReadLine(1, shift: true); return; }
        if (AccessibleKey.READ_CUR_LINE.IsPressed()) { ReadLine(-1, shift: true); return; }
        if (AccessibleKey.READ_FIRST_ITEM.IsPressed()) { JumpCard(0); return; }
        if (AccessibleKey.READ_LAST_ITEM.IsPressed()) { JumpCard(int.MaxValue); return; }
        if (AccessibleKey.CONFIRM.IsPressed()) { if (m_holding) AttemptDrop(); else OpenActionMenu(); return; }

        // 1-9, 0 → jump to card N in the current zone
        if (AccessibleKey.TryGetPressedNumber() is { } n) JumpCard(n - 1);
    }

    private void FocusZone(BattleBoardReader.Zone z)
    {
        if (m_manager == null) return;
        // A move-energy ability (Solar Transfer) never touches your hand — the energy is already on your
        // Pokémon. So C is a silent no-op here: don't switch zones, don't speak, just stay on the board.
        // (Attach-from-a-pile really does use the temp hand on C — that path is below.)
        if (m_attachSel is ReparentCardMenu && z == BattleBoardReader.Zone.MyHand) return;
        m_zone = z; m_card = 0; m_line = -1;
        string zoneName = BattleBoardReader.ZoneName(z);

        // During an attach-distribution, C (your hand) shows the menu's attach energies — the game lifts
        // them into a temporary hand, NOT your real hand — so you handle the energy then press B to drop
        // it on a Pokémon. Attach-from-a-pile (Oricorio ex, Teal Dance): C shows the temp hand the game
        // lifts the pile energy into, so you pick one and drop it on a Pokémon. (Move-energy never reaches
        // here — handled above — because for a move the energy is on the board, not in any hand.)
        if (m_attachSel != null && z == BattleBoardReader.Zone.MyHand)
        {
            m_zoneCards = AttachEnergyCards();
            if (m_zoneCards.Count == 0) { Say(LocalizationKey.BATTLE_NO_ENERGY_TO_ATTACH); return; }
            Say(LocalizationKey.BATTLE_ATTACH_ENERGY_ZONE, AttachEnergyCount(), CardLabel(0));
            return;
        }

        if (BattleBoardReader.IsCountOnly(z))
        {
            m_zoneCards = null;
            int n = BattleBoardReader.GetCount(z);
            Say(n == 1 ? LocalizationUtils.Format(LocalizationKey.BATTLE_ZONE_ONE_CARD, zoneName) : LocalizationUtils.Format(LocalizationKey.BATTLE_ZONE_CARDS, zoneName, n));
            return;
        }

        m_zoneCards = BattleBoardReader.GetCards(m_manager, z);
        if (m_zoneCards.Count == 0) { Say(LocalizationKey.BATTLE_ZONE_EMPTY, zoneName); return; }
        Say(LocalizationKey.BATTLE_ZONE_AND_CARD, zoneName, CardLabel(0));
    }

    private string CardLabel(int i)
    {
        var card = m_zoneCards![i];
        string name = BattleBoardReader.CardNameWithType(card);
        // In the hand, a pile of identical cards reads as one entry — speak its count like the badge.
        // (Not for the attach temp hand: its total is announced in the zone label instead.)
        if (m_zone == BattleBoardReader.Zone.MyHand && m_attachSel == null)
        {
            int n = BattleBoardReader.HandStackCount(m_manager!, card);
            if (n > 1) name = LocalizationUtils.Format(LocalizationKey.BATTLE_COPIES, name, n);
        }
        // ", has energy to move" / ", can receive energy" during a Solar Transfer-style move
        if (ReparentTag(card) is { } tag) name = LocalizationUtils.Format(LocalizationKey.COMMON_STATE_FORMAT, name, tag);
        return Announce.Item(name, null, i + 1, m_zoneCards.Count);
    }

    // Re-read the current browsable zone's cards in place (board may have changed while a prompt was
    // up, or after a play). Keeps the cursor on a valid index; no speech — navigation reads fresh.
    private void RefreshZoneCards()
    {
        if (m_manager == null || m_zoneCards == null || BattleBoardReader.IsCountOnly(m_zone)) return;
        m_zoneCards = (m_attachSel != null && m_zone == BattleBoardReader.Zone.MyHand)
            ? AttachEnergyCards()
            : BattleBoardReader.GetCards(m_manager, m_zone);
        m_card = m_zoneCards.Count == 0 ? 0 : Mathf.Clamp(m_card, 0, m_zoneCards.Count - 1);
        m_line = -1;
    }

    private void MoveCard(int delta)
    {
        if (m_zoneCards == null || m_zoneCards.Count == 0) return;        // nothing to browse — silent
        int next = Mathf.Clamp(m_card + delta, 0, m_zoneCards.Count - 1);
        if (next == m_card) return;                                       // at an edge — stay silent, no filler
        m_card = next; m_line = -1;
        Say(CardLabel(m_card));
    }

    private void JumpCard(int index)
    {
        if (m_zoneCards == null || m_zoneCards.Count == 0) { Say(LocalizationKey.BATTLE_NO_CARDS_HERE); return; }
        int target = index == int.MaxValue ? m_zoneCards.Count - 1 : index;
        if (target < 0 || target >= m_zoneCards.Count) { Say(LocalizationKey.BATTLE_NO_CARD_THERE); return; }
        m_card = target; m_line = -1;
        Say(CardLabel(m_card));
    }

    private void ReadLine(int dir, bool shift)
    {
        if (m_zoneCards == null || m_card < 0 || m_card >= m_zoneCards.Count) { Say(LocalizationKey.BATTLE_NOTHING_FOCUSED); return; }

        var card = m_zoneCards[m_card];
        var lines = BattleBoardReader.DetailLines(card);

        if (shift && dir < 0) // Shift+Up: repeat current
        {
            Say(m_line < 0 || m_line >= lines.Count ? BattleBoardReader.CardName(card) : lines[m_line]);
            return;
        }
        if (shift && dir > 0) // Shift+Down: read from current line to the end
        {
            int from = m_line < 0 ? 0 : m_line;
            if (from >= lines.Count) { Say(LocalizationKey.BATTLE_END_OF_CARD); return; }
            for (int i = from; i < lines.Count; i++) Say(lines[i], interrupt: i == from);
            m_line = lines.Count - 1;
            return;
        }

        if (dir > 0)
        {
            if (m_line < lines.Count - 1) { m_line++; Say(lines[m_line]); }
            else if (lines.Count > 0) { m_line = lines.Count - 1; Say(lines[m_line]); } // at bottom: re-speak last line, no filler
        }
        else
        {
            if (m_line <= 0) { m_line = -1; Say(BattleBoardReader.CardName(card)); }
            else { m_line--; Say(lines[m_line]); }
        }
    }

    private void AnnounceStatus()
    {
        if (m_manager == null) return;
        var parts = new List<string> { PhaseText(m_phase) ?? LocalizationUtils.Get(LocalizationKey.BATTLE_NAME) };
        // Surface the tutorial objective on demand — gives a reliable "what does it want me to do?"
        // answer when the FTUE is mid-step (the T key the player already uses for status).
        var ftueCard = FtueFocusedCard();
        if (ftueCard != null) parts.Add(TutorialWantsText(ftueCard));
        parts.Add(LocalizationUtils.Format(LocalizationKey.BATTLE_STATUS_PRIZES,
            BattleBoardReader.GetCount(BattleBoardReader.Zone.MyPrize), BattleBoardReader.GetCount(BattleBoardReader.Zone.OppPrize)));
        var myActive = BattleBoardReader.GetCards(m_manager, BattleBoardReader.Zone.MyActive).FirstOrDefault();
        var oppActive = BattleBoardReader.GetCards(m_manager, BattleBoardReader.Zone.OppActive).FirstOrDefault();
        if (myActive != null) parts.Add(LocalizationUtils.Format(LocalizationKey.BATTLE_STATUS_MY_ACTIVE, BattleBoardReader.ShortSummary(myActive)));
        if (oppActive != null) parts.Add(LocalizationUtils.Format(LocalizationKey.BATTLE_STATUS_OPP_ACTIVE, BattleBoardReader.ShortSummary(oppActive)));
        Say(string.Join(". ", parts));
    }

    private void EndTurn()
    {
        if (m_manager == null) return;
        if (!m_manager.IsMyTurn) { Say(LocalizationKey.BATTLE_NOT_YOUR_TURN); return; }

        // A local selection is open (a card effect is asking you to choose) — ending the turn now
        // would desync the match. The game blocks it; so do we.
        if (m_localSelecting) { Say(LocalizationKey.BATTLE_FINISH_SELECTION_FIRST); return; }

        // Tutorial forced-card step: FTUECardIndicator sets FTUEMatchManager.focusedCard and loops
        // `while (focusedCard != null)` waiting for THAT card to be played — but it never blocks the
        // end-turn button and canEndTurn stays true, so ending turn here strands the FTUE coroutine
        // and freezes the match. Redirect to the wanted card instead (the native FTUE signal).
        var ftueCard = FtueFocusedCard();
        if (ftueCard != null) { Say(LocalizationKey.BATTLE_TUTORIAL_BEFORE_END_TURN, TutorialWantsText(ftueCard)); return; }

        // Defer to the game's OWN end-turn gate rather than rolling our own: canEndTurn (the match-rules
        // check, recomputed by UpdateEndTurn) plus the real End Turn button's interactable state — the
        // FTUE and turn-processing phases Block() that button during forced / non-idle moments. This is
        // what froze before: the bare op skipped both gates and sent end-turn mid-selection.
        var info = NetworkMatchController.currentMatchInfo;
        bool canEnd = info != null && info.UpdateEndTurn();
        if (!canEnd || IsEndTurnButtonBlocked()) { Say(LocalizationKey.BATTLE_CANT_END_TURN); return; }

        MatchUserOperations.EndTurn("", false);   // the same op the real button sends; result via log / phase
    }

    // Is the real End Turn button non-clickable right now? Two independent flags gate it, and a
    // sighted player can't click it if EITHER is set, so neither may we:
    //  • button.enabled  — the FTUE's BlockAll() → ForceSetButtons(false) → ToggleButton(false) sets
    //    this false during EVERY forced tutorial step (this is the bug that froze us: we only checked
    //    interactable before, which stays true here).
    //  • button.interactable (== !isBlocking) — turn-processing / FTUE Block(key) sets this false.
    // Conservative: if we can't find the button, don't block (canEndTurn still gates).
    private bool IsEndTurnButtonBlocked()
    {
        try
        {
            var btn = m_manager?.MatchInfoSidebar?.EndTurnButton;
            var t = btn?.EndButtonTransform;
            var uiBtn = t != null ? t.GetComponent<UnityEngine.UI.Button>() : null;
            return uiBtn != null && (!uiBtn.enabled || !uiBtn.interactable);
        }
        catch (Exception ex) { Log.Error($"Battle: end-turn button read threw: {ex.Message}"); return false; }
    }

    // The screen-space message bar's "Done" / confirm button (set-up confirm, "press Done to continue"
    // moments). It's shown when the game wants a confirm; Enter presses it. CentralMessageBar.doneButton
    // is only active when a Done action is wired, so activeInHierarchy is the "is there a Done to press".
    private bool MessageBarDoneAvailable() => MessageBarDoneButton() != null;

    // Whichever CentralMessageBar owns the Done the player is looking at. An attach distribution carries
    // its own bar reference (a [SerializeField] on the menu prefab, so it need not be the shared
    // screen-space one) and drives that bar's Done interactability itself — read it from the menu rather
    // than assuming. Everything else (setup confirm, "Choose your starting Pokémon") uses the screen-space bar.
    private CentralMessageBar? ActiveMessageBar()
    {
        if (m_attachSel != null && ReadField<CentralMessageBar>(m_attachSel, "messageBar") is { } bar && bar != null) return bar;
        return m_manager?.MessageBarScreenSpace;
    }

    private void AnnounceDoneBar()
    {
        // Read only the game's own message-bar prompt — no "Press Tab for Done" hint (the player
        // discovers Done by tabbing, the universal convention; F1 lists it). During an attach distribution
        // this is the menu's own instruction ("Attach N energy to your Pokémon"), which is also the answer
        // to "why is Done unavailable".
        string msg = ActiveMessageBar()?.CurrText?.Trim() ?? "";
        if (msg.Length > 0) Say(msg);
    }

    // The real Done button on the game's message bar (CentralMessageBar.DoneButtonTransform). Null when
    // the bar isn't showing one — some selections auto-submit and never offer Done.
    private UnityEngine.UI.Button? MessageBarDoneButton()
    {
        try
        {
            var t = ActiveMessageBar()?.DoneButtonTransform;
            var btn = t != null ? t.GetComponent<UnityEngine.UI.Button>() : null;
            return btn != null && btn.gameObject.activeInHierarchy ? btn : null;
        }
        catch { return null; }
    }

    // Can the game actually accept Done right now? The BUTTON's interactable flag is the answer, and it
    // is the only one. The attach distribution drives it through UpdateSubmitButtonInteractability →
    // messageBar.SetButtonInteractability(AttachmentConstraints.AreMinimumConstraintsSatisfied() && …),
    // and during the tutorial FTUECardIndicator sets SecondaryAttachedQuantityRequirement, so
    // AreMinimumConstraintsSatisfied means "you have attached exactly the N energies I asked for".
    private bool MessageBarDoneUsable() => MessageBarDoneButton() is { } b && b.IsInteractable();

    // Confirm the focused Done — the same call a sighted player's tap makes.
    //
    // Button.interactable does NOT gate onClick.Invoke(); it only stops the POINTER from raising the
    // click. So invoking a greyed-out Done runs its handler as if the rules were met. That's what let
    // Done submit an attach distribution with zero energies attached: CompleteReparentingAction ran, the
    // menu closed, and the tutorial's FTUECardIndicator — which loops `while (focusedCard != null)` once
    // per energy it wants placed — waited forever, leaving every card on the board FTUE-blocked. The game
    // itself only ever bypasses the minimum from OnTimeExpired(). So: never invoke a button the game has
    // disabled, and never reach past it to the op behind it (we used to reflect straight into the private
    // CompleteReparentingAction — that bypassed the gate even more thoroughly).
    private void ConfirmDone()
    {
        var btn = MessageBarDoneButton();
        if (btn == null) { Say(LocalizationKey.BATTLE_CANT_CONFIRM); return; }
        if (!btn.IsInteractable()) { Say(LocalizationKey.BATTLE_DONE_UNAVAILABLE); return; }
        // No "Done" speech on the press: Tab already announced it when focus moved there, so saying it
        // again on confirm is a redundant double-announce (user-reported). The game's own next-phase /
        // battle-log narration is the confirmation.
        try { btn.onClick.Invoke(); }
        catch (Exception ex) { Log.Error($"Battle: Done press threw: {ex.Message}"); }
    }

    #endregion

    #region Card view

    // The game has THREE single-card inspection overlays, and which one a click opens depends on the
    // card (verified in the decompiled controllers): a board Pokémon DEEP-inspects to the STACK overlay
    // (stackCardOverlay — Pokémon + its attached cards), and SHALLOW-inspects (or a hand card always
    // opens) the BIG card overlay (bigCardOverlay); a card pile opens the CARD INSPECTION menu
    // (cardInspectionMenu). All three are BattleMenus with isOpen/Close. The mod used to track only
    // bigCardOverlay, so inspecting a board Pokémon (which opens stackCardOverlay) left the handler
    // thinking nothing was open → it fell back to field nav and Escape opened the battle menu instead
    // of closing the viewer. "Is a viewer open" / "close the viewer" must cover all three.
    private bool InspectionOpen() => OpenInspection() != null;

    private BattleMenu? OpenInspection()
    {
        var m = m_manager;
        if (m == null) return null;
        if (m.bigCardOverlay != null && m.bigCardOverlay.isOpen) return m.bigCardOverlay;
        if (m.stackCardOverlay != null && m.stackCardOverlay.isOpen) return m.stackCardOverlay;
        if (m.cardInspectionMenu != null && m.cardInspectionMenu.isOpen) return m.cardInspectionMenu;
        return null;
    }

    // Close whichever inspection overlay is open (each routes through the game's menu navigation GoBack).
    private void CloseInspection()
    {
        var open = OpenInspection();
        if (open == null) return;
        try { open.Close(); } catch (Exception ex) { Log.Error($"Battle: inspection Close threw: {ex.Message}"); }
        Say(LocalizationKey.BATTLE_CARD_VIEW_CLOSED);
    }

    // I → open the game's card viewer for the focused card AND enter a locked reading mode: you stay on this
    // one card and read every printed line with Up/Down. Backspace/Escape/I exits. This drives the same
    // inspection path a mouse tap uses (owner.OnCardClicked → TryInitiateOwnerInspection → the right overlay),
    // and it satisfies tutorial coaching that gates on the big-card view being open (FTUEBigCardIndicator
    // waits on bigCardOverlay.isOpen + blocks placement until you've looked).
    private void EnterCardView()
    {
        if (m_manager == null) return;
        if (InspectionOpen()) { CloseInspection(); m_mode = Mode.Nav; m_cvCard = null; m_cvLines = null; return; }   // toggle off an already-open viewer (e.g. FTUE)
        if (m_zoneCards == null || m_card < 0 || m_card >= m_zoneCards.Count) return;
        var card = m_zoneCards[m_card];
        if (card == null) return;

        // isAltPress: true forces the INSPECTION path (matching the game's own Card3D.OnCardClicked,
        // which always forwards true) — without it, your own Pokémon with a usable action would open the
        // ATTACK overlay instead of a card viewer. Enter is the mod's action key; I is purely inspect.
        if (card.owner is BaseCardOwner owner)
        {
            try { owner.OnCardClicked(card, true); }   // opens the real viewer (also clears FTUE big-card gates)
            catch (Exception ex) { Log.Error($"Battle: inspect OnCardClicked threw: {ex.Message}"); }
        }
        // Only enter the locked reading mode if a viewer actually opened — the game blocks inspection in
        // some states (FTUEMatchManager.blockInspection) and some cards aren't inspectable. Entering
        // CardView with nothing open is exactly the stranded state this fix removes.
        if (!InspectionOpen()) { Say(LocalizationKey.BATTLE_CANT_INSPECT); return; }
        m_cvCard = card;
        m_cvLines = BattleBoardReader.DetailLines(card);
        m_cvLine = -1;
        m_mode = Mode.CardView;
        Say(LocalizationKey.BATTLE_CARD_VIEW, BattleBoardReader.CardNameWithType(card));
    }

    private void HandleCardView()
    {
        // Exit if the viewer was closed externally (the game/FTUE dismissed it).
        if (!InspectionOpen()) { m_mode = Mode.Nav; m_cvCard = null; m_cvLines = null; return; }

        if (AccessibleKey.READ_NEXT_LINE.IsPressed()) { CvReadLine(1); return; }
        if (AccessibleKey.READ_PREV_LINE.IsPressed()) { CvReadLine(-1); return; }
        if (AccessibleKey.BACK.IsPressed() || AccessibleKey.ESCAPE.IsPressed() || AccessibleKey.BATTLE_INSPECT.IsPressed()) ExitCardView();
        // Left/Right deliberately ignored — you stay on this one card while reading.
    }

    private void CvReadLine(int dir)
    {
        var lines = m_cvLines;
        if (lines == null || lines.Count == 0) { Say(BattleBoardReader.CardNameWithType(m_cvCard!)); return; }
        if (dir > 0)
        {
            if (m_cvLine < lines.Count - 1) m_cvLine++;
            else m_cvLine = lines.Count - 1;   // stay at the last line
            Say(lines[m_cvLine]);
        }
        else
        {
            if (m_cvLine <= 0) { m_cvLine = -1; Say(BattleBoardReader.CardNameWithType(m_cvCard!)); }
            else { m_cvLine--; Say(lines[m_cvLine]); }
        }
    }

    private void ExitCardView()
    {
        CloseInspection();
        m_mode = Mode.Nav;
        m_cvCard = null;
        m_cvLines = null;
        m_cvLine = -1;
    }

    #endregion

    #region Action menu

    private void OpenActionMenu()
    {
        Log.Info($"OpenActionMenu entry: zone={m_zone} card={m_card}/{m_zoneCards?.Count ?? -1} localSel={m_localSelecting} holding={m_holding} mode={m_mode} attach={m_attachSel != null}");
        // Attach-distribution selection active: Enter on a hand energy picks it up to attach (the drop
        // onto a Pokémon goes through AttemptDrop). This is BEFORE the m_localSelecting block because the
        // attach IS the pending selection — it's fulfilled by this drag, not blocked by it.
        if (m_attachSel != null)
        {
            var focused = (m_zoneCards != null && m_card >= 0 && m_card < m_zoneCards.Count) ? m_zoneCards[m_card] : null;
            // Move-energy (Solar Transfer): Enter on a source Pokémon lifts its energy and holds it; you
            // then drop it on a target like any attach. Other board cards have nothing to move.
            if (m_attachSel is ReparentCardMenu)
            {
                if (IsReparentSource(focused)) { DistributeFromReparentSource(focused!); return; }
                Say(focused != null
                    ? LocalizationUtils.Format(LocalizationKey.BATTLE_NO_ENERGY_TO_MOVE_ON, BattleBoardReader.CardName(focused))
                    : LocalizationUtils.Get(LocalizationKey.BATTLE_CHOOSE_MOVE_SOURCE));
                return;
            }
            // Attach-from-pile (Oricorio ex): Enter on a temp-hand energy picks it up to attach.
            if (focused != null) AttachPickup(focused);
            else Say(LocalizationKey.BATTLE_SELECT_HAND_ENERGY);
            return;
        }
        // While the game is waiting on a local selection, starting a new action would desync — block it.
        if (m_localSelecting) { Say(LocalizationKey.BATTLE_FINISH_SELECTION_FIRST); return; }
        // Re-read the zone before acting: an earlier action (e.g. attaching energy via Excited Turbo)
        // churns card ownership, which can leave the cached m_zoneCards holding a stale/null reference
        // for a slot. Refreshing here picks up the live Card3D so Enter on the Active spot acts on the
        // real Pokémon instead of silently no-oping on a dead reference.
        RefreshZoneCards();

        // A null card here is a stale/empty slot even after refresh — tell the player instead of silently
        // doing nothing (the "frozen, Enter does nothing on my Active" symptom).
        var card = (m_zoneCards != null && m_card >= 0 && m_card < m_zoneCards.Count) ? m_zoneCards[m_card] : null;
        if (card == null) { Say(LocalizationKey.BATTLE_NOTHING_TO_ACT_ON); return; }

        // Tutorial guard: only act on cards the tutorial currently PERMITS. DoesFTUEPermitCardTargetability
        // is the game's own rule — TRUE in free play (the guided part is over and you play freely to win),
        // TRUE for the spotlighted card during a forced step, FALSE otherwise (wrong card, or a step that
        // hasn't armed yet). This blocks the "I attacked before the tutorial allowed it" freeze WITHOUT
        // the false "tutorial isn't ready" during a free turn (the old blockInputs+focusedCard guard hit
        // that because blockInputs stays set under free play). Normal (non-FTUE) matches never block.
        if (!FtuePermitsCard(card))
        {
            var ftueCard = FtueFocusedCard();
            // Log the RAW spotlight — the filtered one is null whenever the board is mid-resolve, and
            // "spotlight=none" in a block line would hide exactly what we'd want to see afterwards.
            var raw = RawFtueFocusedCard();
            Log.Info($"OpenActionMenu FTUE-blocked '{BattleBoardReader.CardName(card)}' (spotlight={(raw != null ? BattleBoardReader.CardName(raw) : "none")}, actionable={ftueCard != null}): {PlayDiag(card)}");
            Say(ftueCard != null
                ? LocalizationUtils.Format(LocalizationKey.BATTLE_TUTORIAL_FIRST, TutorialWantsText(ftueCard))
                : LocalizationUtils.Get(LocalizationKey.BATTLE_TUTORIAL_NOT_READY));
            return;
        }

        bool myTurn = m_manager != null && m_manager.CanPerformNewActions;
        // The Setup phase ("Drag a Basic Pokémon to your Active Spot") is NOT LocalTurn, so
        // CanPerformNewActions is false — yet you must be able to place your starting Pokémon then.
        // Hand placement is allowed during setup too; in-play actions (attack/ability/retreat) are not.
        bool canPlace = myTurn || (m_manager != null && m_manager.IsSetupPhase);

        // Route by the ZONE you navigated into, matching what you pressed to get here:
        //  • Your hand → play the card through the game's native placement (pick up → target →
        //    drop). Evolving is "play the evolution from your hand", so it lives on THIS path —
        //    pressing Enter on the board Pokémon opens its attack menu, not evolve.
        //  • Your active / bench → open that Pokémon's real attack / ability / retreat menu.
        if (m_zone == BattleBoardReader.Zone.MyHand && canPlace)
        {
            StartPickup(card);
            return;
        }

        // For one of MY in-play Pokémon, open the game's REAL AttackOverlay FIRST. Show() populates
        // its option widgets synchronously and sets isOpen — exactly what the FTUE attack coach
        // waits on — then we navigate/click those real options rather than firing our own ops.
        bool overlayCard = (m_zone == BattleBoardReader.Zone.MyActive || m_zone == BattleBoardReader.Zone.MyBench) && myTurn;
        // The in-play Stadium is activated exactly like a Pokémon ability — the game's StadiumController
        // taps it open in the SAME AttackOverlay (focusMenu.Show) whenever it has a usable action. So a
        // stadium with a usable ability gets the overlay too (ownership doesn't matter — stadiums are
        // shared). Without a usable action it falls through to "No actions for <stadium>".
        bool stadiumCard = m_zone == BattleBoardReader.Zone.Stadium && myTurn
                           && card.cardInfo != null && card.cardInfo.activeUsableActions != null
                           && card.cardInfo.activeUsableActions.Count > 0;
        if (overlayCard || stadiumCard) TryOpenAttackOverlay(card);

        m_actions = BuildActions(card);
        if (m_actions.Count == 0)
        {
            Log.Info($"OpenActionMenu no actions '{BattleBoardReader.CardName(card)}' zone={m_zone} overlayCard={overlayCard} canPlace={canPlace}: {PlayDiag(card)}");
            if (overlayCard || stadiumCard) TryCloseAttackOverlay();
            Say(LocalizationKey.BATTLE_NO_ACTIONS, BattleBoardReader.CardName(card));
            return;
        }

        m_mode = Mode.ActionMenu;
        m_actionIdx = 0;
        // Options may read "unavailable" right now and become usable a moment later: during a tutorial
        // attack step the game only arms its wanted attack once this overlay is open and done animating
        // (FTUEActionIndicator → Card3D.FocusAttack), and the repopulate that follows is deferred by
        // AttackOverlay.framesToWaitOnRefresh. PopulateOptionsHook re-announces if that flips.
        Say(LocalizationKey.BATTLE_ACTIONS_FOR, BattleBoardReader.CardName(card));
        SayAction(0, interrupt: false);
    }

    private void TryOpenAttackOverlay(Card3D card)
    {
        try
        {
            var overlay = m_manager?.attackOverlay;
            if (overlay == null) return;
            // ALWAYS point the overlay at the FOCUSED card — exactly what a sighted player gets by
            // clicking that Pokémon. The game's AttackOverlay.Show only populates when currentCard is
            // null (`if (!(currentCard != null))`), so calling Show on an overlay already open for a
            // DIFFERENT Pokémon is a NO-OP — it keeps the previous card's attack/ability options. That's
            // the freeze: after using Oricorio ex's Excited Turbo the overlay stayed open on Oricorio,
            // so opening Mega Charizard X's menu still listed Excited Turbo, and invoking it fired
            // Oricorio's ability out of context → desync. Close() flips isOpen=false and nulls
            // currentCard synchronously (BattleMenu.Close + AttackOverlay.OnClose), so closing first
            // then Show(card) cleanly repopulates for the focused card.
            if (overlay.isOpen && overlay.currentCard != card) overlay.Close();
            if (!overlay.isOpen) overlay.Show(card);
        }
        catch (Exception ex) { Log.Error($"Battle: AttackOverlay.Show threw: {ex.Message}"); }
    }

    private void TryCloseAttackOverlay()
    {
        try
        {
            var overlay = m_manager?.attackOverlay;
            if (overlay != null && overlay.isOpen) overlay.Close();
        }
        catch (Exception ex) { Log.Error($"Battle: AttackOverlay.Close threw: {ex.Message}"); }
    }

    // "unavailable" is resolved at speak time, never baked into the label — the game can arm or disarm
    // an option after this menu was built (see BattleAction.CanUse).
    private string ActionLabel(int i)
    {
        var a = m_actions[i];
        return Announce.Item(a.Label, a.CanUse() ? null : LocalizationUtils.Get(LocalizationKey.GLOBAL_DISABLED), i + 1, m_actions.Count);
    }

    // Speak an action and remember whether it was usable, so a later repopulate can tell a real change
    // from a no-op and only re-announce when the answer actually flipped.
    private void SayAction(int i, bool interrupt = true)
    {
        m_spokenActionUsable = m_actions[i].CanUse();
        Say(ActionLabel(i), interrupt);
    }

    private void HandleActionMenu()
    {
        if (AccessibleKey.READ_NEXT_MENU_OPTION.IsPressed() || AccessibleKey.READ_NEXT_ITEM.IsPressed())
        {
            if (m_actionIdx < m_actions.Count - 1) { m_actionIdx++; SayAction(m_actionIdx); }
            else Say(LocalizationKey.BATTLE_LAST_ACTION);
            return;
        }
        if (AccessibleKey.READ_PREV_MENU_OPTION.IsPressed() || AccessibleKey.READ_PREV_ITEM.IsPressed())
        {
            if (m_actionIdx > 0) { m_actionIdx--; SayAction(m_actionIdx); }
            else Say(LocalizationKey.BATTLE_FIRST_ACTION);
            return;
        }
        if (AccessibleKey.BACK.IsPressed() || AccessibleKey.ESCAPE.IsPressed())
        {
            // Back out to the board — close the real overlay if we opened it, and just re-announce
            // the card we're on, no "Cancelled" filler.
            m_mode = Mode.Nav;
            TryCloseAttackOverlay();
            if (m_zoneCards != null && m_card >= 0 && m_card < m_zoneCards.Count) Say(CardLabel(m_card));
            return;
        }
        if (AccessibleKey.CONFIRM.IsPressed())
        {
            var action = m_actions[m_actionIdx];
            // The option's own button is the ONLY gate. AttackOverlay.CanUseAttack/CanUseAbility and
            // WeakResistRetreatBar already fold in the FTUE's per-action spotlight (attackFocused /
            // abilityFocused / retreatFocused), and a teaching step calls Card3D.FocusNone() so every
            // option goes non-interactable. We used to layer our own "an FTUEAttackOverlayIndicator is
            // running ⇒ refuse" rule on top; that state outlives its coaching (only the Close/Card/Buffs/
            // Debuffs variants ever call NextState), so it kept refusing the attack the tutorial had
            // since asked for. Trust the game.
            if (!action.CanUse()) { Say(LocalizationKey.BATTLE_NOT_USABLE_NOW); return; }   // greyed option — stay in the menu
            m_mode = Mode.Nav;
            try { action.Invoke(); }   // result narrated by the battle log (localized); no echo from us
            catch (Exception ex) { Log.Error($"Battle action '{action.Label}' threw: {ex.Message}"); }
            // The overlay's own option click closes it (HandleOverlayActionClickCleanup), but a retreat
            // or an ability that leaves it open would otherwise linger on this card and re-surface as
            // stale options on the NEXT Pokémon's menu. Close defensively so the overlay never carries a
            // stale currentCard between Pokémon (the Excited-Turbo-on-Charizard freeze). No-op if already closed.
            TryCloseAttackOverlay();
        }
    }

    // Builds the list of currently-legal actions for the focused card, each wired to the
    // game-native op that performs it (same message the mouse sends).
    private List<BattleAction> BuildActions(Card3D card)
    {
        var list = new List<BattleAction>();
        var info = card?.cardInfo;
        if (card == null || !card.IsPublic || info == null) return list;
        if (m_manager == null || !m_manager.CanPerformNewActions) return list;

        var where = Board.GetPlayerPos(info.currentPos);
        // Stadiums are shared: whoever's turn it is may use the in-play stadium's ability (the server
        // lists it in activeUsableActions) no matter who played it. Every other card needs ownership.
        bool isStadium = where == PlayerPos.STADIUM;
        if (!isStadium && !info.isMyCard) return list;

        // Hand cards are played through the game's native targeting flow, invoked directly from
        // OpenActionMenu (StartPickup → hold-and-drop for target cards, or the no-target
        // auto-play for Item/Trainer/Supporter) — never via direct ops or this action menu.
        if (where != PlayerPos.ACTIVE && where != PlayerPos.BENCH && !isStadium) return list;

        // Drive the game's REAL attack overlay options (opened in OpenActionMenu). Clicking an
        // option's OnClick() is the exact sighted-player flow (→ TryUseAction → confirmations →
        // op) and is what the FTUE watches — never our own UseCard. A stadium surfaces its effect
        // here as an ability option.
        var overlay = m_manager.attackOverlay;
        // The overlay must be open AND showing THIS card. The currentCard guard is the safety net
        // for the stale-overlay bug (see TryOpenAttackOverlay): if Show didn't re-point — e.g. a
        // mid-animation Close left it briefly on the old card — we must NOT read another Pokémon's
        // options onto this one. No match → no actions (caller closes + says "No actions").
        if (overlay == null || !overlay.isOpen || overlay.currentCard != card) return list;

        if (overlay.AttackOptions != null)
            foreach (var opt in overlay.AttackOptions) AddOverlayOption(list, opt, isAttack: true, card);
        if (overlay.AbilityOptions != null)
            foreach (var opt in overlay.AbilityOptions) AddOverlayOption(list, opt, isAttack: false, card);

        // Retreat: same rule as the attacks — the RetreatOption's own button. WeakResistRetreatBar
        // builds it as `owner is ActiveController && canRetreat && CanPerformNewActions &&
        // retreatFocused > -2`, so it already folds in the match-info flag AND the FTUE retreat
        // spotlight. Reading canRetreat alone missed the spotlight, letting a retreat fire during a
        // tutorial step that forbids it. Clean "Retreat" label (no card-action detail/name). Never
        // on a stadium (it has no retreat — its overlay's RetreatOption is hidden anyway).
        var retreat = isStadium ? null : overlay.RetreatOption;
        if (retreat != null)
            list.Add(new BattleAction
            {
                Label = LocalizationUtils.Get(LocalizationKey.BATTLE_RETREAT),
                CanUse = () => OptionUsable(retreat) && !FtueStepUnarmed(card),
                Invoke = () => retreat.OnClick(),
            });
        return list;
    }

    // Wrap one real overlay option as a menu entry whose Invoke is the option's own click. The label
    // reads "<prefix>: <name>, <cost/damage + rules text>" so the player hears WHAT the attack/ability
    // does (important for the abilities tutorial) before the "X of Y" index.
    //
    // Usability comes from the option's OWN button.interactable — the exact state the game greys out
    // and that the mouse path respects. Crucially, the overlay computes that (CanUseAttack/CanUseAbility)
    // as activeUsableActions + CanPerformNewActions + !IsOperationProcessing AND the FTUE per-action
    // spotlight (card.attackFocused/abilityFocused): during a tutorial step that highlights ONE action,
    // only that one is interactable. A separate activeUsableActions cross-ref (the old approach) misses
    // the spotlight, so it would let the player fire a non-highlighted action and freeze the tutorial.
    // OnClick() does NOT self-gate (it always fires), so this gate is what stops an unusable invoke.
    private void AddOverlayOption(List<BattleAction> list, CardActionOption opt, bool isAttack, Card3D card)
    {
        if (opt == null) return;
        // The overlay POOLS its option widgets: AttackOptions / AbilityOptions keep leftover entries from
        // a previously-shown Pokémon and just SetActive(false) the ones the current card doesn't have
        // (SetupAbilities/SetupAttacks/HideAttacksForBenchedPokemon). A sighted player never sees those —
        // so we must skip any whose GameObject is inactive, or a phantom option from another Pokémon
        // leaks in (e.g. Oricorio ex's Excited Turbo showing on Mega Charizard X, which has no ability).
        if (!opt.gameObject.activeInHierarchy) return;
        var action = opt.MatchAction;
        bool named = action != null && !string.IsNullOrEmpty(action.actionName);
        string shown = named ? action!.actionName : LocalizationUtils.Get(isAttack ? LocalizationKey.BATTLE_ATTACK : LocalizationKey.BATTLE_USE_ABILITY);
        string detail = named ? BattleBoardReader.ActionDetail(card, shown) : "";
        string label = LocalizationUtils.Format(isAttack ? LocalizationKey.BATTLE_ATTACK_NAMED : LocalizationKey.BATTLE_ABILITY_NAMED, shown);
        if (detail.Length > 0) label = LocalizationUtils.Format(LocalizationKey.COMMON_STATE_FORMAT, label, detail);
        list.Add(new BattleAction { Label = label, CanUse = () => OptionUsable(opt) && !FtueStepUnarmed(card), Invoke = () => opt.OnClick() });
    }

    // An action-arming tutorial step owns this card's actions but hasn't named one yet.
    //
    // FTUEActionIndicator / FTUEAbilityIndicator set focusedCard (BlockOtherCards), wait `while
    // (!overlay.isOpen || overlay.isAnimating)` — i.e. for the PLAYER to open the attack overlay — and
    // only THEN call Card3D.FocusAttack / FocusAbility / FocusRetreat. Until that moment all three focus
    // fields are still their -1 default, and AttackOverlay.CanUseAttack reads -1 as "no tutorial
    // restriction", so every usable attack's button is interactable. A mouse player is shielded from that
    // window by the overlay's open animation plus reaction time; a keyboard crosses it in two frames.
    //
    // Firing there — even the attack the tutorial wants — strands it: CardActionOption.OnClick() calls
    // UnfocusCard() first, so focusedCard goes null, and the indicator is left waiting forever on an
    // overlay we then closed, never reaching FocusAttack or NextState(). focusedCard stays null,
    // FTUEMatchUnlockActions never runs, and DoesFTUEPermitCardTargetability denies every card on the
    // board while the animator never advances to the opponent's AI states. Hard freeze, observed
    // 2026-07-10 after attacking quickly.
    //
    // CRITICAL SCOPING: this pre-arm window only exists under an ARMING indicator. Other spotlight
    // states never arm a per-action focus at all, so their -1 fields mean "no per-action restriction",
    // NOT "not armed yet" — and the game's own button.interactable already governs usability there.
    // FTUECardIndicator drives a stadium ability that opens a selection (e.g. Surfing Beach): it
    // spotlights the stadium via BlockOtherCards and just waits for the ability to fire, never calling
    // FocusAbility. Treating its permanent -1 as "unarmed" greyed the ability out forever ("can't use
    // the ability"), which is exactly the reported bug. So gate on ActionArmingIndicatorActive first.
    //
    // Armed = any of the three focus fields is >= 0. FocusNone() parks them at -2, which CanUseAttack
    // already treats as "nothing allowed", so a teaching step is unaffected by this check. Free play and
    // ordinary matches have focusedCard == null, so it never applies there.
    private bool FtueStepUnarmed(Card3D card)
    {
        if (card == null || !ReferenceEquals(RawFtueFocusedCard(), card)) return false;
        if (!FtueTracker.ActionArmingIndicatorActive) return false;
        return card.attackFocused < 0 && card.abilityFocused < 0 && card.retreatFocused < 0;
    }

    // The game's own "can the player click this option" state: the option button's interactable flag
    // (= _canInteractDelegate() && _isActionUsable, i.e. IsMyTurn && CanUse). This is exactly what the
    // mouse path obeys, so we obey it too rather than re-deriving usability ourselves.
    private static bool OptionUsable(CardActionOption opt)
    {
        try
        {
            var t = opt?.ButtonTransform;
            var btn = t != null ? t.GetComponent<UnityEngine.UI.Button>() : null;
            return btn != null && btn.interactable;
        }
        catch { return false; }
    }

    #endregion

    #region Hand-card placement
    // Enter on a hand card runs it through the game's CardTargetingHandler. Cards that need a board
    // TARGET — Pokémon, Energy, Tool, Stadium — "hold and drop": pick up here, navigate the board
    // normally (A / B / arrows), then press Enter on the destination to drop — attach/evolve onto
    // the focused Pokémon, or play to the focused zone; Backspace cancels. Cards with NO board target
    // — Item / Trainer / Supporter — play immediately to the pending slot (no drag, no field nav);
    // see the no-target branch below. No separate target menu: the field shortcuts ARE the targeting.

    private void StartPickup(Card3D card)
    {
        var info = card.cardInfo;
        if (info == null || m_manager == null) { Say(LocalizationKey.BATTLE_CANT_PLAY_THIS); return; }
        if (info.placeableBoardPos.Count == 0 && info.attachableEntities.Count == 0 && info.activeUsableActions.Count == 0)
        {
            Log.Info($"StartPickup blocked (no legal play) '{BattleBoardReader.CardName(card)}': {PlayDiag(card)}");
            Say(LocalizationKey.BATTLE_CANT_PLAY_RIGHT_NOW, BattleBoardReader.CardName(card));
            return;
        }

        // Act on the instance the targeting evaluator actually keys on (the hand sender's), not our
        // owner-list instance — see ResolveHandTargetingInstance. Fall back to our own if the hand
        // sender doesn't list it (shouldn't happen for a hand card, but keep the play path alive).
        var resolved = ResolveHandTargetingInstance(info.entityID);
        var targetCard = resolved ?? card;
        if (resolved == null)
            Log.Info($"StartPickup: '{BattleBoardReader.CardName(card)}' (eid={info.entityID}) NOT in hand sender's accessible cards — using owner-list instance");
        else if (!ReferenceEquals(resolved, card))
            Log.Info($"StartPickup: '{BattleBoardReader.CardName(card)}' owner-list instance differed from hand-sender instance — using sender's");

        // Does this card need the player to choose a board target? Pokémon / Energy / Tool / Stadium
        // do (a Pokémon to attach or evolve onto, or a zone to place into) — mirrors the game's
        // CardHasAttachableEntities / CardHasPlaceableBoardPositions purposes. Item / Trainer /
        // Supporter do not — they have only activeUsableActions and resolve to the fixed pending slot
        // (CardHasUsableActions → UseCard), so there's nothing to drag to.
        bool needsTarget = info.placeableBoardPos.Count > 0 || info.attachableEntities.Count > 0;
        if (!needsTarget)
        {
            // No board target → no drag, no field nav. Confirm in place: "Use X?" — Enter plays it,
            // Backspace cancels (HandleConfirmPlay). The actual play runs in BeginNoTargetPlay. We don't
            // touch the targeting handler until confirmed, so cancelling leaves nothing to clean up.
            Log.Info($"StartPickup → ConfirmPlay (no-target) '{BattleBoardReader.CardName(targetCard)}': use={info.activeUsableActions.Count}");
            m_confirmCard = targetCard;
            m_mode = Mode.ConfirmPlay;
            Say(LocalizationKey.BATTLE_USE_CARD_QUESTION, BattleBoardReader.CardName(targetCard));
            return;
        }

        // Target card: pick it up through the game's targeting handler and enter the navigate-and-drop
        // hold (Backspace cancels; Enter on the destination drops). TryTargetCard IS the game's own
        // authority — it runs IsCardTargetable itself, so we don't pre-check (that was redundant).
        m_manager.ApplyIdleStatePlacementContext();
        m_placeInit.Reset();
        if (!m_manager.TargetingHandler.TryTargetCard(targetCard, m_placeInit))
        {
            Log.Info($"StartPickup: TryTargetCard rejected '{BattleBoardReader.CardName(targetCard)}': status={m_manager.TargetingHandler.GetTargetabilityStatus(targetCard)} resolved={resolved != null} {PlayDiag(targetCard)}");
            Say(LocalizationKey.BATTLE_CANT_PLAY_NOW, BattleBoardReader.CardName(targetCard));
            return;
        }

        m_holding = true;
        m_heldCard = targetCard;
        Say(LocalizationKey.BATTLE_HOLDING, BattleBoardReader.CardName(targetCard));
    }

    // Confirmed a no-target card → run it through the game's targeting handler aimed at the pending
    // slot. No m_holding, so the field stays un-navigable. Driving the real targeting handler (not a
    // bare UseCard) keeps FTUE coaching and any play confirmations firing.
    //
    // Ordering matters and is dictated by the game's evaluation loop
    // (ReceiverPlacementHandler.EvaluateReceiversUntilPlacementAttemptIsEndedByInitiator): each pass
    // records the imminent receiver, yields a frame, and only THEN asks the initiator whether to
    // place. So the receiver must already be chosen when the loop's first pass runs — otherwise the
    // pass records nothing and the attempt resolves against a null receiver (→ CancelPlacement). We
    // therefore SetChosen before TryTargetCard starts the loop, then arm once. No frame counting: the
    // arm simply survives until the loop's next exit check, exactly like the game's own
    // MatchAutomationTargetingInitiator.
    private void BeginNoTargetPlay(Card3D card)
    {
        m_mode = Mode.Nav;
        if (m_manager == null) return;
        var pending = PendingReceiver();
        if (pending == null)
        {
            Log.Info($"BeginNoTargetPlay: no pending receiver for '{BattleBoardReader.CardName(card)}'");
            Say(LocalizationKey.BATTLE_CANT_PLAY_NOW, BattleBoardReader.CardName(card));
            return;
        }
        m_manager.ApplyIdleStatePlacementContext();
        m_placeInit.Reset();
        m_placeInit.SetChosen(pending);
        // TryTargetCard is the game's own authority (it runs IsCardTargetable internally) — no pre-check.
        if (!m_manager.TargetingHandler.TryTargetCard(card, m_placeInit))
        {
            Log.Info($"BeginNoTargetPlay: TryTargetCard rejected '{BattleBoardReader.CardName(card)}': status={m_manager.TargetingHandler.GetTargetabilityStatus(card)} {PlayDiag(card)}");
            Say(LocalizationKey.BATTLE_CANT_PLAY_NOW, BattleBoardReader.CardName(card));
            return;
        }
        m_placeInit.ArmPlacement();
        Log.Info("Battle: no-target play armed");   // result narrated by the battle log
    }

    // "Use X?" confirmation for a no-target card. Enter plays it, Backspace/Escape backs out without
    // playing. All other keys are ignored — it's a simple yes/no in place, no field navigation.
    private void HandleConfirmPlay()
    {
        if (AccessibleKey.CONFIRM.IsPressed())
        {
            var card = m_confirmCard;
            m_confirmCard = null;
            if (card != null) BeginNoTargetPlay(card);
            else m_mode = Mode.Nav;
            return;
        }
        if (AccessibleKey.BACK.IsPressed() || AccessibleKey.ESCAPE.IsPressed())
        {
            m_confirmCard = null;
            m_mode = Mode.Nav;
            // Re-orient on the hand card (we never navigated away) without playing it.
            if (m_zoneCards != null && m_card >= 0 && m_card < m_zoneCards.Count) Say(CardLabel(m_card));
        }
    }

    // Resolve the current drop target from where the player is focused: the focused Pokémon for
    // attach/evolve, or the current zone for play. Receiver is null if the focus isn't a legal spot.
    // The description is for the log only: the spoken result comes from the battle log (localized).
    private (ICardPlacementReceiver? recv, string desc) ComputeDropTarget()
    {
        if (m_heldCard == null || m_manager == null) return (null, "");
        var focused = (m_zoneCards != null && m_card >= 0 && m_card < m_zoneCards.Count) ? m_zoneCards[m_card] : null;

        // Attach-distribution: the receiver is the focused board Pokémon, if the attach permits it
        // (ValidPlacementReceivers). The attach menu's placement context attaches the held energy onto it.
        if (m_attachSel != null)
        {
            if (focused is ICardPlacementReceiver afr && IsAttachReceiver(focused))
                return (afr, "attached to " + BattleBoardReader.CardName(focused));
            return (null, "");
        }

        var info = m_heldCard.cardInfo;
        if (info == null) return (null, "");

        if (info.attachableEntities.Count > 0 && focused != null && info.attachableEntities.Contains(focused.entityID) && focused is ICardPlacementReceiver fr)
            return (fr, (info.isPokemon ? "evolved onto " : "attached to ") + BattleBoardReader.CardName(focused));
        if (info.placeableBoardPos.Count > 0)
        {
            var bp = ZoneToBoardPos(m_zone);
            if (bp.HasValue && info.placeableBoardPos.Contains(bp.Value)
                && m_manager.GetOwnerForBoardPos(bp.Value) is ICardPlacementReceiver zr)
                return (zr, "played to " + m_zone);
        }

        // Trainer / Item / Supporter → play it; no board target, so it goes to the local "pending"
        // slot (the game's own receiver for CardHasUsableActions → UseCard). Drops from any focus.
        if (info.activeUsableActions.Count > 0 && PendingReceiver() is { } pend)
            return (pend, "played");

        return (null, "");
    }

    // Point the game's placement loop at the current focus EVERY FRAME while holding. This is
    // required: the loop commits to whatever receiver it last saw as "imminent" the frame BEFORE
    // we raise Commit, so the chosen receiver must already be set before the Enter that drops.
    private void RefreshHeldChosen() => m_placeInit.SetChosen(ComputeDropTarget().recv);

    // Drop the held card onto whatever the player is currently focused on.
    private void AttemptDrop()
    {
        if (!m_holding || m_heldCard == null || m_manager == null) return;
        var (recv, desc) = ComputeDropTarget();
        if (recv == null) { Say(LocalizationKey.BATTLE_NOT_A_VALID_SPOT); return; }

        m_placeInit.SetChosen(recv);
        // RefreshHeldChosen kept this receiver imminent every frame while holding, so the loop's
        // current pass has already recorded it — arming now commits onto exactly it.
        m_placeInit.ArmPlacement();
        m_holding = false;
        m_heldCard = null;
        Log.Info($"Battle: drop committed — {desc}");   // spoken result comes from the battle log (localized)
    }

    private void CancelPickup()
    {
        try { m_manager?.TargetingHandler.ClearTarget(); }
        catch (Exception ex) { Log.Error($"Battle: ClearTarget threw: {ex.Message}"); }
        m_placeInit.Reset();
        var held = m_heldCard;
        m_holding = false;
        m_heldCard = null;
        Say(held != null
            ? LocalizationUtils.Format(LocalizationKey.BATTLE_CANCELLED_CARD, BattleBoardReader.CardName(held))
            : LocalizationUtils.Get(LocalizationKey.BATTLE_CANCELLED));
    }

    // The local "pending" slot owner — the receiver a no-target card drops onto. It is the single
    // valid receiver the game's own CardHasUsableActions purpose yields
    // (board.GetPlayer(LOCAL).pendingCard), and its RecordPlacement fires MatchUserOperations.UseCard.
    private ICardPlacementReceiver? PendingReceiver() =>
        m_manager != null && m_manager.GetOwnerForBoardPos(Board.GetBoardPos(PlayerPos.PENDING, PlayerID.LOCAL)) is ICardPlacementReceiver p ? p : null;

    private static BoardPos? ZoneToBoardPos(BattleBoardReader.Zone z) => z switch
    {
        BattleBoardReader.Zone.MyActive => Board.GetBoardPos(PlayerPos.ACTIVE, PlayerID.LOCAL),
        BattleBoardReader.Zone.MyBench => Board.GetBoardPos(PlayerPos.BENCH, PlayerID.LOCAL),
        BattleBoardReader.Zone.Stadium => Board.GetBoardPos(PlayerPos.STADIUM, PlayerID.LOCAL),
        _ => (BoardPos?)null,
    };

    // The targeting evaluator (PlacementPurposeEvaluator) decides "is this card placeable" with a
    // REFERENCE-keyed dictionary built from the hand SENDER's own Card3D instances —
    // HandController.GetCardsAccessibleForTargeting() = each hand slot's top card. We navigate the
    // hand off the card OWNER's list (PlayerCardOwner.GetOwnedCards via BattleBoardReader), and after
    // a hand mutation (a search puts a card into hand, a play removes one) those two sources can hold
    // DIFFERENT instances for the same logical card. Since IsCardPlaceable is ContainsKey(instance),
    // our owner-list instance then reads as "not targetable" even though the game says the card is
    // playable (attachableEntities non-empty). Re-fetch the sender's instance by entityID so we pick
    // up / target exactly what the evaluator validated. Returns null if the hand has no such card.
    private Card3D? ResolveHandTargetingInstance(string entityID)
    {
        if (string.IsNullOrEmpty(entityID) || m_manager == null) return null;
        try
        {
            var hand = m_manager.board?.GetPlayer(PlayerID.LOCAL)?.hand;
            if (hand == null) return null;
            // Exact instance, when it's the top of its slot.
            foreach (var c in hand.GetCardsAccessibleForTargeting())
                if (c != null && c.cardInfo != null && c.cardInfo.entityID == entityID) return c;
            // Otherwise it's a DUPLICATE buried in a stacked slot. HandCardSlot stacks identical cards
            // (same cardDataRow.ArchetypeID — shown as ONE pile with a count badge) and only the TOP
            // card is "accessible for targeting" (GetCardsAccessibleForTargeting = slot top-cards). So a
            // second copy of the same card reads as un-targetable. Target the slot's top card instead —
            // an interchangeable copy, so the play is identical: exactly what clicking the visual stack does.
            foreach (var slot in hand.HandSlots)
                if (slot != null && slot.DoesSlotContainCardEntityID(entityID))
                    return slot.GetTopCard();
        }
        catch (Exception ex) { Log.Warn($"ResolveHandTargetingInstance threw: {ex.Message}"); }
        return null;
    }

    // Diagnostic line for the "you can't play X" exits. The card's legal-play sets are CACHED on
    // cardInfo (the SDK recomputes them in CardInfo.UpdateAll on every server state update) and a
    // HAND card's sets are only filled when the SDK's myPlayerInfo.isMyTurn is true. We enter the
    // play path on CanPerformNewActions / IsSetupPhase, which is a different turn notion — so logging
    // both the counts AND the two turn flags side by side tells us whether a block is a real game
    // rule (counts 0 while it genuinely is our turn) or a turn-notion mismatch (mgr says go, SDK
    // hasn't filled legality). Only called on a block, so the cost is irrelevant.
    private string PlayDiag(Card3D card)
    {
        var info = card?.cardInfo;
        string counts = info != null
            ? $"place={info.placeableBoardPos.Count} attach={info.attachableEntities.Count} use={info.activeUsableActions.Count}"
            : "no cardInfo";
        bool sdkTurn = false;
        try { sdkTurn = NetworkMatchController.currentMatchInfo?.myPlayerInfo?.isMyTurn ?? false; } catch { }
        return $"{counts} canPerform={m_manager?.CanPerformNewActions} setup={m_manager?.IsSetupPhase} mgrTurn={m_manager?.IsMyTurn} sdkTurn={sdkTurn} ftueGoverns={FtueTracker.FtueGovernsMatch(m_manager)}";
    }

    #endregion

    #region Attach distribution
    // AccessibleBattlePrompt routes this selection here instead of opening a mod overlay: the player attaches
    // energy exactly like on their turn — pick up a hand energy, navigate to a benched Pokémon, drop;
    // Tab = Done. All of this is gated on m_attachSel, so normal play / other prompts are untouched.

    // Called by AccessibleBattlePrompt when the attach selection opens. Announce the game's prompt; the
    // player then uses C (hand) → Enter to grab an energy → B → drop on a Pokémon → Tab to finish.
    internal static void BeginAttachSelection(AttachmentDistributionSelectionMenu menu, string question)
    {
        if (menu == null) return;
        var self = s_instance;
        self.m_attachSel = menu;
        self.m_holding = false;
        self.m_heldCard = null;
        if (!string.IsNullOrWhiteSpace(question)) AccessibilityMgr.Output(self, question, interrupt: true);
        Log.Info("Battle: attach-distribution selection — using board drag");
    }

    private void EndAttach()
    {
        m_attachSel = null;
        m_reparentPickupPending = null;
        if (m_holding)
        {
            try { m_manager?.TargetingHandler.ClearTarget(); } catch { }
            m_placeInit.Reset();
            m_holding = false;
            m_heldCard = null;
        }
    }

    // Pick up a hand energy to attach. Leaves the attach menu's placement context active (does NOT switch
    // to the idle context), so TryTargetCard routes the energy through the menu's AttachCardToOtherCard
    // purpose; the handler itself rejects a card that can't be attached.
    private void AttachPickup(Card3D card)
    {
        if (m_attachSel == null || m_manager == null || card == null) return;
        // The energies sit in the menu's TEMPORARY hand; like any carousel/hand card they only become
        // targetable once the placement context re-evaluates their just-settled positions. StartPickup
        // gets this for free via ApplyIdleStatePlacementContext, but we must NOT switch contexts here —
        // that would drop the menu's AttachCardToOtherCard purpose. So refresh the CURRENT (attach)
        // context instead. This was the missing step and the likely cause of the reattach intermittently
        // not taking online (the energy wasn't targetable yet, so TryTargetCard silently rejected it).
        try { m_manager.TargetingHandler.RefreshPlacementContext(); }
        catch (Exception ex) { Log.Error($"Battle: attach RefreshPlacementContext threw: {ex.Message}"); }
        m_placeInit.Reset();
        if (!m_manager.TargetingHandler.TryTargetCard(card, m_placeInit)) { Say(LocalizationKey.BATTLE_CANT_ATTACH, BattleBoardReader.CardName(card)); return; }
        m_holding = true;
        m_heldCard = card;
        Say(LocalizationKey.BATTLE_HOLDING, BattleBoardReader.CardName(card));
    }

    // Is the focused board card a Pokémon the held energy may attach to (per the attach menu's own rule)?
    private bool IsAttachReceiver(Card3D c)
    {
        if (m_attachSel == null || m_heldCard == null || c == null) return false;
        try { return m_attachSel.ValidPlacementReceivers(m_heldCard).Any(r => ReferenceEquals(r, c)); }
        catch { return false; }
    }

    // The menu's temporary hand (a ReparentHandController : HandController) holding the attachable
    // energies. Private on the menu → reflected. This is where the energies live during the attach, NOT
    // board.GetPlayer(LOCAL).hand — so C reads from here while m_attachSel is set.
    private HandController? AttachHand() => m_attachSel == null ? null : ReadField<HandController>(m_attachSel, "tempHand");

    // The attach energies as navigable cards (stacked pile tops, like the real hand). Pickup peels the top.
    private List<Card3D> AttachEnergyCards()
    {
        var hand = AttachHand();
        if (hand == null) return new List<Card3D>();
        try { return hand.GetCardsAccessibleForTargeting().Where(c => c != null).ToList(); }
        catch (Exception ex) { Log.Error($"Battle: attach hand cards threw: {ex.Message}"); return new List<Card3D>(); }
    }

    // How many energies are still available to attach (total, across the pile) — for the "N available" label.
    private int AttachEnergyCount()
    {
        var hand = AttachHand();
        if (hand == null) return 0;
        try { return hand.GetOwnedCards().Count(c => c != null); }
        catch { return AttachEnergyCards().Count; }
    }

    // ---- Reparent (MOVE energy already on your Pokémon — e.g. Mega Venusaur ex's Solar Transfer) ----
    // Unlike attach-from-a-pile, the energy starts ON a Pokémon and only becomes grabbable after you pick
    // which Pokémon to take it FROM: a ReparentCardMenu puts a "distribute" click override on each eligible
    // source, and clicking one lifts its movable energy into the temp hand. So in a ReparentCardMenu, Enter
    // on a source Pokémon distributes + picks up its energy; you then drop it on a target exactly like any
    // other attach (the drop path in ComputeDropTarget/AttemptDrop is unchanged). Matches the sighted flow:
    // click a source, then click a target. AttachFromPile (Oricorio ex) is untouched — its energy is
    // already in the temp hand, so it never reaches these methods.

    // The source Pokémon whose energy is currently lifted into the temp hand (null if none).
    private string? CurrentReparentSourceId()
    {
        try { return (AttachHand() as ReparentHandController)?.CurrentDistributingEndpoint?.EndpointID; }
        catch { return null; }
    }

    // The Pokémon you can move energy FROM right now — the ones still holding a movable energy. Passing
    // null does NOT exclude the one mid-distribute, so Enter on it again grabs its next energy. Empty for
    // a non-reparent attach (AttachFromPile) or a non-reparent context.
    private HashSet<string> ReparentSources()
    {
        var set = new HashSet<string>();
        if (m_attachSel is not ReparentCardMenu) return set;
        try
        {
            foreach (var id in m_attachSel.AttachmentConstraints.GetCardIDsEligibleToDistributeAttachments(null))
                if (!string.IsNullOrEmpty(id)) set.Add(id);
        }
        catch (Exception ex) { Log.Error($"Battle: ReparentSources threw: {ex.Message}"); }
        return set;
    }

    private bool IsReparentSource(Card3D? card) =>
        card != null && m_attachSel is ReparentCardMenu && ReparentSources().Contains(card.entityID);

    // Enter on a source Pokémon during a move: lift a movable energy off it (the game's own distribute
    // click) and hold it, ready to drop on a target. Reuses AttachPickup, so the whole drop path is shared.
    private void DistributeFromReparentSource(Card3D source)
    {
        if (m_attachSel == null || m_manager == null || source == null) return;
        // Only click to distribute when this source isn't already the lifted one — the game swaps the
        // current distributor's override to Inspect, so clicking it again would open the card viewer.
        if (CurrentReparentSourceId() != source.entityID)
        {
            var listener = source.Interactions != null ? source.Interactions.OverrideClickListener : null;
            if (listener == null)   // no distribute override (a server round-trip paused it, or it isn't a source)
            {
                Say(LocalizationKey.BATTLE_CANT_MOVE_ENERGY_FROM, BattleBoardReader.CardName(source));
                return;
            }
            try { listener.OnCardClicked(source, false); }   // SendCardToDistributorOnClick → lifts its energy into the temp hand
            catch (Exception ex) { Log.Error($"Battle: reparent distribute threw: {ex.Message}"); }
            try { m_manager.TargetingHandler.RefreshPlacementContext(); } catch { }
        }
        if (!TryPickUpLiftedEnergy(source))
        {
            // The lifted energy hasn't surfaced in the temp hand this frame — retry from HandleInput rather
            // than dropping the keypress on a blind player. Clears itself once it takes or the menu closes.
            m_reparentPickupPending = source;
            m_reparentPickupTicks = 0;
        }
    }

    // Pick up the energy the source just lifted into the temp hand. Returns false ONLY when nothing has
    // surfaced yet (empty temp hand) — the one retry-worthy case. If energy IS present but the pickup is
    // rejected, AttachPickup already announced it and we return true, so the retry never spams "Can't
    // attach". Shared by the immediate path and the per-frame retry.
    private bool TryPickUpLiftedEnergy(Card3D source)
    {
        var energies = AttachEnergyCards();
        if (energies.Count == 0) return false;   // not surfaced yet → caller may retry
        AttachPickup(energies[0]);               // says "Holding <energy>" on success, "Can't attach <energy>" on reject
        if (m_holding) Say(LocalizationUtils.Format(LocalizationKey.BATTLE_FROM_SOURCE, BattleBoardReader.CardName(source)), interrupt: false);
        return true;                             // handled either way — do not retry
    }

    // Finish a move-energy pickup whose energy wasn't in the temp hand on the distribute frame. No re-click
    // (the source is already the distributing one), just re-check until the energy surfaces or we give up.
    private void TickReparentPickup()
    {
        if (m_attachSel == null || !m_attachSel.isOpen || m_reparentPickupPending == null) { m_reparentPickupPending = null; return; }
        try { m_manager?.TargetingHandler.RefreshPlacementContext(); } catch { }
        var source = m_reparentPickupPending;
        if (TryPickUpLiftedEnergy(source)) { m_reparentPickupPending = null; return; }
        if (++m_reparentPickupTicks >= 10)   // ~10 frames and still nothing — tell the player, don't hang
        {
            m_reparentPickupPending = null;
            Say(LocalizationKey.BATTLE_NO_ENERGY_TO_MOVE_FROM, BattleBoardReader.CardName(source));
        }
    }

    // The move-energy state of a board Pokémon: valid sources while choosing, valid receivers while holding.
    // Null for anything that isn't a move target (so opponent/energy/pile read plain).
    private string? ReparentTag(Card3D card)
    {
        if (m_attachSel is not ReparentCardMenu || card == null) return null;
        if (m_holding) return IsAttachReceiver(card) ? LocalizationUtils.Get(LocalizationKey.BATTLE_CAN_RECEIVE_ENERGY) : null;
        return IsReparentSource(card) ? LocalizationUtils.Get(LocalizationKey.BATTLE_HAS_ENERGY_TO_MOVE) : null;
    }

    #endregion

    #region Tutorial (FTUE) guidance: keep the player on the spotlighted card

    // The FTUE's spotlighted card exactly as the game reports it. Non-null does NOT mean the player
    // still has to go and act on it — use FtueFocusedCard for that. Gated on the game's per-match
    // FTUE switch (the installed permit delegate), NOT the global lesson flag — that flag stays true
    // for parked / always-running lessons right through ranked matches (see FtueTracker.MatchFtuePermit).
    private Card3D? RawFtueFocusedCard() => FtueTracker.FtueGovernsMatch(m_manager) ? FtueFocus.Card : null;

    // The spotlight, but only while it names something the player can actually walk to and act on.
    //
    // `focusedCard` outlives the action that satisfies it. The FTUE clears it through
    // Card3D.UnfocusCard → WaitToUnfocus, and that coroutine doesn't run until the card has settled
    // with its new owner. So between "your selection / play committed" and "the card finished moving",
    // focusedCard still names a card you already dealt with — spam Enter in that gap and we'd keep
    // repeating an objective you've met. (Observed: picking the energy out of Night Stretcher's
    // discard-pile selection.) Three game-owned conditions say the board isn't ready to be acted on:
    //   • a battle menu is open — the card lives inside it, and AccessibleBattlePrompt owns it;
    //   • the match is still resolving — PerformingAnimations(), which MatchReady makes true for
    //     an operation still in flight (MatchInfo.sendingMsg);
    //   • the card isn't idle with the owner its server position claims — Card3D's own test.
    private Card3D? FtueFocusedCard()
    {
        var card = RawFtueFocusedCard();
        if (card == null) return null;
        if (IsAnyBattleMenuOpen()) return null;
        if (MatchNetworkParser.PerformingAnimations()) return null;
        return IsSettledInOwner(card) ? card : null;
    }

    // Does the FTUE permit acting on this card right now? Calls the permit the game itself installed
    // on THIS match's targeting handler (FTUEMatchManager.Start → SecondaryIsCardTargetable =
    // DoesFTUEPermitCardTargetability): true in free play, true for the spotlighted card during a
    // forced step; false for a non-spotlighted card or a not-yet-armed step. No delegate installed
    // means the FTUE does not govern this match — never impose tutorial rules there. (The old gate
    // read the GLOBAL lesson flag + the always-present FTUEMatchManager and reflected the private
    // method, which locked a ranked player out of every action with "the tutorial isn't ready for
    // that yet" when a parked/always-running lesson kept the flag true — field report, 2026-07-23.)
    private bool FtuePermitsCard(Card3D card)
    {
        if (card == null) return true;
        var permit = FtueTracker.MatchFtuePermit(m_manager);
        if (permit == null) return true;
        try { return permit(card); }
        catch (Exception ex) { Log.Error($"Battle: FTUE permit threw: {ex.Message}"); return true; }
    }

    // On each new coaching step (the FTUE spotlights a different card), announce which card it wants
    // and where — WITHOUT moving the player's focus (jumping the cursor is disorienting). Fires only
    // when the spotlight changes; navigating between steps stays entirely up to the player.
    private void TutorialGuidanceTick()
    {
        // No spotlight at all → the step is genuinely over; forget it so the next one announces.
        if (RawFtueFocusedCard() == null) { m_ftueCardId = ""; return; }
        // Spotlit, but the board isn't ready to be acted on (menu open / still resolving / card in
        // flight). Say nothing and remember nothing: if it's still wanted once things settle, we
        // announce it then — and if the player's action already satisfied it, focusedCard clears first
        // and we never speak a stale objective.
        var ftue = FtueFocusedCard();
        if (ftue == null || ftue.entityID == m_ftueCardId) return;
        m_ftueCardId = ftue.entityID;
        Say(TutorialWantsText(ftue), interrupt: false);
    }

    private bool IsAnyBattleMenuOpen()
    {
        try { return m_manager != null && m_manager.MenuNavigation != null && m_manager.MenuNavigation.IsAnyMenuOpen(); }
        catch { return false; }
    }

    // Unified "Tutorial wants <card>, in <zone>" phrasing — shared by the spotlight announce, the
    // wrong-card action guard, and the end-turn block so the player always hears the same objective.
    private string TutorialWantsText(Card3D ftue)
    {
        var zone = ZoneOfCard(ftue);
        return zone != null
            ? LocalizationUtils.Format(LocalizationKey.BATTLE_TUTORIAL_WANTS_IN, BattleBoardReader.CardName(ftue), BattleBoardReader.ZoneName(zone.Value))
            : LocalizationUtils.Format(LocalizationKey.BATTLE_TUTORIAL_WANTS, BattleBoardReader.CardName(ftue));
    }

    // Where the card IS, for a player who has to walk the board to reach it. `cardInfo.currentPos` is
    // SERVER state and lags reality twice over: while an operation is in flight the card still reports
    // the zone it left, and a card being shown inside a selection overlay (Night Stretcher's "pick a
    // Pokémon from your discard pile") reports DISCARD while its real owner is the overlay's
    // CardSelectionSource. Naming a zone in either case points the player at somewhere they can't act.
    //
    // The game already defines "settled where the server says it is" — Card3D.WaitUntilCardIsIdleInOwner
    // waits on exactly `owner == GetOwnerForBoardPos(cardInfo.currentPos)`. Borrow that test; when it
    // fails there is no board zone to name, so callers drop the clause rather than guess.
    private BattleBoardReader.Zone? ZoneOfCard(Card3D c)
    {
        var info = c?.cardInfo;
        if (info == null || !IsSettledInOwner(c!)) return null;
        var pp = Board.GetPlayerPos(info.currentPos);
        bool mine = Board.GetPlayerI(info.currentPos) == PlayerID.LOCAL;
        return pp switch
        {
            PlayerPos.ACTIVE => mine ? BattleBoardReader.Zone.MyActive : BattleBoardReader.Zone.OppActive,
            PlayerPos.BENCH => mine ? BattleBoardReader.Zone.MyBench : BattleBoardReader.Zone.OppBench,
            PlayerPos.HAND => mine ? BattleBoardReader.Zone.MyHand : (BattleBoardReader.Zone?)null,
            PlayerPos.DISCARD => mine ? BattleBoardReader.Zone.MyDiscard : BattleBoardReader.Zone.OppDiscard,
            PlayerPos.STADIUM => BattleBoardReader.Zone.Stadium,
            _ => null,
        };
    }

    // The game's own idle-in-owner test (Card3D.WaitUntilCardIsIdleInOwner). ICardOwner is an
    // interface, so != / ReferenceEquals is plain reference comparison, same as the game does.
    private bool IsSettledInOwner(Card3D c)
    {
        var info = c.cardInfo;
        if (info == null || m_manager == null) return false;
        try { return ReferenceEquals(c.owner, m_manager.GetOwnerForBoardPos(info.currentPos)); }
        catch { return false; }
    }

    #endregion

    private static string? PhaseText(MatchManager.MatchState state) => state switch
    {
        MatchManager.MatchState.Setup => LocalizationUtils.Get(LocalizationKey.BATTLE_PHASE_SETUP),
        MatchManager.MatchState.LocalTurn => LocalizationUtils.Get(LocalizationKey.BATTLE_PHASE_YOUR_TURN),
        MatchManager.MatchState.OpponentTurn => LocalizationUtils.Get(LocalizationKey.BATTLE_PHASE_OPPONENT_TURN),
        _ => null,
    };
}
