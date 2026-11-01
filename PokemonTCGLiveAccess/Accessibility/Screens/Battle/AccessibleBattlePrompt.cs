using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using CardDatabase.DataAccess;
using HarmonyLib;
using MatchLogic;
using RainierClientSDK;
using TPCI.Localization;
using TPCI.Rainier.Match.Cards;
using TPCI.Rainier.Match.Selection.Constraints;
using TPCI.Rainier.Match.Cards.Ownership;
using UnityEngine;
using UnityEngine.UI;
using static Accessibility.GameReflect;

namespace Accessibility;

// Handles the game's mid-action DECISION PROMPTS (take a prize, coin call, yes/no, mulligan, …).
// Every local prompt funnels through SelectionOverlayOpener.RunSelectionRequest (which is
// intrinsically local-only — the opponent/bot uses a separate message-bar path), so we postfix it,
// read the question + options off the game's own overlay, let the player arrow through and choose,
// and submit through the overlay's OWN methods. Closing is detected by polling the overlay.
//
// This UI goes over AccessibleBattleScreen (pausing board nav) and hides when the prompt closes. Handled
// families: PRIZE, TEXT (coin/quiz/number/yes-no/mulligan), entity selections (hand/pile/attached/
// board-attached), BOARD, DragActive, ReparentDamage and ACTION (choose an attack to use as this attack).
// TEXT and ACTION draw in a modal drawer this UI owns: see OwnsModal.
// Attach distribution is a board drag, routed to AccessibleBattleScreen. A family with no builder still
// announces its question, but leaves the player with nothing to press — so any new one must be wired up here.
internal sealed class AccessibleBattlePrompt : AccessibleUI
{
    private static readonly AccessibleBattlePrompt s_instance = new();

    private bool m_active;
    // The prompt opened while another UI (a tutorial professor dialogue) had focus — see OnPromptOpened.
    private bool m_pendingShow;

    // One navigable choice. Eid is the entity-selection card id (so the label can show ", selected"
    // by reading the game's own SelectionGroup.selectedOptions); null for prize / text options.
    // Card is the resolved Card3D for prize options (their selected-state lives in a private list,
    // not a SelectionGroup, so ", selected" is read by membership). IsDone marks the synthetic
    // "Done" entry that submits a multi-pick selection.
    private sealed class Option
    {
        public string Name = "";
        public Action Choose = () => { };
        public string? Eid;
        public Card3D? Card;
        public bool IsDone;
        // The Pokémon this card is attached to (energy-discard menus). Spoken as a SECTION NAME — only
        // when it differs from the previously-focused option — so consecutive energy on the same Pokémon
        // doesn't repeat it. Null for cards with no parent (prizes, hand/deck cards).
        public string? Parent;
        // For a COLLAPSED carousel/pile stack (e.g. 18 identical Basic Fire Energy shown as one pile):
        // the entityIDs in the pile, used for the live available/selected counts. Null = not a stack.
        public List<string>? StackEids;
        // Shown in the carousel but not choosable (EntitySelection.allOptions minus allValidOptions): the
        // player reads them the way a sighted player sees them greyed out.
        public bool ReadOnly;
    }

    private ISelectionRequestHandler? m_handler;   // the open overlay (also a BattleMenu)
    private string m_question = "";
    private readonly List<Option> m_options = new();
    private string? m_lastSpokenParent;  // last announced section (parent Pokémon) — see SpeakOption
    private int m_idx;
    private int m_line = -1;        // detail-line cursor on the focused card option (-1 = on the header/name)
    private List<string>? m_lines;  // cached detail lines for the focused option; null until first Up/Down resolves it
    private bool m_submitted;
    private int m_target;
    private SelectionGroup? m_entityGroup;
    private EntitySelection? m_entitySel;
    private EntitySelectionOverlay? m_entityOverlay;
    private BoardAttachedCardSelectionMenu? m_bacsMenu;   // multi-parent attached select (distribute-then-select)
    private bool m_entityClickMenu;   // board-attached / attached: select the specific board card by entityID
    private int m_entityMax;
    // Board-attached deferred select: after we distribute an energy's parent Pokémon (a native click on
    // it), the energy only becomes selectable once it settles into the distribution carousel — so we
    // finish the select over the next ticks. eid of the energy we're waiting to select, "" when idle.
    private string m_pendingSelectEid = "";
    // A press that arrived before the card it targets had settled. Re-armed each frame from HandleInput until
    // the game says the card is clickable — see IsSettledForClick. Never dropped: the player pressed Enter.
    private Action? m_pendingChoose;
    private string m_pendingSelectParent = "";   // the energy's parent Pokémon we must "distribute" (open) to reach it
    private int m_pendingSelectTicks;
    private bool m_boardAutoSubmit;   // the menu submits on the single click itself (see BuildBoardOptions)
    private PrizeCardSelectionMenu? m_prizeMenu;
    private int m_prizeMin;
    private int m_prizeMax;
    private BoardSelectionMenu? m_boardMenu;
    private SelectionGroup? m_boardGroup;
    private ActivePromotionDragMenu? m_dragMenu;
    private SelectionGroup? m_dragGroup;
    private ReparentDamageMenu? m_damageMenu;
    private DamageSelection? m_damageSel;
    // The MENU'S OWN constraint evaluator, read by reflection — never constructed. Its constructor runs
    // InitializeFinalDamageToCurrent, which resets newDamageCounters = currentDamageCounters on the SHARED
    // DamageSelectionFinal objects — erasing every drop already recorded on the payload the game submits.
    // Constructing "read-only copies" per drop is exactly how Phantom Dive / Adrena-Brain lost counters:
    // visuals showed every drop, but SelectInput sent only the last one.
    private ReparentDamageConstraintEvaluator? m_damageEval;
    private string? m_damageSource;    // MOVE: the source Pokémon currently distributing (null = pick-source phase, or put-new-damage)
    private bool m_damageSourceStarted; // have we lifted the chosen source's counters into the tray yet (first-drop trigger)
    private string m_damageTargetEid = "";
    private bool m_damageDropLanded;   // ReactToSuccessfulPlacement ran for the armed drop (discriminates silent refusal)
    private bool m_damageDropInFlight; // an armed drop whose placement loop hasn't reported back yet
    private Action? m_damageRetry;     // a drop held until the game is ready — completed by the coach-focus /
                                       // unblock / server-confirm EVENTS below, never by frame counting
    private bool m_damagePlacementEndedHooked;   // OnPlacementEvaluationEnded subscribed for this prompt
    private readonly KbPlacementInitiator m_damageInit = new();   // explicit-commit placement for a damage counter → target
    private MatchManager? m_mgr;

    #region Hooks

    [HarmonyPatch(typeof(SelectionOverlayOpener), "RunSelectionRequest")]
    private static class RunSelectionRequestHook
    {
        private static void Postfix(PlayerSelection selectionRequest, ISelectionRequestHandler __result)
        {
            if (selectionRequest == null) return;
            try { s_instance.OnPromptOpened(selectionRequest, __result); }
            catch (Exception ex) { Log.Error($"BattlePrompt.OnPromptOpened threw: {ex.Message}"); }
        }
    }

    // The game's own "a damage counter just landed" callback — announce and rebuild HERE, the same
    // moment the game itself refreshes its click overrides. No flag, no next-frame deferral.
    // Runs inside the game's placement callback (main thread) the instant a counter is applied. Everything
    // is try-wrapped — a throw here would unwind into the match's own stack.
    [HarmonyPatch(typeof(ReparentDamageMenu), "ReactToSuccessfulPlacement")]
    private static class ReactToSuccessfulPlacementHook
    {
        private static void Postfix(ReparentDamageMenu __instance, ICardPlacementReceiver receiver)
        {
            var s = s_instance;
            if (!ReferenceEquals(__instance, s.m_damageMenu)) return;
            s.m_damageDropLanded = true;
            s.m_damageDropInFlight = false;
            try { s.AfterDamageDropApplied(receiver); }
            catch (Exception ex) { Log.Error($"BattlePrompt: damage drop follow-up threw: {ex.Message}"); }
        }
    }

    // Tutorial drag coach (FTUEDragDamage): DragDamageBlockCards is the exact moment the coach focuses
    // the tray counter it wants dropped — a drop the player already asked for completes right here.
    [HarmonyPatch(typeof(FTUEMatchManager), "DragDamageBlockCards")]
    private static class DragDamageBlockCardsHook
    {
        private static void Postfix()
        {
            if (s_instance.m_damageMenu == null) return;
            s_instance.CompleteDamageRetry();
        }
    }

    // The focused counter becomes grabbable when its FocusCard coroutine clears blockDrag —
    // FocusBlockDrag(false) is that exact moment. Completes a drop held on targetability.
    [HarmonyPatch(typeof(Card3D), "FocusBlockDrag")]
    private static class FocusBlockDragHook
    {
        private static void Postfix(Card3D __instance, bool state)
        {
            var s = s_instance;
            if (state || s.m_damageMenu == null || s.m_damageRetry == null) return;
            bool isTrayCounter;
            try { isTrayCounter = s.TrayCounters().Contains(__instance); } catch { return; }
            if (isTrayCounter) s.CompleteDamageRetry();   // the game just freed the counter for grabbing
        }
    }

    // Repeatable / unlimited damage effects: after each drop the menu locks itself and waits for the
    // server echo (isWaitingForUpdate); UnlimitedConfirm is the game re-opening for the next counter.
    [HarmonyPatch(typeof(ReparentDamageMenu), "UnlimitedConfirm")]
    private static class UnlimitedConfirmHook
    {
        private static void Postfix(ReparentDamageMenu __instance)
        {
            var s = s_instance;
            if (!ReferenceEquals(__instance, s.m_damageMenu)) return;
            try
            {
                if (!s.IsPromptOpen()) return;
                s.RebuildDamageOptions();
                s.m_idx = 0; s.m_line = -1; s.m_lines = null;
                s.Say(LocalizationUtils.Get(LocalizationKey.BATTLE_PROMPT_NEXT_COUNTER));
                if (s.m_options.Count > 0) s.SpeakOption(s.m_idx, interrupt: false);
            }
            catch (Exception ex) { Log.Error($"BattlePrompt: unlimited-confirm follow-up threw: {ex.Message}"); }
        }
    }

    // During the tutorial a professor dialogue can still be mid-sentence when a selection opens (e.g.
    // Hilda's "put an Evolution Pokémon and an Energy into your hand" step). The FTUE parks on that
    // dialogue and only ARMS the selection — makes the cards targetable, via focusedCard or free play —
    // once the player advances it. Stealing focus from it buries the dialogue so it can never be advanced,
    // and the FTUE's DoesFTUEPermitCardTargetability then blocks every card (observed 2026-07-11: acted=False
    // on every pick, and the dialogue was never resumed). So wait BELOW it: the dialogue stays the focused
    // thing the player can advance, and the prompt shows (and announces) once no other UI has focus.
    // ponytail: poll, because AccessibilityMgr has no "show below the top UI"; one field read per frame.
    [AccessibilityPoller]
    private static void PendingShowPoller()
    {
        var s = s_instance;
        if (!s.m_pendingShow) return;
        if (!s.IsPromptOpen()) { s.m_pendingShow = false; s.Close(); return; }
        if (AccessibilityMgr.Focused is AccessibleUI) return;
        s.m_pendingShow = false;
        AccessibilityMgr.ShowUI(s);
    }

    private void OnPromptOpened(PlayerSelection sel, ISelectionRequestHandler? handler)
    {
        // Attach-distribution ("attach energy onto a Pokémon") is a placement, not a list — so no mod
        // overlay. Hand it to the battle screen: the game lifts the energies into a temporary hand, and
        // the screen points C (your hand) at THAT temp hand, so you pick the energy, press B, and drop
        // it on the Pokémon — the same native board drag as attaching on your turn. Tab = Done.
        if (handler is AttachmentDistributionSelectionMenu attachMenu)
        {
            string q = "";
            try { q = attachMenu.DynamicStringProvider.UpdateSelectionLocStrings(sel.variableSelection.GetLocId(), sel.variableSelection.locParams) ?? ""; }
            catch { }
            AccessibleBattleScreen.BeginAttachSelection(attachMenu, q);
            return;
        }

        BuildPrompt(sel, handler);

        // A recognized card selection that opened with NO valid options — e.g. a deck search (Nest
        // Ball) when nothing in the deck matches, or "choose a card to discard" with an empty pile.
        // The game still opens the overlay with a "no valid cards" notice + a Done button; speak that
        // message and offer a Done that submits the empty selection (exactly what clicking Done does).
        // Without this the player heard "isn't supported yet" and had no way to dismiss the overlay.
        if (IsEmptyCardSelection(sel, handler))
        {
            // The game's own "no valid cards" message. We use the plain key, not the "...press done" variant —
            // the Done is presented as a real navigable option, not spoken as a control hint.
            var msg = LocalizationUtils.GetGame("battle_select_no_valid_cards", LocalizationKey.BATTLE_PROMPT_NO_VALID_CARDS);
            m_question = string.IsNullOrWhiteSpace(m_question) ? msg : LocalizationUtils.Format(LocalizationKey.BATTLE_SENTENCES, m_question, msg);
            m_options.RemoveAll(o => o.IsDone);
            m_options.Add(new Option { Name = LocalizationUtils.Get(LocalizationKey.BATTLE_DONE), IsDone = true, Choose = () => DismissEmptySelection(handler) });
        }

        if (m_options.Count == 0)
        {
            // A selection family we don't drive yet. Announce the question so the player knows a
            // choice is pending — the battle screen stays focused, but its actions are gated on the live
            // selection state (its m_localSelecting), so this can't soft-lock the match.
            // The diagnostics logged in BuildPrompt capture exactly what to wire up next.
            if (m_active) Close();   // a replaced prompt must not leave this UI up on an empty list
            string q = string.IsNullOrWhiteSpace(m_question) ? LocalizationUtils.Get(LocalizationKey.BATTLE_PROMPT_CHOICE_REQUIRED) : m_question;
            AccessibilityMgr.OutputNotification(LocalizationUtils.Format(LocalizationKey.BATTLE_PROMPT_UNSUPPORTED, q));
            return;
        }

        if (!m_active)
        {
            m_active = true;
            if (AccessibilityMgr.Focused is AccessibleUI) m_pendingShow = true;   // see PendingShowPoller
            else AccessibilityMgr.ShowUI(this);
        }
        else if (AccessibilityMgr.IsFocused(this)) AnnouncePrompt();   // a new prompt replaced the previous one without closing
    }

    private void BuildPrompt(PlayerSelection sel, ISelectionRequestHandler? handler)
    {
        m_handler = handler;
        m_options.Clear();
        m_lastSpokenParent = null;
        m_idx = 0;
        m_line = -1;
        m_lines = null;
        m_submitted = false;
        m_target = 0;
        m_entityGroup = null;
        m_entitySel = null;
        m_entityOverlay = null;
        m_bacsMenu = null;
        m_entityClickMenu = false;
        m_entityMax = 0;
        m_boardAutoSubmit = false;
        m_pendingSelectEid = "";
        m_pendingSelectParent = "";
        m_pendingSelectTicks = 0;
        m_pendingChoose = null;
        m_prizeMenu = null;
        m_prizeMin = 0;
        m_prizeMax = 0;
        m_boardMenu = null;
        m_boardGroup = null;
        m_dragMenu = null;
        m_dragGroup = null;
        UnhookDamagePlacementEnded();
        m_damageMenu = null;
        m_damageSel = null;
        m_damageEval = null;
        m_damageSource = null;
        m_damageSourceStarted = false;
        m_damageDropLanded = false;
        m_damageDropInFlight = false;
        m_damageRetry = null;
        m_damageTargetEid = "";
        m_damageInit.Reset();
        m_mgr = MatchManager.GetInstanceFromScene();
        m_question = "";

        // The localized prompt text the overlay itself shows (works for every BattleMenu family).
        if (handler is BattleMenu menu)
        {
            try { m_question = menu.DynamicStringProvider.UpdateSelectionLocStrings(sel.variableSelection.GetLocId(), sel.variableSelection.locParams) ?? ""; }
            catch { m_question = ""; }
        }

        if (handler is PrizeCardSelectionMenu prize) BuildPrizeOptions(prize);
        else if (handler is TextSelectionOverlay text) BuildTextOptions(text, sel);
        else if (handler is EntitySelectionOverlay entity && sel.variableSelection is EntitySelection esel) BuildEntityOptions(entity, esel);
        else if (handler is BoardSelectionMenu board && sel.variableSelection is EntitySelection besel) BuildBoardOptions(board, besel);
        else if (handler is ActivePromotionDragMenu drag && sel.variableSelection is EntitySelection dsel) BuildActiveDragOptions(drag, dsel);
        else if (handler is ReparentDamageMenu dmg && sel.variableSelection is DamageSelection dmgSel) BuildDamageOptions(dmg, dmgSel);
        else if (handler is ActionSelectionOverlay act && sel.variableSelection is ActionSelection actSel) BuildActionOptions(act, actSel, sel);

        LogPromptDiagnostics(sel, handler);
    }

    // The Text and Action families draw their options in a modal drawer. This UI owns those decisions, so
    // the generic modal UI must not also take focus and speak the same drawer (the double announcement, and
    // the focus fight). The drawers are the prefab's, one per overlay, so they are asked for live. Every
    // other modal in a match (confirmations, submission errors, the reveal grid) stays the modal UI's.
    internal static bool OwnsModal(ModalElementContainer modal)
    {
        if (AccessibleBattleScreen.CurrentManager == null) return false;
        var opener = MonoSingleton<SelectionOverlayOpener>.instance;
        if (opener == null) return false;
        var text = opener.GetBattleMenu(VariableSelectionSettings.SelectionMethod.Text) as TextSelectionOverlay;
        if (text != null && ReferenceEquals(text.GetAssociatedModalBuilder()?.Drawer, modal)) return true;
        return ReferenceEquals(ActionDrawer(opener.GetBattleMenu(VariableSelectionSettings.SelectionMethod.Action) as PlayerSelectionOverlay), modal);
    }

    private static ModalElementContainer? ActionDrawer(PlayerSelectionOverlay? overlay) =>
        ReadField<ModalDrawerBuilder>(ReadField<PlayerSelectionOverlayCustomView>(overlay, "customViewProvider") as ActionSelectionOverlayCustomView_H, "overlayPopup")?.Drawer;

    // MatchAnnouncements skips a message bar that only repeats what this prompt already said.
    internal static bool IsCurrentQuestion(string text) =>
        s_instance.m_active && s_instance.m_question.IndexOf(text, StringComparison.Ordinal) >= 0;

    // Record what every selection request looked like and whether we built options. Families we don't
    // drive yet build 0 options (the battle screen stays focused — gated so it can't soft-lock); this line
    // tells us the exact overlay + selection method + group shape to wire up next (e.g. the Ultra Ball
    // deck-search). Cheap, fires once per prompt.
    private void LogPromptDiagnostics(PlayerSelection sel, ISelectionRequestHandler? handler)
    {
        try
        {
            var vs = sel.variableSelection;
            string groups = "n/a";
            string extra = "";
            if (vs is EntitySelection es && es.selectionGroups != null)
            {
                groups = "[" + string.Join(", ", es.selectionGroups.Select(g => $"{g?.validOptions?.Count ?? -1} valid / max {g?.maxAmount} / min {g?.minAmount}")) + "]";
                // Log totals + each option's card name and the PARENT Pokémon it's attached to — this is
                // what tells us a discard like "choose any Fire Energy" spans several Pokémon (so the
                // options must be labelled by parent), not just the active.
                var opts = es.selectionGroups.Where(g => g != null).SelectMany(g => g.validOptions)
                    .Select(o => $"{ResolveName(o.cardAddress.entityID)}@{(string.IsNullOrEmpty(o.parentEntityID) ? "-" : ResolveName(o.parentEntityID))}");
                extra = $" total={es.totalMinAmount}/{es.totalMaxAmount} opts=[{string.Join(", ", opts)}]";
            }
            else if (vs is ReparentSelection rs)
                groups = $"reparentOptions={rs.reparentOptions?.Count ?? -1} min/max={rs.minReparentedOptions}/{rs.maxReparentedOptions} unlimited={rs.unlimitedReparent}";
            else if (vs is DamageSelection dsg)
                groups = $"to={dsg.toDamageSelections?.Count ?? -1} from={dsg.fromDamageSelections?.Count ?? -1} movable={dsg.minMovableDamageTotal}/{dsg.maxMovableDamageTotal} perTarget={dsg.minRecvDamageSingle}/{dsg.maxRecvDamageSingle} targets={dsg.minTargetSelectionCount}/{dsg.maxTargetSelectionCount}";
            else if (vs is ActionSelection asg)
            {
                var acts = asg.options ?? new List<ActionOption>();
                groups = $"actions={acts.Count} energyFill={asg.energyFillDisplayMode} opts=[{string.Join(", ", acts.Select(o => $"{o.actionSourceID}@{ResolveName(o.parentEntityID)}{(o.isSelectable ? "" : " (not selectable)")}"))}]";
            }
            Log.Info($"BattlePrompt: handler={handler?.GetType().Name ?? "null"} method={vs?.selectionMethod} selType={vs?.GetType().Name} groups={groups}{extra} built={m_options.Count} q='{m_question}'");
        }
        catch (Exception ex) { Log.Error($"BattlePrompt: diagnostics threw: {ex.Message}"); }
    }

    #endregion

    #region No valid targets

    // A recognized card-choosing overlay that has no selectable options at all (every group's
    // validOptions is empty) — the game opens it anyway with a "no valid cards" notice + Done.
    private static bool IsEmptyCardSelection(PlayerSelection sel, ISelectionRequestHandler? handler)
    {
        if (handler is not EntitySelectionOverlay) return false;
        if (sel.variableSelection is not EntitySelection es || es.selectionGroups == null) return false;
        return es.selectionGroups.All(g => g?.validOptions == null || g.validOptions.Count == 0);
    }

    // Dismiss an empty card selection by submitting it with nothing chosen — the overlay's own
    // SubmitClicked, the same call its Done button makes (minAmount is 0 for these, so it completes).
    private void DismissEmptySelection(ISelectionRequestHandler? handler)
    {
        m_submitted = true;
        try { (handler as EntitySelectionOverlay)?.SubmitClicked(); }
        catch (Exception ex) { Log.Error($"BattlePrompt: dismiss empty selection threw: {ex.Message}"); }
    }

    #endregion

    #region Prize

    private static readonly MethodInfo? s_tryMarkPrize = AccessTools.Method(typeof(PrizeCardSelectionMenu), "TryMarkCardSelected");

    private void BuildPrizeOptions(PrizeCardSelectionMenu prize)
    {
        var group = prize.OnlyGroup;
        if (group == null) return;
        m_prizeMenu = prize;
        var owner = ReadPrizeOwner(prize);
        var opts = group.validOptions;
        int n = opts.Count;
        m_prizeMax = group.maxAmount < 1 ? 1 : group.maxAmount;
        // Mirror the menu's own _minPrizesToSelect: minAmount unless it's -1 (= "exactly max").
        m_prizeMin = group.minAmount != -1 ? group.minAmount : m_prizeMax;
        m_target = m_prizeMax;
        for (int i = 0; i < n; i++)
        {
            int idx = i;
            var card = owner?.GetPrivateCardAtIndex(opts[idx].cardAddress.index);
            m_options.Add(new Option { Name = LocalizationUtils.Get(LocalizationKey.BATTLE_PROMPT_PRIZE_CARD), Card = card, Choose = () => MarkPrize(prize, idx) });
        }
        // A multi / variable take gets an explicit Done; single forced takes auto-submit on the pick.
        if (m_prizeMax > 1) m_options.Add(new Option { Name = LocalizationUtils.Get(LocalizationKey.BATTLE_DONE), IsDone = true, Choose = () => SubmitPrize(prize) });
        if (string.IsNullOrWhiteSpace(m_question))
            m_question = m_prizeMax > 1
                ? LocalizationUtils.Format(LocalizationKey.BATTLE_PROMPT_TAKE_PRIZES, m_prizeMax)
                : LocalizationUtils.Get(LocalizationKey.BATTLE_PROMPT_TAKE_PRIZE);
    }

    // Toggle a prize via the card's OWN click override — the exact sighted click. The menu wires each
    // valid prize with a Select override (TryMarkCardSelected); once a card is selected its override
    // swaps to Deselect (MarkCardDeselected), so a second press DESELECTS instead of re-adding. That
    // fixes the freeze where calling TryMarkCardSelected directly added the same card to _selectedPrizes
    // twice (duplicate → bad submit / soft-lock). The count is read from the menu's own _selectedPrizes.
    private void MarkPrize(PrizeCardSelectionMenu prize, int optionIndex)
    {
        var group = prize.OnlyGroup;
        if (group == null || optionIndex < 0 || optionIndex >= group.validOptions.Count) return;

        var owner = ReadPrizeOwner(prize);
        var card = owner?.GetPrivateCardAtIndex(group.validOptions[optionIndex].cardAddress.index);
        if (card == null) { Say(LocalizationUtils.Get(LocalizationKey.BATTLE_PROMPT_PRIZE_FAILED)); return; }

        int prev = ReadSelectedPrizes(prize).Count;
        var listener = card.Interactions != null ? card.Interactions.OverrideClickListener : null;
        if (listener != null)
        {
            try { listener.OnCardClicked(card, false); }   // routes to Select or Deselect per current state
            catch (Exception ex) { Log.Error($"BattlePrompt prize click threw: {ex.Message}"); }
        }
        else
        {
            // Fallback: the private select directly (only adds when valid + below max).
            try { s_tryMarkPrize?.Invoke(prize, new object[] { card, false }); }
            catch (Exception ex) { Log.Error($"BattlePrompt prize mark threw: {ex.Message}"); }
        }

        int now = ReadSelectedPrizes(prize).Count;

        if (now > prev)
        {
            // A forced exact take (min == max) auto-submits at the count — keeps "take 1" / "take 2"
            // frictionless. A variable take waits for the Done option.
            if (m_prizeMin == m_prizeMax && now >= m_prizeMax) { m_submitted = true; prize.SubmitSelection(); return; }
            Say(now < m_prizeMax
                ? LocalizationUtils.Format(LocalizationKey.BATTLE_PROMPT_PRIZE_SELECTED_MORE, now, m_prizeMax, m_prizeMax - now)
                : LocalizationUtils.Format(LocalizationKey.BATTLE_PROMPT_PRIZE_SELECTED, now, m_prizeMax));
        }
        else if (now < prev)
        {
            Say(LocalizationUtils.Format(LocalizationKey.BATTLE_PROMPT_PRIZE_DESELECTED, now));
        }
        else
        {
            Say(LocalizationUtils.Get(now >= m_prizeMax ? LocalizationKey.BATTLE_PROMPT_PRIZE_MAX : LocalizationKey.BATTLE_PROMPT_PRIZE_FAILED));
        }
    }

    // Submit a multi / variable prize take via the menu's own SubmitSelection, once the count is in range.
    private void SubmitPrize(PrizeCardSelectionMenu prize)
    {
        int now = ReadSelectedPrizes(prize).Count;
        if (now < m_prizeMin) { Say(LocalizationUtils.Format(LocalizationKey.BATTLE_PROMPT_CHOOSE_AT_LEAST, m_prizeMin, now)); return; }
        if (now > m_prizeMax) { Say(LocalizationUtils.Get(LocalizationKey.BATTLE_PROMPT_TOO_MANY)); return; }
        m_submitted = true;
        prize.SubmitSelection();
    }

    private static PrizeController? ReadPrizeOwner(PrizeCardSelectionMenu prize) => ReadField<PrizeController>(prize, "_currentPrizeOwner");

    private static List<Card3D> ReadSelectedPrizes(PrizeCardSelectionMenu prize) =>
        ReadField<List<Card3D>>(prize, "_selectedPrizes") ?? new List<Card3D>();

    #endregion

    #region Entity selection (hand / pile / attached)
    // Discard N, search, choose card(s) from hand or a pile.

    private void BuildEntityOptions(EntitySelectionOverlay overlay, EntitySelection esel)
    {
        if (esel.selectionGroups.Count == 0) return;
        m_entityOverlay = overlay;
        m_entitySel = esel;
        m_entityGroup = esel.selectionGroups[0];   // kept for the data-fallback path
        // Board-attached / attached selections (e.g. "discard Fire Energy off your Pokémon") move the
        // SPECIFIC public board card; resolve those by entityID so re-selecting toggles that exact card.
        // Carousel/hand overlays (deck search, discard-from-hand) keep the source-owner / FTUE-focused
        // resolution. (See MarkEntity.)
        m_bacsMenu = overlay as BoardAttachedCardSelectionMenu;   // the multi-parent kind needs distribute-first
        m_entityClickMenu = m_bacsMenu != null || overlay is AttachedCardSelectionMenu;
        // Totals across ALL groups — multi-group prompts ("1 Pokémon OR 1 Basic Energy", Night Stretcher)
        // have one group per kind; the chosen card lands in whichever group accepts it.
        int entityMin = esel.totalMinAmount < 0 ? 0 : esel.totalMinAmount;
        m_entityMax = esel.totalMaxAmount < 1 ? 1 : esel.totalMaxAmount;
        m_target = m_entityMax;
        // Build the card options to match what a sighted player sees:
        if (m_entityClickMenu && m_bacsMenu == null)
        {
            // Plain AttachedCardSelectionMenu ("Discard any Fire Energy from among your Pokémon", e.g.
            // Mega Charizard X's Inferno X). The energy is lifted into a temp HAND (ReparentHandController)
            // that STACKS identical cards PER POKÉMON — only each stack's FRONT is targetable. So collapse
            // the energy into one option per (name, parent Pokémon) — the "each Pokémon gets its own stack"
            // model — and peel the front on each press (MarkAttachedHandStack). The old per-card path
            // (GetCard(eid)+TryTargetCard on every buried energy) failed for all but the one card that
            // happened to be the front (3 of 4 acted=False), so only one energy could ever be discarded.
            var stacks = new Dictionary<(string name, string parent), List<string>>();
            var order = new List<(string name, string parent)>();
            foreach (var group in esel.selectionGroups)
            {
                if (group == null) continue;
                foreach (var opt in group.validOptions)
                {
                    string eid = opt.cardAddress.entityID;
                    string parentEid = opt.parentEntityID ?? "";
                    string name = ResolveName(eid);
                    var key = (name, parentEid);
                    if (!stacks.TryGetValue(key, out var eids)) { eids = new List<string>(); stacks[key] = eids; order.Add(key); }
                    eids.Add(eid);
                }
            }
            // Order by parent so each Pokémon's energy reads consecutively (parent = spoken section name).
            foreach (var key in order.OrderBy(k => k.parent, StringComparer.Ordinal))
            {
                var (name, parentEid) = key;
                string? parentName = !string.IsNullOrEmpty(parentEid) ? ResolveParentName(parentEid) : null;
                var eids = stacks[key];
                m_options.Add(new Option { Name = name, Parent = parentName, Eid = eids[0], StackEids = eids, Choose = () => MarkAttachedHandStack(name, parentEid, eids) });
            }
        }
        else if (m_entityClickMenu)
        {
            // BoardAttachedCardSelectionMenu (distribute one Pokémon at a time): ONE option per energy,
            // tagged with its PARENT Pokémon (spoken as a section name, see SpeakOption) and grouped by
            // parent. Selection defers until the parent is the open one (MarkEntity → TickPendingEnergySelect).
            var cardOpts = new List<(string sortKey, Option opt)>();
            foreach (var group in esel.selectionGroups)
            {
                if (group == null) continue;
                foreach (var opt in group.validOptions)
                {
                    var addr = opt.cardAddress;             // copy per-iteration so the lambda captures the right one
                    string parentEid = opt.parentEntityID;  // the Pokémon this card is attached to (empty off-board)
                    string cardName = ResolveName(addr.entityID);
                    string? parentName = !string.IsNullOrEmpty(parentEid) ? ResolveParentName(parentEid) : null;
                    cardOpts.Add((parentEid ?? "", new Option { Name = cardName, Parent = parentName, Eid = addr.entityID, Choose = () => MarkEntity(addr, cardName, parentEid) }));
                }
            }
            foreach (var c in cardOpts.OrderBy(c => c.sortKey, StringComparer.Ordinal)) m_options.Add(c.opt);
        }
        else
        {
            // Carousel / pile / hand (deck search, discard-from-hand): the overlay STACKS identical cards
            // into one pile with a count badge (e.g. 18 Basic Fire Energy). Mirror that — COLLAPSE
            // identical cards (same displayed name) into ONE option carrying the pile's entityIDs
            // (StackEids, for the live count). Selecting it peels the current targetable stack top
            // (MarkStackOption), so a big pile reads as one entry and there's no stale-id problem as it
            // shrinks. First-seen order is preserved.
            var stacks = new Dictionary<string, List<string>>();
            var order = new List<string>();
            foreach (var group in esel.selectionGroups)
            {
                if (group == null) continue;
                foreach (var opt in group.validOptions)
                {
                    string eid = opt.cardAddress.entityID;
                    string name = ResolveName(eid);
                    if (!stacks.TryGetValue(name, out var eids)) { eids = new List<string>(); stacks[name] = eids; order.Add(name); }
                    eids.Add(eid);
                }
            }
            foreach (var name in order)
                m_options.Add(new Option { Name = name, Eid = stacks[name][0], StackEids = stacks[name], Choose = () => MarkStackOption(name) });
            AddReadOnlyOptions(esel);
        }
        // Multi-pick ("choose any / up to N") needs an explicit Done so the player can stop short of the
        // max and submit the few they chose. Single-pick still auto-submits on the pick (no Done shown).
        // An optional single pick ("you may choose 1") needs it too: without it there was no way to decline.
        if (m_entityMax > 1 || entityMin == 0) m_options.Add(new Option { Name = LocalizationUtils.Get(LocalizationKey.BATTLE_DONE), IsDone = true, Choose = SubmitEntity });
        if (string.IsNullOrWhiteSpace(m_question))
            m_question = m_entityMax > 1
                ? LocalizationUtils.Format(entityMin == m_entityMax ? LocalizationKey.BATTLE_PROMPT_CHOOSE_CARDS : LocalizationKey.BATTLE_PROMPT_CHOOSE_UP_TO_CARDS, m_entityMax)
                : LocalizationUtils.Get(LocalizationKey.BATTLE_PROMPT_CHOOSE_CARD);
        AppendRange(entityMin, m_entityMax);
        if ((esel.variableSelectionSettings as EntitySelectionSettings)?.exclusiveGroupings == true && esel.selectionGroups.Count > 1)
            m_question = LocalizationUtils.Format(LocalizationKey.BATTLE_SENTENCES, m_question, LocalizationUtils.Get(LocalizationKey.BATTLE_PROMPT_EXCLUSIVE_GROUPS));
    }

    // The bounds a sighted player reads off the drawer's "x / y" counter. Nothing to add for "exactly 1".
    private void AppendRange(int min, int max)
    {
        string? range = null;
        if (min == 0) range = LocalizationUtils.Format(LocalizationKey.BATTLE_PROMPT_RANGE_OPTIONAL, max);
        else if (min < max) range = LocalizationUtils.Format(LocalizationKey.BATTLE_PROMPT_RANGE_BETWEEN, min, max);
        else if (max > 1) range = LocalizationUtils.Format(LocalizationKey.BATTLE_PROMPT_RANGE_EXACT, max);
        if (range != null) m_question = LocalizationUtils.Format(LocalizationKey.BATTLE_SENTENCES, m_question, range);
    }

    // Cards the carousel shows greyed out: public ones collapsed by name, hidden ones as one count, so a deck
    // search still tells the player what else is in the pile and how many cards stayed face down.
    private void AddReadOnlyOptions(EntitySelection esel)
    {
        if (esel.allOptions == null) return;
        var valid = new HashSet<string>(esel.AllValidOptionIDs);
        var counts = new Dictionary<string, int>();
        var firstEid = new Dictionary<string, string>();
        int hidden = 0;
        foreach (var addr in esel.allOptions)
        {
            if (valid.Contains(addr.entityID)) continue;
            string name = addr.entityID.StartsWith("PRIVATE", StringComparison.Ordinal) ? "" : BattleBoardReader.EntityName(addr.entityID);
            if (name.Length == 0) { hidden++; continue; }
            counts[name] = counts.TryGetValue(name, out int n) ? n + 1 : 1;
            if (!firstEid.ContainsKey(name)) firstEid[name] = addr.entityID;
        }
        Action refuse = () => Say(LocalizationUtils.Get(LocalizationKey.BATTLE_PROMPT_NOT_SELECTABLE));
        foreach (var kv in counts)
        {
            string body = kv.Value > 1 ? LocalizationUtils.Format(LocalizationKey.BATTLE_PROMPT_PILE, kv.Key, kv.Value) : kv.Key;
            m_options.Add(new Option { Name = LocalizationUtils.Format(LocalizationKey.COMMON_STATE_FORMAT, body, LocalizationUtils.Get(LocalizationKey.BATTLE_PROMPT_NOT_SELECTABLE)), Eid = firstEid[kv.Key], ReadOnly = true, Choose = refuse });
        }
        if (hidden > 0)
            m_options.Add(new Option { Name = LocalizationUtils.Format(LocalizationKey.BATTLE_PROMPT_HIDDEN_CARDS, hidden), ReadOnly = true, Choose = refuse });
    }

    // Backspace on a pile or card: put back one chosen card, through the same click that chose it. The
    // game's own click toggles a selected card back; the pile label's count shows what is left.
    private void DeselectFocused()
    {
        if (m_mgr == null || m_idx < 0 || m_idx >= m_options.Count) return;
        var option = m_options[m_idx];
        var eid = option.StackEids?.LastOrDefault(IsSelectedEid) ?? (option.Eid != null && IsSelectedEid(option.Eid) ? option.Eid : null);
        if (eid == null) { Say(LocalizationUtils.Get(LocalizationKey.BATTLE_PROMPT_NOTHING_SELECTED)); return; }
        var card = ResolveCardForRead(eid) ?? m_mgr.GetCard(eid);
        if (card == null) { Say(LocalizationUtils.Get(LocalizationKey.BATTLE_PROMPT_CHANGE_FAILED)); return; }
        int prev = TotalSelected();
        bool acted = false;
        try
        {
            m_mgr.TargetingHandler.RefreshPlacementContext();
            acted = m_mgr.TargetingHandler.TryTargetCard(ResolveTargetableCard(card), ClickReleaseAutoPlacementInitiator.ReusableInitiator);
        }
        catch (Exception ex) { Log.Error($"BattlePrompt: deselect threw: {ex.Message}"); }
        Log.Info($"BattlePrompt deselect eid={eid} acted={acted} selected={TotalSelected()}");
        AnnounceSelectionResult(prev, option.Name, acted);
    }

    // The attached-to Pokémon's name for a section header, or null if it can't be resolved to a real name.
    private static string? ResolveParentName(string parentEntityID)
    {
        string parent = BattleBoardReader.EntityName(parentEntityID);
        return string.IsNullOrEmpty(parent) ? null : parent;
    }

    // Select a card the FULLY NATIVE way — drive the exact card movement a mouse click makes, NEVER a
    // logical poke. TryTargetCard → AddCardToSelection MOVES the card → Card3D.SetOwnership → UnfocusCard
    // → FTUEMatchManager.UnfocusCard clears focusedCard, which is what lets an FTUE "select N" step
    // advance (its coroutine loops `while (focusedCard != null)`, FTUECardIndicator). The old
    // AddToSelectedOptionsByEntityID data-poke never moved the card, so focusedCard never cleared → the
    // tutorial froze (the Dawn "select 2 Basic Pokémon" freeze testers hit). So there is NO data-poke
    // fallback: if the native select can't run we say so and leave the selection untouched (recoverable),
    // never a half-selection that desyncs.
    private void MarkEntity(CardAddress addr, string label, string? parentEid)
    {
        if (m_entityOverlay == null || m_mgr == null) return;
        var th = m_mgr.TargetingHandler;
        if (th == null) return;
        int prev = TotalSelected();
        bool wasSelected = IsSelectedEid(addr.entityID);

        try { th.RefreshPlacementContext(); } catch (Exception ex) { Log.Error($"BattlePrompt: RefreshPlacementContext threw: {ex.Message}"); }

        // Board/attached selects act on the SPECIFIC public card by entityID (registry) so re-selecting
        // toggles that exact card; carousel/hand use the source-owner held card, or the FTUE-forced
        // focused card during a guided step.
        Card3D? card = m_entityClickMenu
            ? m_mgr.GetCard(addr.entityID)
            : ResolveSelectionCard(addr, FtueFocus.Card);

        if (card == null) { Say(LocalizationUtils.Get(LocalizationKey.BATTLE_PROMPT_SELECT_FAILED)); Log.Info($"BattlePrompt entity: card null eid={addr.entityID}"); return; }

        // A selection carousel STACKS identical cards (e.g. 18 Basic Fire Energy shown as one pile); only
        // the stack's TOP card is targetable, so map a buried duplicate to its top — exactly what the
        // game's own SelectionCardOwner.OnCardClicked does (GetFrontCardOfStackThatHoldsCard) before it
        // targets. Without this, TryTargetCard on the option's own (buried) instance just returns false.
        card = ResolveTargetableCard(card);

        bool acted = false;
        try { acted = th.TryTargetCard(card, ClickReleaseAutoPlacementInitiator.ReusableInitiator); }
        catch (Exception ex) { Log.Error($"BattlePrompt: TryTargetCard threw: {ex.Message}"); }

        Log.Info($"BattlePrompt entity eid={addr.entityID} clickMenu={m_entityClickMenu} acted={acted} wasSelected={wasSelected} owner={card.owner?.GetType().Name ?? "null"}" + (acted ? "" : $" [{TargetDebug(card)}]"));

        // Board-attached, SELECTING an energy whose parent Pokémon isn't "open" yet: the energy isn't
        // targetable until its parent is the one "distributing". The game opens ONE Pokémon at a time and
        // BLOCKS clicking another while one is open — you must return to the mini-board first. So defer to
        // TickPendingEnergySelect, which drives the whole sequence (close the wrong Pokémon → open this
        // one → select). Only on a fresh select, never a deselect (a selected energy is already targetable).
        if (!acted && !wasSelected && m_bacsMenu != null && !string.IsNullOrEmpty(parentEid))
        {
            m_pendingSelectEid = addr.entityID;
            m_pendingSelectParent = parentEid ?? "";
            m_pendingSelectTicks = 0;
            Log.Info($"BattlePrompt entity: deferring select of {addr.entityID} on parent {parentEid}");
            return;   // TickPendingEnergySelect opens the parent + selects + announces
        }

        AnnounceSelectionResult(prev, label, acted);
    }

    // Announce a select / deselect / failure from the game's OWN selection count, and auto-submit a
    // single-pick prompt. Shared by the immediate path and the deferred board-attached path.
    private void AnnounceSelectionResult(int prev, string nm, bool acted)
    {
        int now = TotalSelected();
        if (now > prev)
        {
            // Single-pick prompts (search a card, choose 1 Pokémon) submit immediately; multi-pick waits
            // for the Done option so the player controls how many.
            if (m_entityMax == 1) { m_submitted = true; m_entityOverlay?.SubmitClicked(); return; }
            Say(LocalizationUtils.Format(LocalizationKey.BATTLE_PROMPT_SELECTED_COUNT, nm, now, m_entityMax));
        }
        else if (now < prev)
        {
            Say(LocalizationUtils.Format(LocalizationKey.BATTLE_PROMPT_DESELECTED_COUNT, nm, now));
        }
        else
        {
            Say(LocalizationUtils.Get(acted ? LocalizationKey.BATTLE_PROMPT_CHANGE_FAILED : LocalizationKey.BATTLE_PROMPT_SELECT_FAILED));
        }
    }

    // Finish a board-attached select after its parent began distributing: the energy becomes targetable
    // once the carousel settles, so retry the native select each tick until it takes, then announce.
    // Times out (no freeze) if it never becomes selectable.
    private void TickPendingEnergySelect()
    {
        if (m_mgr == null || string.IsNullOrEmpty(m_pendingSelectEid)) { m_pendingSelectEid = ""; m_pendingSelectParent = ""; return; }
        var th = m_mgr.TargetingHandler;
        try { th?.RefreshPlacementContext(); } catch { }

        // The game opens ONE Pokémon's attached energies at a time and blocks switching while one is open.
        // So make sure the energy's PARENT is the open one before selecting: a DIFFERENT Pokémon open →
        // return to the mini-board to close it; NONE open → open this parent (its distribute click); the
        // RIGHT one open → fall through and select. Act every ~8 frames so each close/open animation
        // settles before we re-issue.
        if (m_bacsMenu != null && !string.IsNullOrEmpty(m_pendingSelectParent))
        {
            string? curr = null;
            try { curr = m_bacsMenu.GetAttachmentDistributor()?.CurrentDistributingEndpoint?.EndpointID; } catch { }
            if (curr != m_pendingSelectParent)
            {
                if (m_pendingSelectTicks % 8 == 0)
                {
                    if (!string.IsNullOrEmpty(curr))
                    {
                        try { m_bacsMenu.ReturnCardsInSelectionModeToTinyBoard(); }
                        catch (Exception ex) { Log.Error($"BattlePrompt: return-to-board threw: {ex.Message}"); }
                    }
                    else
                    {
                        var parent = m_mgr.GetCard(m_pendingSelectParent);
                        if (parent != null) NativeClick(parent);
                    }
                }
                if (++m_pendingSelectTicks >= 120) FailPendingSelect();
                return;
            }
        }

        int prev = TotalSelected();
        var card = m_mgr.GetCard(m_pendingSelectEid);
        if (card != null) card = ResolveTargetableCard(card);   // once open, the energy is a stacked carousel card
        bool acted = false;
        if (card != null && th != null)
        {
            try { acted = th.TryTargetCard(card, ClickReleaseAutoPlacementInitiator.ReusableInitiator); }
            catch (Exception ex) { Log.Error($"BattlePrompt: deferred TryTargetCard threw: {ex.Message}"); }
        }
        if (acted)
        {
            var eid = m_pendingSelectEid;
            string nm = m_options.FirstOrDefault(o => o.Eid == eid)?.Name ?? LocalizationUtils.Get(LocalizationKey.CARD_CARD);
            m_pendingSelectEid = ""; m_pendingSelectParent = "";
            AnnounceSelectionResult(prev, nm, acted: true);
            return;
        }
        if (++m_pendingSelectTicks >= 120) FailPendingSelect();
    }

    private void FailPendingSelect()
    {
        Log.Info($"BattlePrompt entity: deferred select timed out for {m_pendingSelectEid} (parent {m_pendingSelectParent})");
        m_pendingSelectEid = ""; m_pendingSelectParent = "";
        Say(LocalizationUtils.Get(LocalizationKey.BATTLE_PROMPT_ENERGY_SELECT_FAILED));
    }

    // The exact dispatch CardInteractions.OnUp makes for a real click: the card's override click listener
    // if one is set (board / prize / a distributing parent / attached), else its owner's click listener
    // (carousel / hand → the selection owner). We use it to distribute a board-attached parent Pokémon.
    private static bool NativeClick(Card3D card)
    {
        var ci = card.Interactions;
        if (ci == null) return false;
        try
        {
            if (ci.OverrideClickListener != null) { ci.OverrideClickListener.OnCardClicked(card, false); return true; }
            if (ci.CardClickListener != null) { ci.CardClickListener.OnCardClicked(card, false); return true; }
        }
        catch (Exception ex) { Log.Error($"BattlePrompt: NativeClick threw: {ex.Message}"); }
        return false;
    }

    private bool IsSelectedEid(string eid) =>
        m_entitySel != null && m_entitySel.selectionGroups.Any(g => g.selectedOptions.Any(s => s.entityID == eid));

    // Is the game willing to be clicked on this card yet?
    //
    // SelectionCardOwner.OnCardClicked — the real click path — opens with
    // `if (clickedCard.positioner.isAnimating) return;`. A card still flying into the pile is simply not
    // clickable, and acting anyway is worse than failing, because TryTargetCard SUCCEEDS: targetability
    // was already computed, so the card moves and Card3D.SetOwner calls UnfocusCard(). That early-returns
    // unless `cardFocused`, and cardFocused is set only at the END of FocusCard →
    // WaitUntilCardIsIdleInOwner, which is itself parked on `while (positioner.isAnimating)`. So
    // FTUEMatchManager.focusedCard is never cleared, FTUECardIndicator's `while (focusedCard != null)`
    // never completes, the tutorial never spotlights the next card, and DoesFTUEPermitCardTargetability
    // then denies every remaining one. Observed 2026-07-10: a pile pick taken 175 ms after the overlay
    // opened selected card 1 and made cards 2 and 3 permanently unselectable.
    //
    // Second clause: the FTUE's focus handshake. positioner.isAnimating can go false in Update before the
    // FTUE's coroutine (which runs after Update) has set cardFocused, so wait for the flag UnfocusCard
    // actually reads rather than for the condition it happens to be waiting on.
    private static bool IsSettledForClick(Card3D card)
    {
        if (card == null) return false;
        try
        {
            if (card.positioner != null && card.positioner.isAnimating) return false;
            var focused = FtueFocus.Card;
            if (focused != null && ReferenceEquals(focused, card) && !card.cardFocused) return false;
        }
        catch { }
        return true;
    }

    // Diagnostic for a TryTargetCard that refused a card the owner reported as accessible-for-targeting.
    // Distinguishes the causes that all read the same in the plain "acted=False" line: no active placement
    // context (ctx isn't the selection overlay), the FTUE targetability gate (only the spotlighted
    // focusedCard is targetable until the tutorial arms free play — Hilda hit this), or an
    // eligibility/instance mismatch (anyTargetable is true but not for this exact instance). It only runs
    // when a select already failed.
    private string TargetDebug(Card3D card)
    {
        try
        {
            var th = m_mgr?.TargetingHandler;
            string ctx = th != null ? th.CurrentContextLabel : "no-mgr";
            bool anyTargetable = th != null && th.AreAnyCardsTargetable;
            var ftue = FTUEMatchManager.Instance;
            if (ftue == null) return $"ctx={ctx} anyTargetable={anyTargetable} ftue=off";
            string focused = ftue.focusedCard == null ? "null" : ftue.focusedCard.entityID;
            var permit = FtueTracker.MatchFtuePermit(m_mgr);
            bool permits;
            try { permits = permit == null || permit(card); } catch { permits = true; }
            return $"ctx={ctx} anyTargetable={anyTargetable} focused={focused} blockDrag={card.blockDrag} ftuePermits={permits}";
        }
        catch (Exception ex) { return $"debug-threw:{ex.Message}"; }
    }

    // Pick one card from a COLLAPSED pile (carousel/hand). Always selects the pile's current targetable
    // TOP, so repeated presses peel cards off in order (up to the prompt's max) with no stale-id issues.
    // Fully native: TryTargetCard moves the real card → clears FTUE focus. No deselect here — for a
    // "choose up to N" you simply stop and press Done (board-attached discards keep per-card toggling).
    private void MarkStackOption(string name)
    {
        if (m_entityOverlay == null || m_mgr == null) return;
        var th = m_mgr.TargetingHandler;
        if (th == null) return;
        int prev = TotalSelected();
        try { th.RefreshPlacementContext(); } catch (Exception ex) { Log.Error($"BattlePrompt: RefreshPlacementContext threw: {ex.Message}"); }

        var top = FindSelectableTop(name);
        // A press that can't land yet is held and retried from HandleInput. The delegate re-enters this
        // method, so it re-reads the live front and re-checks the guard each frame — no timers, no counters.
        if (top != null && !IsSettledForClick(top)) { m_pendingChoose = () => MarkStackOption(name); return; }
        if (top == null)
        {
            // No unselected front of this name remains → the whole pile is already chosen. State it
            // (not a failure) so the player knows to move on / press Done, rather than hearing a
            // misleading "couldn't select".
            Say(LocalizationUtils.Format(LocalizationKey.BATTLE_PROMPT_NO_MORE, name, TotalSelected(), m_entityMax));
            Log.Info($"BattlePrompt stack: no targetable front for '{name}' selected={TotalSelected()}/{m_entityMax}");
            return;
        }

        bool acted = false;
        try { acted = th.TryTargetCard(top, ClickReleaseAutoPlacementInitiator.ReusableInitiator); }
        catch (Exception ex) { Log.Error($"BattlePrompt: stack TryTargetCard threw: {ex.Message}"); }
        Log.Info($"BattlePrompt stack '{name}' acted={acted} top={top.entityID}" + (acted ? "" : $" [{TargetDebug(top)}]"));

        AnnounceSelectionResult(prev, name, acted);
    }

    // Select the next energy from one Pokémon's attached-energy stack (plain AttachedCardSelectionMenu —
    // "discard any Fire Energy from among your Pokémon"). The energy sits in a temp HAND that stacks
    // identical cards and only exposes each stack's FRONT to targeting; the front isn't identifiable up
    // front (the hand owner returns ALL its cards, not just tops), but TryTargetCard on a buried card is
    // a harmless no-op (returns false, changes nothing — verified in the log). So peel the front the
    // robust native way: walk this pile's unselected eids and TryTargetCard each until one takes — that
    // one IS the current front, and it's the exact call a real click makes. One per press; Done finishes.
    private void MarkAttachedHandStack(string name, string parentEid, List<string> eids)
    {
        if (m_entityOverlay == null || m_mgr == null) return;
        var th = m_mgr.TargetingHandler;
        if (th == null) return;
        int prev = TotalSelected();
        try { th.RefreshPlacementContext(); } catch (Exception ex) { Log.Error($"BattlePrompt: RefreshPlacementContext threw: {ex.Message}"); }

        bool acted = false;
        string actedEid = "";
        foreach (var eid in eids)
        {
            if (IsSelectedEid(eid)) continue;                 // already discarded — skip
            var card = m_mgr.GetCard(eid);
            if (card == null) continue;
            card = ResolveTargetableCard(card);               // no-op for a hand owner, but safe
            // Same rule as the pile: never poke a card the game itself would refuse to be clicked on.
            if (!IsSettledForClick(card)) { m_pendingChoose = () => MarkAttachedHandStack(name, parentEid, eids); return; }
            try { acted = th.TryTargetCard(card, ClickReleaseAutoPlacementInitiator.ReusableInitiator); }
            catch (Exception ex) { Log.Error($"BattlePrompt: attached TryTargetCard threw: {ex.Message}"); }
            if (acted) { actedEid = eid; break; }             // hit the current front
        }
        Log.Info($"BattlePrompt attached '{name}' parent={parentEid} acted={acted} eid={actedEid} selected={TotalSelected()}/{m_entityMax}");

        if (!acted && TotalSelected() == prev)
        {
            // Nothing took and nothing changed → every energy of this pile is already chosen. State it
            // (not a failure) so the player moves on / presses Done.
            Say(LocalizationUtils.Format(LocalizationKey.BATTLE_PROMPT_NO_MORE, name, TotalSelected(), m_entityMax));
            return;
        }
        AnnounceSelectionResult(prev, name, acted);
    }

    // The current targetable TOP card of a pile (matched by displayed name). A real click only ever
    // hits a stack's CURRENT FRONT: SelectionCardOwner.OnCardClicked → GetFrontCardOfStackThatHoldsCard
    // → TryTargetCard(front). So we must return a genuine live front of the UNSELECTED (source) side —
    // never a stale reference, never an already-selected card. GetCardsAccessibleForTargeting() yields
    // exactly the per-stack fronts, and a selected card has moved to the selected-side owner, so it
    // naturally drops out of this scan.
    //
    // FTUE: a forced "select this card" step makes only FTUEMatchManager.focusedCard targetable, so we
    // honour it — but ONLY when it's actually one of those current unselected fronts. The old code
    // returned focusedCard unconditionally on a name match; that was the deck-search bug — after a
    // selection the focus stayed pointing at the just-selected card (now on the selected side), so
    // repeated Enter re-targeted that same card (thrashing the count, the top=… repeats in the log)
    // and the genuine remaining front was never reached. Requiring focus to be a live front, and
    // skipping selected cards, fixes the multi-select while still serving forced FTUE picks (there,
    // focus IS the one allowed front).
    private Card3D? FindSelectableTop(string name)
    {
        var focused = FtueFocus.Card;
        Card3D? front = null;
        try
        {
            // Pile sources backed by a SelectionCardOwner (deck search, attached lists): their
            // GetCardsAccessibleForTargeting() is exactly the per-stack fronts.
            foreach (var owner in UnityEngine.Object.FindObjectsByType<SelectionCardOwner>(FindObjectsSortMode.None))
            {
                if (owner == null || owner.AreHeldCardsConsideredSelected) continue;   // source / unselected side only
                foreach (var c in owner.GetCardsAccessibleForTargeting())
                {
                    if (c == null || ResolveName(c.entityID) != name || IsSelectedEid(c.entityID)) continue;
                    if (focused != null && ReferenceEquals(c, focused)) return c;   // the forced-FTUE card, and it's a live front
                    front ??= c;                                                    // first genuine current front
                }
            }
            if (front != null) return front;

            // Discard-from-hand (HandSelectionOverlay: Ultra Ball "discard 2 cards", etc.) uses a
            // HandFanController, which is a HandController — NOT a SelectionCardOwner — so the scan above
            // misses it entirely (that was the "no targetable front" bug for every hand card). The hand's
            // GetCardsAccessibleForTargeting() returns each slot's GetTopCard() (the targetable front of
            // each stack), so consult the active overlay's hand the same way. Selecting a hand card is the
            // identical native verb (TryTargetCard on that front).
            if (m_entityOverlay is IHandProvider hp && hp.Hand is { } hand)
            {
                foreach (var c in hand.GetCardsAccessibleForTargeting())
                {
                    if (c == null || ResolveName(c.entityID) != name || IsSelectedEid(c.entityID)) continue;
                    if (focused != null && ReferenceEquals(c, focused)) return c;
                    front ??= c;
                }
            }
        }
        catch (Exception ex) { Log.Warn($"BattlePrompt: FindSelectableTop threw: {ex.Message}"); }
        return front;
    }

    // Submit a multi-pick entity selection through the overlay's OWN confirm path (its Done button).
    // SubmitClicked validates min/max; if the player chose fewer than the max but met the minimum it
    // raises the game's "you can still select more" confirmation — itself a modal the mod speaks and
    // navigates. So we just press it and let the game decide.
    // We do NOT set m_submitted: if a confirm/error modal appears and the player cancels, the overlay
    // stays open and we must remain interactive — HandleInput / OnGainedFocus detect the real close via IsPromptOpen.
    private void SubmitEntity()
    {
        if (m_entityOverlay == null) return;
        // The game's SubmitClicked sets alreadySubmitted even when it then reports an error, which would
        // lock the overlay, so an invalid count never reaches it.
        if (m_entitySel != null && !m_entitySel.Validate())
        {
            Say(LocalizationUtils.Format(LocalizationKey.BATTLE_PROMPT_CHOOSE_AT_LEAST, Math.Max(m_entitySel.totalMinAmount, 1), TotalSelected()));
            return;
        }
        try { m_entityOverlay.SubmitClicked(); }
        catch (Exception ex) { Log.Error($"BattlePrompt: SubmitClicked threw: {ex.Message}"); }
    }

    // Map a card to the TARGETABLE instance the game would actually act on. A selection carousel (and the
    // hand) STACKS identical cards by archetype and exposes only each stack's top via
    // GetCardsAccessibleForTargeting() — a buried duplicate isn't targetable. SelectionCardOwner.OnCardClicked
    // resolves the clicked card to its stack front (GetFrontCardOfStackThatHoldsCard) before targeting; we
    // do the same: if the card isn't itself an accessible top, return the accessible top of its stack (the
    // same-archetype one). Selecting any copy of an identical pile is equivalent. No-op for non-selection
    // owners (board Pokémon, etc.), so it's safe to call on every path.
    private static Card3D ResolveTargetableCard(Card3D card)
    {
        if (card.owner is not SelectionCardOwner sco) return card;
        try
        {
            var tops = sco.GetCardsAccessibleForTargeting();
            foreach (var c in tops) if (ReferenceEquals(c, card)) return card;   // already a stack top
            int arch = card.cardDataRow != null ? card.cardDataRow.ArchetypeID : int.MinValue;
            if (arch != int.MinValue)
                foreach (var c in tops)
                    if (c != null && c.cardDataRow != null && c.cardDataRow.ArchetypeID == arch) return c;
        }
        catch (Exception ex) { Log.Warn($"BattlePrompt: ResolveTargetableCard threw: {ex.Message}"); }
        return card;
    }

    // Resolve the EXACT Card3D to select. During the FTUE, DoesFTUEPermitCardTargetability makes ONLY
    // FTUEMatchManager.focusedCard targetable (everything else is blocked), so it IS the card to select
    // for a forced step regardless of any entityID quirks — prefer it unconditionally. Outside the FTUE
    // (focused == null), use the open overlay's source owner's held card with this entityID
    // (AreHeldCardsConsideredSelected==false = unselected side); GetCard only as a last resort (it can
    // return a big-card-view / non-overlay instance the context won't accept).
    private Card3D? ResolveSelectionCard(CardAddress addr, Card3D? focused)
    {
        if (focused != null) return focused;

        foreach (var owner in UnityEngine.Object.FindObjectsByType<SelectionCardOwner>(FindObjectsSortMode.None))
        {
            if (owner == null || owner.AreHeldCardsConsideredSelected) continue;   // source (unselected) side only
            foreach (var c in owner.GetOwnedCards())
                if (c != null && c.entityID == addr.entityID) return c;
        }

        return m_mgr != null ? m_mgr.GetCard(addr.entityID, addr.pos, addr.index) : null;
    }

    #endregion

    #region Board selection
    // Choose a Pokémon ON the board — e.g. switch a Benched Pokémon to Active, choose a new Active after
    // a knockout, pick an attack target. Routes through BoardSelectionMenu (a BattleMenu, NOT an
    // EntitySelectionOverlay). The valid cards are the public board Card3Ds; a click on one fires the
    // menu's DelegatedClickOverride → MarkCardSelected (adds to the group + auto-submits for a single-card
    // pick). We invoke that exact override — what a sighted player's board click does — via
    // CardInteractions.OverrideClickListener.

    private static readonly MethodInfo? s_boardSubmit = AccessTools.Method(typeof(BoardSelectionMenu), "SubmitSelection");
    private static readonly FieldInfo? s_boardAutoSubmitField = AccessTools.Field(typeof(BoardSelectionMenu), "shouldAutoSubmitWhenSelectingSingleCard");

    private void BuildBoardOptions(BoardSelectionMenu board, EntitySelection esel)
    {
        var group = esel.selectionGroups.Count > 0 ? esel.selectionGroups[0] : null;   // BoardSelectionMenu uses [0]
        if (group == null) return;
        m_boardMenu = board;
        m_boardGroup = group;
        m_target = group.maxAmount < 1 ? 1 : group.maxAmount;
        foreach (var opt in group.validOptions)
        {
            var eid = opt.cardAddress.entityID;
            m_options.Add(new Option { Name = ResolveName(eid), Choose = () => MarkBoard(eid), Eid = eid });
        }
        if (string.IsNullOrWhiteSpace(m_question))
            m_question = m_target > 1
                ? LocalizationUtils.Format(LocalizationKey.BATTLE_PROMPT_CHOOSE_POKEMONS, m_target)
                : LocalizationUtils.Get(LocalizationKey.BATTLE_PROMPT_CHOOSE_POKEMON);

        // Same rule as BoardSelectionMenu's own auto-submit. Otherwise a sighted player gets a confirm button
        // on the message bar, and may stop anywhere inside the range.
        m_boardAutoSubmit = s_boardAutoSubmitField?.GetValue(board) is true && group.maxAmount == 1 && group.IsGroupFillRequirementSpecific();
        if (!m_boardAutoSubmit)
            m_options.Add(new Option { Name = LocalizationUtils.Get(LocalizationKey.BATTLE_DONE), IsDone = true, Choose = SubmitBoard });
        AppendRange(group.minAmount < 0 ? m_target : group.minAmount, m_target);
    }

    private void SubmitBoard()
    {
        if (m_boardMenu == null || m_boardGroup == null) return;
        if (!m_boardGroup.DoesSelectionCountFallWithinSubmitRange())
        {
            Say(LocalizationUtils.Format(LocalizationKey.BATTLE_PROMPT_CHOOSE_AT_LEAST, Math.Max(m_boardGroup.minAmount, 1), m_boardGroup.selectedOptions.Count));
            return;
        }
        m_submitted = true;
        try { s_boardSubmit?.Invoke(m_boardMenu, null); }
        catch (Exception ex) { Log.Error($"BattlePrompt board submit threw: {ex.Message}"); }
    }

    private void MarkBoard(string entityID)
    {
        if (m_boardMenu == null || m_boardGroup == null || m_mgr == null) return;
        int before = m_boardGroup.selectedOptions.Count;
        // At the maximum the menu turns an unselected card's click into Inspect: refuse instead of opening it.
        if (!m_boardAutoSubmit && before >= m_target && !m_boardGroup.selectedOptions.Any(s => s.entityID == entityID))
        {
            Say(LocalizationUtils.Get(LocalizationKey.BATTLE_PROMPT_MAX_SELECTED));
            return;
        }
        var card = m_mgr.GetCard(entityID);   // the public board card — same instance BoardSelectionMenu holds
        var listener = card != null && card.Interactions != null ? card.Interactions.OverrideClickListener : null;
        if (card == null || listener == null)
        {
            Log.Warn($"BattlePrompt board: no override click listener for {entityID} (card={(card == null ? "null" : "found")})");
            Say(LocalizationUtils.Get(LocalizationKey.BATTLE_PROMPT_POKEMON_FAILED));
            return;
        }

        // The exact call a board click makes (CardInteractions routes clicks to OverrideClickListener).
        // For a valid card → MarkCardSelected (+ auto-submit on a single-card pick); for an already
        // selected card → MarkCardDeselected. So this naturally supports select AND deselect.
        try { listener.OnCardClicked(card, false); }
        catch (Exception ex) { Log.Error($"BattlePrompt board select threw: {ex.Message}"); }

        Log.Info($"BattlePrompt board select eid={entityID} marked={m_boardGroup.selectedOptions.Count} open={IsPromptOpen()}");

        if (!IsPromptOpen()) { m_submitted = true; return; }   // auto-submitted and closed
        int marked = m_boardGroup.selectedOptions.Count;
        string name = m_options.FirstOrDefault(o => o.Eid == entityID)?.Name ?? "";
        if (marked < before) { Say(LocalizationUtils.Format(LocalizationKey.BATTLE_PROMPT_DESELECTED_COUNT, name, marked)); return; }
        if (marked < m_target) { Say(LocalizationUtils.Format(LocalizationKey.BATTLE_PROMPT_SELECTED_MORE, marked, m_target, m_target - marked)); return; }
        // Full: the count, then land on Done, so one more Enter finishes.
        Say(LocalizationUtils.Format(LocalizationKey.BATTLE_PROMPT_SELECTED_COUNT, name, marked, m_target));
        int done = m_options.FindIndex(o => o.IsDone);
        if (done < 0) return;
        m_idx = done; m_line = -1; m_lines = null;
        SpeakOption(done, interrupt: false);
    }

    #endregion

    #region Action selection
    // Choose one of a Pokémon's attacks and use it as this attack. Slowking's Seek Inspiration (an attack
    // of the Pokémon you just discarded off your deck) and Zoroark's Foul Play (an attack of the Defending
    // Pokémon) both raise SelectionMethod.Action → ActionSelectionOverlay, a modal drawer of AttackOption
    // widgets built from ActionSelection.options.
    //
    // Each widget's click command calls `overlay.SelectOption(index)` — which writes selectedIndex and
    // submits (SubmitSelectionAndClose → MatchUserOperations.SelectInput) — so that public method IS the
    // click endpoint, not a parallel path around it. It's the same one the game's action timer uses to
    // auto-pick (OnTimeExpired → SelectOption(0)). Two things the widget adds, both no-ops here:
    // CardActionOption.OnClick's UnfocusCard(), guarded on the FTUE-only card.cardFocused, and the
    // button's interactable gate — GetActionOptionSetupList passes canUse: true for every option and
    // never calls SetCanInteractDelegate, so no option is ever greyed out. There is no Back button
    // either (ShowBackButton => false): a sighted player must pick one, and so must we.
    //
    // The index we submit is the raw position in ActionSelection.options — the overlay's builder bumps
    // its counter for EVERY option and only renders the ones whose card-action row and CardAction both
    // resolve. Mirror that filter exactly so our indices stay aligned with the ones SelectOption expects.
    private void BuildActionOptions(ActionSelectionOverlay overlay, ActionSelection asel, PlayerSelection sel)
    {
        var db = ManagerSingleton<CardDatabaseManager>.instance;
        var matchInfo = NetworkMatchController.currentMatchInfo;
        if (db == null || matchInfo == null || asel.options == null) return;

        // The overlay only draws the energy cost when it's filling it against a player's energy; with
        // EnergyFillDisplayMode.NONE (Seek Inspiration — the cost is already paid) no cost is shown.
        bool showCost = asel.energyFillDisplayMode != ActionSelection.EnergyFillDisplayMode.NONE;
        string origin = BattleBoardReader.EntityName(sel.originCardEntityID);   // the drawer's title, e.g. "Slowking"
        // The widgets hold the live damage, once the drawer is up (it can open a little after this hook).
        var widgets = (ActionDrawer(overlay)?.CurrentOptions.OfType<AttackOption>() ?? Enumerable.Empty<AttackOption>()).ToList();
        int shown = 0;

        for (int i = 0; i < asel.options.Count; i++)
        {
            var opt = asel.options[i];
            if (opt == null || !int.TryParse(opt.actionSourceID, out int actionId)) continue;
            var row = db.TryGetActionFromDatabase(actionId);
            var action = matchInfo.GetAction(opt.actionGUID) as CardAction;
            if (row == null || action == null) continue;   // the overlay skips these too — keep i aligned

            int index = i;
            // The Pokémon the attack belongs to (the discarded card / the Defending Pokémon) — spoken
            // once as a section name, since it isn't the card named in the header.
            string parent = ResolveParentName(opt.parentEntityID) ?? "";
            // Which card pays the cost, as the drawer's GetCardForOption decides.
            var payer = asel.energyFillDisplayMode == ActionSelection.EnergyFillDisplayMode.ShowPlayerEnergyFill ? sel.originCardEntityID : opt.parentEntityID;
            string? liveCost = showCost ? CardInfo.Get(payer)?.GetAttackEnergyCost(action) : null;
            string? liveDamage = shown < widgets.Count ? PopupUtils.Text(ReadField<LocText>(widgets[shown], "damageAmountDisplay")) : null;
            shown++;
            m_options.Add(new Option
            {
                Name = ActionOptionLabel(row, action, showCost, liveCost, liveDamage),
                Parent = (parent.Length > 0 && parent != origin) ? parent : null,
                Eid = opt.parentEntityID,   // Up/Down reads that Pokémon's card
                Choose = () => { m_submitted = true; overlay.SelectOption(index); },
            });
        }

        if (string.IsNullOrWhiteSpace(m_question)) m_question = LocalizationUtils.Get(LocalizationKey.BATTLE_PROMPT_CHOOSE_ATTACK);
        if (origin.Length > 0) m_question = LocalizationUtils.Format(LocalizationKey.BATTLE_SENTENCES, origin, m_question);
    }

    // "<name>[, cost …][, N damage][. rules text]" — the same four fields the AttackOption widget draws,
    // read straight off the card-action row the widget itself renders from. An attack renamed or rewritten
    // for this match carries override loc ids on its CardAction, which win over the printed text (exactly
    // as AttackOption.SetupCustom resolves them). SpeakIcons runs first because energy symbols live in
    // this text as {W}-style tokens, which a later strip would silently delete.
    private static string ActionOptionLabel(CardActionDataRow row, CardAction action, bool showCost, string? liveCost, string? liveDamage)
    {
        string name = !string.IsNullOrEmpty(action.overrideNameLocID)
            ? LocalizationUtils.TryGetGame(action.overrideNameLocID) ?? row.Name
            : row.Name;
        if (string.IsNullOrWhiteSpace(name)) name = action.actionName;

        string text = !string.IsNullOrEmpty(action.overrideBodyLocID)
            ? LocalizationUtils.TryGetGame(action.overrideBodyLocID) ?? ""
            : row.Text ?? "";

        var head = new List<string> { CardText.SpeakIcons(name).Trim() };
        if (showCost)
        {
            // The cost and damage this match, when an effect changed them; else the printed values.
            string cost = BattleBoardReader.EnergyCostWords(string.IsNullOrEmpty(liveCost) ? row.Cost : liveCost);
            if (cost.Length > 0) head.Add(cost);
        }
        string damage = string.IsNullOrWhiteSpace(liveDamage) ? row.Damage : liveDamage!.Trim();
        if (!string.IsNullOrEmpty(damage)) head.Add(LocalizationUtils.Format(LocalizationKey.CARD_DAMAGE, damage));

        string label = string.Join(", ", head);
        text = CardText.SpeakIcons(text).Trim().TrimEnd('.');
        return text.Length > 0 ? LocalizationUtils.Format(LocalizationKey.BATTLE_SENTENCES, label, text) : label;
    }

    #endregion

    #region Active-promotion drag
    // "Choose 1 Benched Pokémon to switch with the Active Pokémon" routes to ActivePromotionDragMenu
    // (SelectionMethod.DragActive). It's a placement CONTEXT (ITargetCardPlacementContext +
    // ISupportsAutoPlacement) with a DragBenchToActive purpose — i.e. you DRAG the bench Pokémon onto
    // the Active slot, the same targeting-handler flow as the discard/search overlays. Native select =
    // TryTargetCard(benchCard, ClickReleaseAutoPlacementInitiator) → auto-places onto the single Active
    // receiver → ReactToSuccessfulPlacement → SubmitSelectionAndClose (auto-submits; no explicit submit).

    private void BuildActiveDragOptions(ActivePromotionDragMenu menu, EntitySelection esel)
    {
        var group = esel.selectionGroups.Count > 0 ? esel.selectionGroups[0] : null;
        if (group == null) return;
        m_dragMenu = menu;
        m_dragGroup = group;
        m_target = group.maxAmount < 1 ? 1 : group.maxAmount;
        foreach (var opt in group.validOptions)
        {
            var eid = opt.cardAddress.entityID;
            m_options.Add(new Option { Name = ResolveName(eid), Choose = () => MarkActiveDrag(eid), Eid = eid });
        }
        if (string.IsNullOrWhiteSpace(m_question))
            m_question = LocalizationUtils.Get(LocalizationKey.BATTLE_PROMPT_CHOOSE_SWITCH);
    }

    private void MarkActiveDrag(string entityID)
    {
        if (m_dragMenu == null || m_mgr == null) return;
        var th = m_mgr.TargetingHandler;

        // During the FTUE only focusedCard is targetable (the tutorial forces which bench Pokémon);
        // otherwise the chosen public bench card. Same resolution as the entity selections.
        var focused = FtueFocus.Card;
        Card3D? card = focused ?? m_mgr.GetCard(entityID);

        try { th.RefreshPlacementContext(); } catch (Exception ex) { Log.Error($"BattlePrompt activeDrag refresh threw: {ex.Message}"); }

        bool targetable = card != null && th.IsCardTargetable(card);
        bool ok = false;
        if (card != null && targetable)
        {
            // Auto-places onto the single Active receiver; the menu's ReactToSuccessfulPlacement submits.
            try { ok = th.TryTargetCard(card, ClickReleaseAutoPlacementInitiator.ReusableInitiator); }
            catch (Exception ex) { Log.Error($"BattlePrompt activeDrag TryTargetCard threw: {ex.Message}"); }
        }

        Log.Info($"BattlePrompt activeDrag eid={entityID} focused={(focused == null ? "null" : focused.entityID)} " +
                 $"targetable={targetable} ok={ok} autoSupported={m_dragMenu.IsAutoPlacementSupported} open={IsPromptOpen()}");

        if (!IsPromptOpen()) { m_submitted = true; return; }   // placed + auto-submitted → done

        // Didn't place (not targetable, or the menu needs a tap-to-confirm we didn't drive). Clear any
        // held target so we don't strand the card, and tell the player.
        if (ok) { try { th.ClearTarget(); } catch (Exception ex) { Log.Error($"BattlePrompt activeDrag ClearTarget threw: {ex.Message}"); } }
        Say(LocalizationUtils.Get(LocalizationKey.BATTLE_PROMPT_SWITCH_FAILED));
    }

    #endregion

    #region Damage distribution
    // ReparentDamageMenu / DamageSelection — NATIVE, like a sighted player.
    // Two shapes: MOVE existing counters ("move up to N damage counters ..." — fromDamageSelections
    // non-empty) and PUT new counters (Phantom Dive: "put N damage counters ..." — fromDamageSelections
    // empty).
    //
    // We DRIVE THE GAME'S OWN BOARD, not a data poke — because the tutorial has TWO coaches that each watch
    // for a physical step, and a data poke satisfies neither (it hangs the tutorial after the counters
    // silently land): FTUEReparentDamageIndicator waits for a source to START DISTRIBUTING (you click the
    // source Pokémon and its counters lift into a tray) and FTUEDragDamage waits for a counter to be DROPPED
    // on the target. So: (1) MOVE — click the source Pokémon via its distribute override
    // (SendCardToDistributorOnClick → ReparentDamageController.StartDistributionFromOwner spawns the tray
    // counters + fires the pick-up coach's event); PUT — the counters are pre-spawned at menu-open. (2) Drag
    // a tray counter onto the target Pokémon with the placement initiator (same hold-and-commit as a hand
    // play). ReparentDamageMenu.ReactToSuccessfulPlacement then applies the damage, clears the counter's FTUE
    // focus (advancing the drag coach), and auto-submits at the max. The counter's own denomination is the
    // amount moved — the game breaks the total into counters; we drop them one at a time. The evaluator only
    // TELLS us which sources/targets are valid and when the minimum is met; the drop does the real work.

    // The menu's own private state, read (never written, never re-created) so our reads and the game's
    // submit payload are always the same object graph. isWaitingForUpdate is a bool → FieldInfo, not ReadField.
    private static readonly FieldInfo? s_damageWaitingField = AccessTools.Field(typeof(ReparentDamageMenu), "isWaitingForUpdate");
    private static readonly MethodInfo? s_damageSrcEvent = AccessTools.Method(typeof(ReparentDamageMenu), "TriggerOnDamageDistributionSourceChangedEvent");
    private static readonly MethodInfo? s_damageComplete = AccessTools.Method(typeof(ReparentDamageMenu), "CompleteReparentingAction");

    private void BuildDamageOptions(ReparentDamageMenu menu, DamageSelection ds)
    {
        m_damageMenu = menu;
        m_damageSel = ds;
        m_damageEval = ReadField<ReparentDamageConstraintEvaluator>(menu, "constraintEvaluator");
        if (m_damageEval == null)
            Log.Error("BattlePrompt: couldn't read ReparentDamageMenu.constraintEvaluator — damage options unavailable");
        m_damageSource = null;
        m_damageSourceStarted = false;
        m_damageDropLanded = false;
        m_damageDropInFlight = false;
        m_damageRetry = null;
        // The menu auto-clicks a LONE eligible source before our postfix runs (SetupSelectableCards'
        // single-source branch), so distribution can already be live here. Adopt that source — the
        // evaluator excludes an active distributor from GetCardsWithDistributableDamage, so re-listing
        // would offer an empty source list and dead-end the prompt (Codex review finding, 2026-07-23).
        // Deliberately NOT marking it started: the first drop still runs EnsureDamageSourceDistributing,
        // whose already-distributing branch re-fires the coach event the tutorial missed at auto-start.
        var activeSource = CurrentDamageSourceId();
        if (ds.fromDamageSelections.Count > 0 && !string.IsNullOrEmpty(activeSource) && activeSource != "NewDC")
            m_damageSource = activeSource;
        HookDamagePlacementEnded();
        m_target = 0;
        RebuildDamageOptions();
        if (string.IsNullOrWhiteSpace(m_question))
            m_question = IsDamageMove
                ? LocalizationUtils.Get(LocalizationKey.BATTLE_PROMPT_MOVE_DAMAGE)
                : (ds.maxMovableDamageTotal == 1
                    ? LocalizationUtils.Get(LocalizationKey.BATTLE_PROMPT_PUT_COUNTER)
                    : LocalizationUtils.Format(LocalizationKey.BATTLE_PROMPT_PUT_COUNTERS, ds.maxMovableDamageTotal));
    }

    private bool IsDamageMove => m_damageSel != null && m_damageSel.fromDamageSelections.Count > 0;

    // A MOVE needs a source distributing before you can drop its counters; a PUT already has counters ready.
    private void RebuildDamageOptions()
    {
        m_options.Clear();
        if (m_damageSel == null || m_damageEval == null) return;
        if (IsDamageMove && m_damageSource == null) BuildDamageSourceOptions();
        else BuildDamageTargetOptions();
    }

    private void BuildDamageSourceOptions()
    {
        if (m_damageEval == null) return;
        // Same call the game's SetupSelectableCards makes: exclude the source that's already distributing
        // (its click is Inspect now; picking another source ends its distribution natively).
        var sources = m_damageEval.GetCardsWithDistributableDamage(CurrentDamageSourceId()).Distinct().ToList();
        // During the tutorial only the spotlighted source may distribute — offer just it, so you can't pick
        // a Pokémon the tutorial forbids and land on a dead end. (The tutorial does NOT allow another source.)
        var forced = FtueForcedCardEid();
        if (forced != null && sources.Contains(forced)) sources = new List<string> { forced };
        if (sources.Count == 1) { ChooseDamageSource(sources[0], announce: false); return; }   // lone source → auto-pick + distribute
        foreach (var eid in sources)
            m_options.Add(new Option { Name = LocalizationUtils.Format(LocalizationKey.BATTLE_PROMPT_MOVE_FROM, ResolveName(eid)), Eid = eid, Choose = () => ChooseDamageSource(eid) });
        if (m_damageEval.AreMinimumConstraintsSatisfied())
            m_options.Add(new Option { Name = LocalizationUtils.Get(LocalizationKey.BATTLE_DONE), IsDone = true, Choose = SubmitDamage });
    }

    // Chose a source: drive the game's own "pick up damage from this Pokémon" click (the distribute override
    // SetupSelectableCards put on eligible sources), lifting its counters into the tray and firing the
    // pick-up coach's event. Skip the click when this source is already the one distributing (a lone source
    // auto-starts at menu-open). Then move to the target list.
    private void ChooseDamageSource(string eid, bool announce = true)
    {
        // The tutorial forces which source you move from (coach 1 spotlights it), but the source list is
        // built at menu-open — BEFORE the coach spotlights — so it can still list every Pokémon. If the
        // player lands on a different one, redirect to the forced source so the tutorial isn't desynced.
        var forced = FtueForcedCardEid();
        if (forced != null && forced != eid && m_damageEval != null
            && m_damageEval.GetCardsWithDistributableDamage(null).Contains(forced))
            eid = forced;
        m_damageRetry = null;   // a drop held for an old target must not survive a source change
        m_damageSource = eid;
        // Distribution is triggered LAZILY on the first drop (EnsureDamageSourceDistributing), not here —
        // the tutorial's pick-up coach subscribes ~90 ms after the menu opens, and this can run at menu-open
        // (a lone source auto-picks in the postfix), so clicking now would fire the coach's event before it
        // is listening (exactly the race the old event-fire fix hit). The first drop is always well after.
        m_damageSourceStarted = false;
        RebuildDamageOptions();
        m_idx = 0; m_line = -1; m_lines = null;
        if (announce)
        {
            Say(LocalizationUtils.Format(LocalizationKey.BATTLE_PROMPT_MOVE_FROM_TO_WHICH, ResolveName(eid)));
            if (m_options.Count > 0) SpeakOption(m_idx, interrupt: false);
        }
    }

    // Lift the chosen source's counters into the tray so we can drop them — driving the game's OWN distribute
    // click (which fires the pick-up coach's event). If the game already auto-started this source's
    // distribution at menu-open (lone-source case, before the coach subscribed), re-fire the event so the
    // coach — which missed that first fire — still advances. Called on the first drop, once per source.
    private bool EnsureDamageSourceDistributing()
    {
        if (m_damageSource == null || m_mgr == null || m_damageMenu == null) return false;
        var src = m_mgr.GetCard(m_damageSource);
        if (src == null) return false;
        if (CurrentDamageSourceId() != m_damageSource)
        {
            // Only click a source that still carries its DISTRIBUTE override. Once a source has been the
            // distributor the game swaps its click to INSPECT (the big card viewer) — clicking it THEN pops
            // that overlay and strands the player behind it instead of distributing. Bail cleanly, never click.
            if (src.Interactions?.OverrideClickListener is not DistributeDamageFromCardOnClick)
            { Log.Warn($"BattlePrompt: {m_damageSource} click is inspect (already distributed), not distributing — would open the card viewer"); return false; }
            // DistributeDamageFromCardOnClick → StartDistributionFromOwner: ends any other source's
            // distribution, spawns the tray SYNCHRONOUSLY, fires the coach's event, and refreshes the
            // placement context itself — nothing to wait for afterwards.
            NativeClick(src);
            if (CurrentDamageSourceId() != m_damageSource) { Log.Warn($"BattlePrompt: couldn't start damage distribution from {m_damageSource}"); return false; }
            m_damageSourceStarted = true;
            Log.Info($"BattlePrompt damage: distributed source {m_damageSource}, tray counters now {TrayCounters().Count}");
            return true;
        }
        // Already distributing (a lone source auto-starts at menu-open) → its event fired before the coach
        // subscribed; re-fire it. Only inside a match the FTUE actually governs (else a harmless no-op).
        m_damageSourceStarted = true;
        if (FtueTracker.FtueGovernsMatch(m_mgr) && FTUEMatchManager.Instance != null)
        {
            try
            {
                s_damageSrcEvent?.Invoke(m_damageMenu, new object[] { src, true });
                Log.Info($"BattlePrompt: re-fired FTUE damage-distribution event for {src.entityID}");
            }
            catch (Exception ex) { Log.Error($"BattlePrompt: damage source event threw: {ex.Message}"); }
        }
        return true;
    }

    private void BackToDamageSources()
    {
        // List the OTHER eligible sources; the current one keeps distributing. Choosing another source
        // clicks it, and the game's own SendCardToDistributorOnClick ends this distribution and starts
        // there — the same atomic switch a sighted player's click performs. (Never call EndDistribution
        // ourselves; no real click path does.)
        m_damageRetry = null;   // a drop held for an old target must not survive the switch flow
        m_damageSource = null;
        RebuildDamageOptions();
        m_idx = 0; m_line = -1; m_lines = null;
        if (!string.IsNullOrWhiteSpace(m_question)) Say(m_question);
        if (m_options.Count > 0) SpeakOption(m_idx, interrupt: false);
    }

    // One option per eligible target (the evaluator excludes the source, full targets, and targets beyond
    // the cap). Each Enter drops one tray counter on it; press again to move another. Plus Back (move) and
    // Done (once the minimum is met; the game also auto-submits when you drop the last counter).
    private void BuildDamageTargetOptions()
    {
        m_options.Clear();
        if (m_damageSel == null || m_damageEval == null) return;
        bool move = m_damageSource != null;
        foreach (var eid in m_damageEval.GetEntityIDsForValidDamageReceivers(m_damageSource).Distinct())
        {
            var e = eid;
            m_options.Add(new Option
            {
                Name = LocalizationUtils.Format(move ? LocalizationKey.BATTLE_PROMPT_MOVE_TO : LocalizationKey.BATTLE_PROMPT_PUT_ON, ResolveName(e)),
                Eid = e,
                Choose = () => PlaceDamage(e),
            });
        }
        // Back = switch source. Only offered while another source is actually eligible — the game
        // excludes the current distributor, so with one source there is nothing to go back to.
        if (move && m_damageEval.GetCardsWithDistributableDamage(CurrentDamageSourceId()).Any())
            m_options.Add(new Option { Name = LocalizationUtils.Get(LocalizationKey.BATTLE_PROMPT_BACK), Choose = BackToDamageSources });
        if (m_damageEval.AreMinimumConstraintsSatisfied())
            m_options.Add(new Option { Name = LocalizationUtils.Get(LocalizationKey.BATTLE_DONE), IsDone = true, Choose = SubmitDamage });
    }

    // Drop ONE damage counter on the target — the game's own drag. Pick the tray counter the tutorial
    // spotlights (else the first available), hold it with the placement initiator aimed at the target, and
    // arm the commit — all on this one keypress. The game answers through its own events: success is
    // ReactToSuccessfulPlacement (announce + rebuild there), a refusal is the placement loop ending without
    // it (OnDamagePlacementEnded speaks up), and a not-ready tutorial counter completes via the coach-focus
    // / unblock hooks. No frame counting anywhere.
    private void PlaceDamage(string targetEid)
    {
        if (m_damageMenu == null || m_mgr == null) return;
        if (m_damageDropInFlight) return;   // the previous drop's placement loop hasn't reported back — a double-press, ignore
        if (DamageWaitingForServer()) { Say(LocalizationUtils.Get(LocalizationKey.BATTLE_PROMPT_WAITING)); return; }
        // MOVE: on the very first drop, lift the source's counters into the tray (and fire the pick-up
        // coach). StartDistributionFromOwner spawns the tray synchronously, so keep going right here.
        if (IsDamageMove && m_damageSource != null && !m_damageSourceStarted)
        {
            if (!EnsureDamageSourceDistributing()) { Say(LocalizationUtils.Get(LocalizationKey.BATTLE_PROMPT_LIFT_FAILED)); return; }
        }
        var target = m_mgr.GetCard(targetEid);
        if (target is not ICardPlacementReceiver recv) { Say(LocalizationUtils.Get(LocalizationKey.BATTLE_PROMPT_CANT_PUT_THERE)); return; }
        // While the tutorial owns the cards, a not-ready counter is a COACH-TIMING state, not a
        // refusal, so hold the drop and let the game's own events complete it. The signal is the
        // FTUE's OWN restriction flag, blockInputs (set by BlockAll for every coached step), plus a
        // tracked drag coach — NOT "a tutorial battle exists": FTUEMatchManager.Instance is present
        // in NORMAL matches too (CardInteractions dereferences Instance.blockInputs on every click),
        // so gating on the instance alone would swallow real refusals in ranked (Codex review
        // finding 2). The hold covers the observed live gap where Enter lands after the pick-up
        // coach exits and before FTUEDragDamage arms (21.8s vs 22.8s in the log) — completed by
        // DragDamageBlockCardsHook / FocusBlockDragHook / OnDamageTargetabilityEvaluated.
        // Backspace abandons a held drop.
        bool ftueOwnsCards = FtueTracker.DragDamageCoachActive
            || (FtueTracker.FtueGovernsMatch(m_mgr)
                && FTUEMatchManager.Instance != null && FTUEMatchManager.Instance.blockInputs);
        var counter = NextDamageCounter();
        if (counter == null)
        {
            if (ftueOwnsCards) { m_damageRetry = () => PlaceDamage(targetEid); return; }
            Say(LocalizationUtils.Get(LocalizationKey.BATTLE_PROMPT_NO_COUNTER)); return;
        }
        var th = m_mgr.TargetingHandler;
        if (!th.IsCardTargetable(counter))
        {
            if (ftueOwnsCards) { m_damageRetry = () => PlaceDamage(targetEid); return; }
            Say(LocalizationUtils.Get(LocalizationKey.BATTLE_PROMPT_CANT_MOVE_COUNTER)); return;
        }
        // Same receiver-first order as the energy move / no-target play (AccessibleBattleScreen.BeginNoTargetPlay):
        // TryTargetCard runs the placement loop's first pass synchronously, and that pass records the
        // receiver from KbPlacementInitiator.Chosen — so choose BEFORE the pickup, then arm.
        m_damageInit.Reset();
        m_damageInit.SetChosen(recv);
        bool acted = false;
        try { acted = th.TryTargetCard(counter, m_damageInit); }
        catch (Exception ex) { Log.Error($"BattlePrompt: damage TryTargetCard threw: {ex.Message}"); }
        Log.Info($"BattlePrompt damage pickup: counter={counter.entityID} target={targetEid} acted={acted}");
        if (!acted) { m_damageInit.Reset(); Say(LocalizationUtils.Get(LocalizationKey.BATTLE_PROMPT_CANT_MOVE_COUNTER)); return; }
        m_damageTargetEid = targetEid;
        m_damageDropLanded = false;
        m_damageDropInFlight = true;
        m_damageInit.ArmPlacement();   // the player's Enter is the trigger; the game reports the outcome
    }

    // The tray counter to drop. While an FTUEDragDamage coach is active it focuses the EXACT counter it
    // wants dropped (GetDamageCards()[counterPos]) and waits for THAT instance to clear — dropping any
    // other leaves it focused and hangs the coach — so then drop only the focused counter, and return
    // null (hold the drop) until it's focused. A tutorial step WITHOUT that coach, and every normal
    // match, drops any tray counter. Gating on the tracked coach state (not just "a tutorial battle")
    // is what keeps un-coached reparent steps from deadlocking on a focus that never comes.
    private Card3D? NextDamageCounter()
    {
        var cards = TrayCounters();
        if (cards.Count == 0) return null;
        if (FtueTracker.DragDamageCoachActive && FTUEMatchManager.Instance != null)
        {
            var focused = FTUEMatchManager.Instance.focusedCard;
            if (focused == null) return null;
            foreach (var c in cards) if (ReferenceEquals(c, focused)) return c;
            return null;
        }
        return cards[0];
    }

    private List<Card3D> TrayCounters()
    {
        var ctrl = m_damageMenu != null ? m_damageMenu.GetDamageController() : null;
        if (ctrl == null) return new List<Card3D>();
        try { return ctrl.GetDamageCards().Where(c => c != null).ToList(); }
        catch (Exception ex) { Log.Error($"BattlePrompt: GetDamageCards threw: {ex.Message}"); return new List<Card3D>(); }
    }

    // The source Pokémon whose counters are currently lifted into the tray (null if none distributing).
    private string? CurrentDamageSourceId()
    {
        try { return m_damageMenu?.GetDamageController()?.CurrentDistributingEndpoint?.EndpointID; }
        catch { return null; }
    }

    // A drop just landed (called from the ReactToSuccessfulPlacement postfix, the same moment the game
    // refreshes its own click overrides). Announce the VERIFIED result — read back from
    // finalDamageSelections, the very payload SelectInput will send — and re-offer targets.
    // Do NOT reset m_damageSource: a single-source MOVE keeps its one source for the whole prompt (the
    // just-used source's click is INSPECT now; collapsing back to it would pop the card viewer).
    private void AfterDamageDropApplied(ICardPlacementReceiver receiver)
    {
        m_damageRetry = null;
        string eid = m_damageTargetEid;
        try { if (receiver is Card3D rc && !string.IsNullOrEmpty(rc.entityID)) eid = rc.entityID; } catch { }
        if (m_damageMenu == null || !IsPromptOpen()) return;   // auto-submitted at the limit → the close path hides us
        RebuildDamageOptions();
        m_idx = 0; m_line = -1; m_lines = null;
        Say(DamageDropSummary(eid));
        if (m_options.Count > 0) SpeakOption(m_idx, interrupt: false);
    }

    // "Dunsparce has 60 damage." — read from the selection state the server will receive, so what the
    // player hears is what actually got recorded, never an assumption. Just the target's total: the
    // "N counters left to place" tally that followed it was cut as redundant —
    // the focused option that queues right after carries the flow.
    private string DamageDropSummary(string eid)
    {
        var name = ResolveName(eid);
        try
        {
            var fin = m_damageSel?.finalDamageSelections.FirstOrDefault(f => f.entityID == eid);
            return fin != null
                ? LocalizationUtils.Format(LocalizationKey.BATTLE_PROMPT_HAS_DAMAGE, name, fin.newDamageCounters * 10)
                : LocalizationUtils.Format(LocalizationKey.BATTLE_PROMPT_MOVED_DAMAGE_TO, name);
        }
        catch { return LocalizationUtils.Format(LocalizationKey.BATTLE_PROMPT_MOVED_DAMAGE_TO, name); }
    }

    // The placement loop ended. Success already went through the ReactToSuccessfulPlacement postfix; if our
    // armed drop ends WITHOUT it, the receiver was refused (FTUE gate, staleness) and the counter snapped
    // back to the tray — say so instead of silently doing nothing. (CardTargetingHandler fires this event
    // strictly AFTER the success callbacks, so the discrimination can't race.)
    private void OnDamagePlacementEnded()
    {
        if (!m_damageDropInFlight) return;
        m_damageDropInFlight = false;
        if (m_damageDropLanded) return;
        try
        {
            m_damageInit.Reset();
            if (!IsPromptOpen()) return;
            RebuildDamageOptions();
            m_idx = 0; m_line = -1; m_lines = null;
            Say(LocalizationUtils.Get(LocalizationKey.BATTLE_PROMPT_PLACE_FAILED));
            if (m_options.Count > 0) SpeakOption(m_idx, interrupt: false);
        }
        catch (Exception ex) { Log.Error($"BattlePrompt: damage cancel follow-up threw: {ex.Message}"); }
    }

    // A drop the player asked for was held for the game (coach focus / unblock / server echo); run it now.
    private void CompleteDamageRetry()
    {
        var r = m_damageRetry;
        if (r == null) return;
        m_damageRetry = null;
        try { if (IsPromptOpen()) r(); }
        catch (Exception ex) { Log.Error($"BattlePrompt: damage retry threw: {ex.Message}"); }
    }

    private bool DamageWaitingForServer()
    {
        try { return m_damageMenu != null && s_damageWaitingField?.GetValue(m_damageMenu) is true; }
        catch { return false; }
    }

    private void HookDamagePlacementEnded()
    {
        if (m_damagePlacementEndedHooked || m_mgr == null) return;
        try
        {
            m_mgr.TargetingHandler.OnPlacementEvaluationEnded += OnDamagePlacementEnded;
            // A held drop can also become possible through a plain targetability rebuild — e.g.
            // FTUEMatchManager.UnblockAllCards ends in RefreshPlacementContext with no coach call —
            // so listen to the game's own "targetability re-evaluated" broadcast too (filtered to
            // our menu's context). The retry re-validates and re-arms itself if still blocked.
            m_mgr.TargetingHandler.OnTargetabilityEvaluated += OnDamageTargetabilityEvaluated;
            m_damagePlacementEndedHooked = true;
        }
        catch (Exception ex) { Log.Error($"BattlePrompt: placement-ended subscribe threw: {ex.Message}"); }
    }

    private void UnhookDamagePlacementEnded()
    {
        if (!m_damagePlacementEndedHooked) return;
        try
        {
            if (m_mgr != null)
            {
                m_mgr.TargetingHandler.OnPlacementEvaluationEnded -= OnDamagePlacementEnded;
                m_mgr.TargetingHandler.OnTargetabilityEvaluated -= OnDamageTargetabilityEvaluated;
            }
        }
        catch { }
        m_damagePlacementEndedHooked = false;
    }

    private void OnDamageTargetabilityEvaluated(TPCI.Rainier.Match.Targeting.ITargetCardPlacementContext context)
    {
        if (m_damageRetry == null || m_damageMenu == null) return;
        if (!ReferenceEquals(context, m_damageMenu)) return;   // another menu's evaluation — not ours
        CompleteDamageRetry();
    }

    // Abandon a counter the player Backspaced out of while it was still surfacing — clear the targeting so
    // nothing stays half-grabbed. ClearTarget → OnPlacementCancelled → the counter returns to the tray; the
    // menu returns any un-dropped counters to the source at submit anyway.
    private void CancelDamageHold()
    {
        m_damageRetry = null;
        m_damageDropInFlight = false;
        try { m_mgr?.TargetingHandler.ClearTarget(); } catch (Exception ex) { Log.Error($"BattlePrompt: damage cancel threw: {ex.Message}"); }
        m_damageInit.Reset();
        if (IsPromptOpen()) RebuildDamageOptions();
    }

    // The FTUE-forced card id (the game's spotlighted card) while a tutorial battle is guiding this
    // selection, else null. Used to restrict the source list to the one the tutorial permits.
    private string? FtueForcedCardEid()
    {
        if (!FtueTracker.FtueGovernsMatch(m_mgr)) return null;
        var c = FtueFocus.Card;
        return c != null ? c.entityID : null;
    }

    // Submit through the menu's OWN CompleteReparentingAction (private — reflected). It sends the selection
    // the native drops built (SelectInput) and tears the menu down. Re-read the live evaluator first so the
    // minimum check reflects the counters actually dropped.
    private void SubmitDamage()
    {
        if (m_damageMenu == null) return;
        // A drop is still inside the placement coroutine (the release is observed after a yield) —
        // submitting now would tear the tray down under it and lose that counter. One beat later the
        // drop lands and its own follow-up re-offers Done.
        if (m_damageDropInFlight) { Say(LocalizationUtils.Get(LocalizationKey.BATTLE_PROMPT_STILL_PLACING)); return; }
        if (DamageWaitingForServer()) { Say(LocalizationUtils.Get(LocalizationKey.BATTLE_PROMPT_WAITING)); return; }
        // The MENU's own evaluator (never a fresh copy — constructing one wipes the recorded drops).
        if (m_damageEval != null && !m_damageEval.AreMinimumConstraintsSatisfied())
        { Say(LocalizationUtils.Get(LocalizationKey.BATTLE_PROMPT_NOT_ENOUGH_DAMAGE)); return; }
        m_submitted = true;
        try
        {
            if (s_damageComplete != null) s_damageComplete.Invoke(m_damageMenu, null);
            else Log.Error("BattlePrompt: ReparentDamageMenu.CompleteReparentingAction not found");
        }
        catch (Exception ex) { Log.Error($"BattlePrompt damage submit threw: {ex.Message}"); }
    }

    #endregion

    #region Text (coin / quiz / number / yes-no / mulligan)

    // Options keep their raw index (the one SelectOption wants); REMOVED_OPTION has no widget, exactly as in
    // DefaultTextModalDrawerSetupDataFactory. Choosing presses the drawer's own widget where one exists, so a
    // tutorial that greys out the other buttons (FTUESelectionIndicator) is respected. The widgets are looked
    // up at press time because the drawer can open a little after this hook.
    private void BuildTextOptions(TextSelectionOverlay text, PlayerSelection sel)
    {
        var ids = text.GetOptions();
        if (ids == null) return;
        var ts = sel.variableSelection as TextSelection;
        var kind = ts?.selectionType ?? TextSelection.SelectionType.Default;
        bool number = sel.variableSelection?.selectionMethod == VariableSelectionSettings.SelectionMethod.Number;
        int shown = 0;
        for (int i = 0; i < ids.Count; i++)
        {
            string id = ids[i];
            if (string.IsNullOrEmpty(id) || id == "REMOVED_OPTION") continue;
            int idx = i, position = shown++;
            string label = number ? id
                : kind == TextSelection.SelectionType.EnergySelection ? BattleBoardReader.TypeWord(id)
                : kind == TextSelection.SelectionType.RockPaperScissors ? MatchAnnouncements.RpsName(id)
                : LocalizationUtils.TryGetGame(id) ?? id;
            m_options.Add(new Option { Name = label, Choose = () => ChooseTextOption(text, kind, number, idx, position) });
        }
        // The number wheel opens on its highest value, so the list starts where a sighted player starts.
        if (number && m_options.Count > 0) m_idx = m_options.Count - 1;

        if (ts is CoinFlipContextualTextSelection flip) AddReflipContext(flip, sel);
        // The drawer's title names the card asking; the wheel draws none.
        string origin = number ? "" : BattleBoardReader.EntityName(sel.originCardEntityID);
        if (origin.Length > 0) m_question = string.IsNullOrWhiteSpace(m_question) ? origin : LocalizationUtils.Format(LocalizationKey.BATTLE_SENTENCES, origin, m_question);
    }

    private void ChooseTextOption(TextSelectionOverlay text, TextSelection.SelectionType kind, bool number, int idx, int position)
    {
        var drawer = text.GetAssociatedModalBuilder()?.Drawer;
        if (!number && kind == TextSelection.SelectionType.RockPaperScissors && drawer?.GetFirstDrawerOptionOfType<SelectRPSModalOption>() is { } rps)
        {
            // Not SelectOption alone: this also shows the game's "waiting for the opponent" result panel.
            m_submitted = true;
            rps.OptionSelected(idx);
            return;
        }
        Button? button = null;
        if (!number && drawer != null)
        {
            if (kind == TextSelection.SelectionType.EnergySelection)
            {
                var buttons = drawer.GetFirstDrawerOptionOfType<EnergyTextSelectModalOption>()?.energyOptionButtons;
                if (buttons != null && idx < buttons.Count) button = buttons[idx];
            }
            else
            {
                var widgets = drawer.CurrentOptions.OfType<SimpleButtonModalOption>().ToList();
                if (position < widgets.Count) button = ReadField<Button>(widgets[position], "SettingButton");
            }
        }
        if (button != null && !button.interactable) { Say(LocalizationUtils.Get(LocalizationKey.BATTLE_NOT_USABLE_NOW)); return; }
        m_submitted = true;
        if (button != null) button.onClick.Invoke();
        else text.SelectOption(idx);
    }

    // The reflip question ("flip again?") means nothing without the attack and the flips so far, both of
    // which the game shows in the drawer's custom header.
    private void AddReflipContext(CoinFlipContextualTextSelection flip, PlayerSelection sel)
    {
        var guid = !string.IsNullOrEmpty(flip.CopiedActionGuid) ? flip.CopiedActionGuid : sel.originActionRefID;
        var action = NetworkMatchController.currentMatchInfo?.GetAction(guid) as CardAction;
        var row = action != null && int.TryParse(action.actionSourceID, out int actionId) ? ManagerSingleton<CardDatabaseManager>.instance?.TryGetActionFromDatabase(actionId) : null;
        if (action != null && row != null)
            m_question = LocalizationUtils.Format(LocalizationKey.BATTLE_SENTENCES, ActionOptionLabel(row, action, false, null, null), m_question);
        if (flip.FlipState != null)
        {
            int heads = flip.FlipState.HeadsCount;
            m_question = LocalizationUtils.Format(LocalizationKey.BATTLE_SENTENCES, m_question, LocalizationUtils.Format(LocalizationKey.BATTLE_PROMPT_REFLIP_RESULT, heads, flip.FlipState.TotalAttempts - heads));
        }
    }

    #endregion

    #region AccessibleComponent

    public string GetHelp() => LocalizationUtils.Get(LocalizationKey.BATTLE_PROMPT_HELP);

    // Shown, or a modal above us closed (e.g. the "still have selections" / overpay confirm). If submitting
    // it actually closed the overlay, hide; otherwise re-announce so the player can keep choosing.
    public void OnGainedFocus()
    {
        if (!IsPromptOpen()) { Close(); return; }
        AnnouncePrompt();
    }

    public void HandleInput()
    {
        if (!IsPromptOpen()) { Close(); return; }          // submitted/dismissed → hide
        if (m_submitted) return;                            // wait silently for the close after submit
        if (!string.IsNullOrEmpty(m_pendingSelectEid)) { TickPendingEnergySelect(); return; }   // distributing then selecting an energy
        // A damage drop can be held for the game (the coach hasn't focused / freed its counter yet — the
        // event hooks complete it). Let Backspace/Escape abandon that hold rather than leaving it armed.
        if (m_damageMenu != null && m_damageRetry != null && (AccessibleKey.BACK.IsPressed() || AccessibleKey.ESCAPE.IsPressed()))
        { CancelDamageHold(); return; }
        // A press is waiting on a card that was still moving. Retry it; it re-arms itself if still unsettled.
        if (m_pendingChoose != null) { var retry = m_pendingChoose; m_pendingChoose = null; retry(); return; }

        // Tutorial: the damage source list is built at menu-open, BEFORE the coach spotlights which Pokémon
        // to move damage from, so it can still list every eligible source. The moment the coach points at
        // one, collapse to it (the tutorial doesn't let you use any other — no more "select each Pokémon").
        if (m_damageMenu != null && IsDamageMove && m_damageSource == null)
        {
            var forced = FtueForcedCardEid();
            if (forced != null && m_damageEval != null && m_damageEval.GetCardsWithDistributableDamage(null).Contains(forced))
            { ChooseDamageSource(forced); return; }
        }

        // Look at the board without leaving the decision: everything a sighted player can still see around it.
        if (BattleBoardReader.PressedZone() is { } zone) { AccessibleBoardReview.Open(zone, IsPromptOpen); return; }
        if (MatchAnnouncements.TryHandleInfoKey()) return;
        if (AccessibleKey.BACK.IsPressed() && m_entitySel != null) { DeselectFocused(); return; }

        // Tab jumps to the Done option (multi-pick confirm) for quick access — no-op if there isn't one.
        if (AccessibleKey.NEXT_SECTION.IsPressed()) { JumpToDone(); return; }

        // ONLY Left/Right navigate between options. Up/Down read the focused card's detail lines (like
        // board-card reading) and NEVER move between options — on a card with no readable lines they just
        // re-read the option, they don't jump to another one.
        if (AccessibleKey.READ_NEXT_ITEM.IsPressed()) { Move(1); return; }
        if (AccessibleKey.READ_PREV_ITEM.IsPressed()) { Move(-1); return; }
        if (AccessibleKey.READ_NEXT_LINE.IsPressed()) { ReadLine(1); return; }
        if (AccessibleKey.READ_PREV_LINE.IsPressed()) { ReadLine(-1); return; }
        if (AccessibleKey.CONFIRM.IsPressed()) { Choose(); return; }
        if (AccessibleKey.TryGetPressedNumber() is { } n) JumpTo(n - 1);
    }

    #endregion

    private void Say(string text, bool interrupt = true) => AccessibilityMgr.Output(this, text, interrupt);

    private static string ResolveName(string entityID)
    {
        var name = BattleBoardReader.EntityName(entityID);
        return name.Length > 0 ? name : LocalizationUtils.Get(LocalizationKey.CARD_CARD);
    }

    private void AnnouncePrompt()
    {
        m_line = -1; m_lines = null;   // re-orient to the header whenever we (re)announce
        m_lastSpokenParent = null;     // force the section header to speak on the first option
        if (!string.IsNullOrWhiteSpace(m_question)) Say(m_question);
        if (m_options.Count > 0) SpeakOption(m_idx, interrupt: false);
    }

    // Speak an option, prefixing the parent Pokémon as a SECTION NAME only when it changes from the last
    // option we announced — mirrors how menu groups announce their name on entry but not on every item.
    // So energy on the same Pokémon reads "Basic Fire Energy, 2 of 4" while crossing to another Pokémon
    // reads "Charmeleon. Basic Fire Energy, 3 of 4".
    private void SpeakOption(int i, bool interrupt)
    {
        if (i < 0 || i >= m_options.Count) return;
        var o = m_options[i];
        string label = OptionLabel(i);
        if (!string.IsNullOrEmpty(o.Parent) && o.Parent != m_lastSpokenParent)
            Say(LocalizationUtils.Format(LocalizationKey.BATTLE_SENTENCES, o.Parent, label), interrupt);
        else
            Say(label, interrupt);
        m_lastSpokenParent = o.Parent;
    }

    // Append ", selected" when the game's own selection data says this card is currently chosen — so
    // the player can tell what's already selected as they browse (the game shows it by moving the card
    // to its "selected" area). Reads SelectionGroup.selectedOptions; no separate tracking of our own.
    private string OptionLabel(int i)
    {
        var o = m_options[i];
        if (o.StackEids != null)
        {
            // A collapsed pile: read its live remaining count + how many of it are chosen (the running
            // total across the whole prompt is spoken on each pick).
            int sel = o.StackEids.Count(IsSelectedEid);
            int remaining = o.StackEids.Count - sel;
            string body = sel > 0
                ? LocalizationUtils.Format(LocalizationKey.BATTLE_PROMPT_PILE_SELECTED, o.Name, remaining, sel)
                : LocalizationUtils.Format(LocalizationKey.BATTLE_PROMPT_PILE, o.Name, remaining);
            return Announce.Item(body, null, i + 1, m_options.Count);
        }
        return Announce.Item(o.Name, IsOptionSelected(o) ? LocalizationUtils.Get(LocalizationKey.GLOBAL_SELECTED) : null, i + 1, m_options.Count);
    }

    // Is this option currently selected, per the game's own data? Entity / board / drag picks live in a
    // SelectionGroup's selectedOptions; prize picks live in the menu's private _selectedPrizes (read by
    // Card3D membership, since the prize group's selectedOptions isn't populated until submit). The
    // synthetic Done entry is never "selected".
    private bool IsOptionSelected(Option o)
    {
        if (o.IsDone) return false;
        if (m_prizeMenu != null && o.Card != null) return ReadSelectedPrizes(m_prizeMenu).Contains(o.Card);
        var eid = o.Eid;
        if (eid == null) return false;
        if (m_entitySel != null) return m_entitySel.selectionGroups.Any(g => g.selectedOptions.Any(s => s.entityID == eid));
        if (m_boardGroup != null) return m_boardGroup.selectedOptions.Any(s => s.entityID == eid);
        if (m_dragGroup != null) return m_dragGroup.selectedOptions.Any(s => s.entityID == eid);
        return false;
    }

    // Total cards selected across all entity groups (multi-group prompts spread picks over groups).
    private int TotalSelected() =>
        m_entitySel != null ? m_entitySel.selectionGroups.Sum(g => g.selectedOptions.Count)
        : (m_entityGroup != null ? m_entityGroup.selectedOptions.Count : 0);

    private bool IsPromptOpen()
    {
        // PrizeCardSelectionMenu doesn't drive BattleMenu.isOpen the way the overlays do, so use its
        // own SELECT-state flag; other overlays use isOpen.
        if (m_handler is PrizeCardSelectionMenu p) return p.IsAwaitingSelection;
        if (m_handler is BattleMenu bm) return bm.isOpen;
        return false;
    }

    private void Move(int delta)
    {
        if (m_options.Count == 0) return;
        int next = Mathf.Clamp(m_idx + delta, 0, m_options.Count - 1);
        if (next == m_idx) return;
        m_idx = next;
        m_line = -1; m_lines = null;   // new option → reset line cursor; lines re-resolve on next Up/Down
        SpeakOption(m_idx, interrupt: true);
    }

    private void JumpTo(int i)
    {
        if (i < 0 || i >= m_options.Count) return;
        m_idx = i;
        m_line = -1; m_lines = null;
        SpeakOption(m_idx, interrupt: true);
    }

    // Tab → jump straight to the Done option (multi-pick menus) so the player doesn't have to arrow past
    // every card to confirm. They still press Enter on it themselves.
    private void JumpToDone()
    {
        int di = m_options.FindIndex(o => o.IsDone);
        if (di >= 0) JumpTo(di);
    }

    // Read the focused option's card line-by-line (Down = next line, Up = previous, back past line 0 =
    // the option header). Mirrors board-card reading so "card-related stuff" reads the same everywhere.
    private void ReadLine(int dir)
    {
        if (m_options.Count == 0) return;
        EnsureLines();
        // No detail to read (face-down prize, energy, synthetic option) → re-read the option; NEVER move
        // between options (Left/Right is the only navigation).
        if (m_lines == null || m_lines.Count == 0) { Say(OptionLabel(m_idx)); return; }
        if (dir > 0)
        {
            if (m_line < m_lines.Count - 1) { m_line++; Say(m_lines[m_line]); }
            else Say(LocalizationUtils.Get(LocalizationKey.BATTLE_PROMPT_LAST_LINE));
        }
        else if (m_line >= 0)
        {
            m_line--;
            Say(m_line < 0 ? OptionLabel(m_idx) : m_lines[m_line]);
        }
        else Say(OptionLabel(m_idx));
    }

    // Resolve + cache the focused option's detail lines on first Up/Down. Empty list = no card to read.
    private void EnsureLines()
    {
        if (m_lines != null) return;
        var card = ResolveCardForRead(m_options[m_idx].Eid);
        m_lines = card != null ? BattleBoardReader.DetailLines(card) : new List<string>();
        m_line = -1;
    }

    private static readonly BattleBoardReader.Zone[] s_readZones =
    {
        BattleBoardReader.Zone.MyActive, BattleBoardReader.Zone.MyBench,
        BattleBoardReader.Zone.OppActive, BattleBoardReader.Zone.OppBench, BattleBoardReader.Zone.MyDiscard,
    };

    // Find the live Card3D for an option's entityID WITHOUT side effects (NOT MatchManager.GetCard,
    // which rewrites CardAddress / returns a big-card view for hidden cards). Selection overlays hold
    // their cards in SelectionCardOwners (the carousel/list); board & active-drag picks are public board
    // cards. Read-only and run on demand (a key press), so the scan cost is irrelevant.
    private Card3D? ResolveCardForRead(string? eid)
    {
        if (string.IsNullOrEmpty(eid)) return null;
        try
        {
            foreach (var owner in UnityEngine.Object.FindObjectsByType<SelectionCardOwner>(FindObjectsSortMode.None))
            {
                if (owner == null) continue;
                foreach (var c in owner.GetOwnedCards())
                    if (c != null && c.entityID == eid) return c;
            }
            if (m_mgr != null)
            {
                // The hand (discard-from-hand prompts like Ultra Ball's cost): scan EVERY hand card —
                // the discard source is the HandFanController, not a SelectionCardOwner, so the scan
                // above misses it, and GetCards(MyHand) returns only pile top-cards (would miss a
                // buried duplicate). GetHandCardsAll returns all copies.
                foreach (var c in BattleBoardReader.GetHandCardsAll(m_mgr))
                    if (c != null && c.entityID == eid) return c;
                foreach (var z in s_readZones)
                    foreach (var c in BattleBoardReader.GetCards(m_mgr, z))
                        if (c != null && c.entityID == eid) return c;
            }
        }
        catch (Exception ex) { Log.Warn($"BattlePrompt: ResolveCardForRead threw: {ex.Message}"); }
        return null;
    }

    private void Choose()
    {
        if (m_idx < 0 || m_idx >= m_options.Count) return;
        try { m_options[m_idx].Choose(); }
        catch (Exception ex) { Log.Error($"BattlePrompt.Choose threw: {ex.Message}"); }
        // The submit closes the overlay; HandleInput detects that and hides. Result narrates via the log.
    }

    private void Close()
    {
        m_pendingShow = false;
        if (!m_active) return;
        m_active = false;
        m_handler = null;
        m_pendingChoose = null;
        // Release any card the targeting handler might still be holding (a damage counter caught mid
        // surface-retry) so it isn't left with a stranded target — that makes later board/prompt input
        // read as dead. Unconditional: ClearTarget/Reset are harmless no-ops when nothing is held.
        try { m_mgr?.TargetingHandler.ClearTarget(); } catch (Exception ex) { Log.Error($"BattlePrompt: close ClearTarget threw: {ex.Message}"); }
        m_damageInit.Reset();
        UnhookDamagePlacementEnded();
        m_damageRetry = null;
        m_damageDropLanded = false;
        m_damageDropInFlight = false;
        m_options.Clear();
        AccessibilityMgr.HideUI(this);
    }
}
