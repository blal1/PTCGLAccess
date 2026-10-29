using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using TMPro;
using TPCI.Rainier.Features.DeckEditor;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;
using static Accessibility.GameReflect;

namespace Accessibility;

// RENAME DECK popup — driver: LandscapeEditDeckNamePopupGroup (a HUBGroupController).
//
// This popup had no handler at all in the first mod versions, which is why renaming a deck never worked: the pencil
// opened it silently, the deck editor (or the DECKS screen) underneath kept owning the keyboard, and the text field
// was never activated — so arrows still moved cards in the editor, typing went nowhere, and the only way out was
// Escape.
//
// ── Who opens it (both routes end here) ──
// In landscape — the only aspect this game runs in on desktop — there is exactly ONE entry point:
//   • deck editor: HUBEditDeckController.Settings_BeginNameChange() (the ✎ next to the deck name)
//   • DECKS screen: DeckPanel_L.OpenDeckRenameMenu() (the ✎ in the selected deck's panel)
// Both do `renameDeckPopup as LandscapeEditDeckNamePopupGroup` → ShowPopup(currentName, callback), so one hook on
// ShowPopup covers both. (The portrait path is a different object entirely — OverlayManager + RenameDeckOverlay —
// and never runs here.)
//
// ── Committing the rename ──
// OnDone() / OnCancel() are the popup's own public methods, and they are what the prefab's DONE and CANCEL buttons
// invoke. OnDone sanitizes through HUBEditDeckController.GetValidDeckName, hands the name to the ShowPopup callback
// (which writes _deck.deckName, calls PlayerInventoryManager.UpdateDeck and refreshes the on-screen title) and saves
// to the service. OnCancel restores the cached name. So Enter on DONE routes through the game's real endpoint.
//
// ── Nav model ──
// The text FIELD is its own group (arrows are its caret, so they can't be nav) and the BUTTONS are a second group;
// Tab switches between the two. KeyboardNavigator already hands arrows / Enter / Space to a focused field, echoes the
// typed characters, and the global letter shortcuts skip a focused field — so the mod's global text-input gate
// (AccessibilityMgr.AllowTextInput) is NOT used: it would also stop Escape and Tab reaching this popup.
//
// Enter inside the field: only routed to OnDone when the prefab left the field's own submit events unwired. If the
// prefab already wires onSubmit/onEndEdit, we keep our hands off it rather than risk submitting the rename twice.
internal sealed class AccessibleDeckName : AccessibleUI
{
    private static readonly AccessibleDeckName s_instance = new();

    private readonly List<KeyboardNavigator> m_groups = new();
    private int m_activeGroup;
    private LandscapeEditDeckNamePopupGroup? m_popup;
    private TMP_InputField? m_field;
    private bool m_fieldOwnsSubmit;     // the prefab wired the field's own submit — don't double-fire
    private string m_openedWith = "";   // the name the popup was opened on, to tell a real rename from a cancel
    private bool m_committing;          // OnDone is running, so the close that follows is a commit

    public void HandleInput()
    {
        if (!IsVisible()) { Hide(); return; }

        // Escape FIRST, before any content check can return early — a popup that can't be closed seals a blind
        // player in.
        if (AccessibleKey.ESCAPE.IsPressed()) { Cancel(); return; }
        if (m_groups.Count == 0) return;

        // TAB switches GROUPS (field ↔ buttons); it never steps control by control. Arrows belong to whichever group
        // is live — inside the field they're the caret.
        if (AccessibleKey.NEXT_SECTION.IsPressed() || AccessibleKey.PREV_SECTION.IsPressed())
        {
            if (m_groups.Count > 1)
            {
                var dir = AccessibleKey.PREV_SECTION.IsPressed() ? -1 : 1;
                m_activeGroup = (m_activeGroup + dir + m_groups.Count) % m_groups.Count;
                m_groups[m_activeGroup].FocusFirst(announce: true, interruptPrior: true);
            }
            return;
        }

        // Enter while typing = commit the rename, but only when the prefab left the field's own submit events
        // unwired. If it wired them, TMP already fires them on this same keypress and acting too would submit twice
        // (a redundant SaveDecksToServiceAsync).
        var nav = m_groups[m_activeGroup];
        if (!m_fieldOwnsSubmit && nav.CurrentIsTextField && AccessibleKey.CONFIRM.IsPressed())
        {
            m_popup?.OnDone();
            return;
        }
        nav.HandleInput();
    }

    public string GetHelp() => LocalizationUtils.Get(LocalizationKey.COLLECTION_DECK_NAME_HELP);

    public void OnGainedFocus()
    {
        Rebuild();
        if (m_groups.Count == 0) return;
        // Title first (interrupting whatever the editor was saying), then the focused item queues behind it.
        AccessibilityMgr.Output(this, Title(), interrupt: true);
        m_groups[0].FocusFirst(announce: true, interruptPrior: false);
    }

    // Escape = the popup's own cancel: restores the name it was opened with, then closes.
    private void Cancel() => m_popup?.OnCancel();

    // Detach each navigator before dropping it, so the deck-name field's character-echo listener doesn't outlive the
    // navigator and double-fire on the next rebuild. See KeyboardNavigator.Detach.
    private void ClearGroups()
    {
        foreach (var g in m_groups) g.Detach();
        m_groups.Clear();
        m_activeGroup = 0;
    }

    private void Rebuild()
    {
        ClearGroups();
        m_field = null;
        m_fieldOwnsSubmit = false;
        if (m_popup == null) return;

        // Group 1: the deck-name field. ShowPopup has already pre-filled it with the current name.
        m_field = ReadField<TMP_InputField>(m_popup, "deckTitleInputField");
        if (m_field != null && m_field.isActiveAndEnabled)
        {
            // If we can't tell whether the field wires its own submit, fail CLOSED — assume it does, so Enter-in-field
            // defers to the field / DONE rather than risking a second OnDone on the same keypress.
            try { m_fieldOwnsSubmit = HasPersistent(m_field.onEndEdit) || HasPersistent(m_field.onSubmit); }
            catch { m_fieldOwnsSubmit = true; }
            AddGroup(new List<Selectable> { m_field }, FieldLabel);
        }
        else
        {
            Log.Warn("Rename deck: deckTitleInputField missing or inactive — buttons only, no typing");
        }

        // Group 2: DONE / CANCEL (and any close ✕), by reading order. Their own text is the game's localized label.
        var popupT = m_popup.transform;
        var buttons = CollectionUi.Buttons(m_popup).Where(b => IsRealButton(b, popupT)).ToList();
        if (buttons.Count > 0)
        {
            var g = AddGroup(buttons, KeyboardNavigator.DefaultLabel);
            // DONE and CANCEL sit side by side, so Left/Right is the natural axis; keep Up/Down live too.
            g.AllowHorizontal = true;
            g.AllowVertical = true;
        }
    }

    private KeyboardNavigator AddGroup(List<Selectable> items, KeyboardNavigator.LabelResolver label)
    {
        // AllowTab=false: this component owns Tab (group switching), not the navigator.
        var g = new KeyboardNavigator(this) { AllowTab = false, OnBack = Cancel };
        g.SetItems(items, label);
        m_groups.Add(g);
        return g;
    }

    // The popup's own header, so it announces itself in the player's language.
    private string Title()
    {
        if (m_popup != null)
        {
            foreach (var t in m_popup.GetComponentsInChildren<TMP_Text>(includeInactive: false).OrderBy(t => CollectionUi.ReadingOrder(t.transform)))
            {
                if (!UiLabels.HasLetter(t.text)) continue;
                if (IsPartOfField(t)) continue;                          // the field's own text / placeholder
                if (t.GetComponentInParent<Button>() != null) continue;  // a DONE / CANCEL label
                return t.text.Trim();
            }
        }
        return LocalizationUtils.GetGame("ui_deck_options_rename", LocalizationKey.UI_RENAME_DECK);
    }

    private bool IsPartOfField(TMP_Text t)
    {
        if (m_field == null) return false;
        if (ReferenceEquals(t, m_field.textComponent) || ReferenceEquals(t, m_field.placeholder)) return true;
        return t.transform.IsChildOf(m_field.transform);
    }

    // "<what it's for>, <current name>, edit field". DefaultLabel builds exactly that from the placeholder, but a
    // rename field arrives pre-filled — so when the prefab has no placeholder text we'd otherwise announce a raw
    // GameObject name. Name it from the popup's own header instead.
    private string FieldLabel(Selectable s)
    {
        var label = KeyboardNavigator.DefaultLabel(s);
        if (s is not TMP_InputField f) return label;
        if (f.placeholder is TMP_Text ph && UiLabels.HasLetter(ph.text)) return label;
        var content = string.IsNullOrWhiteSpace(f.text) ? LocalizationUtils.Get(LocalizationKey.NAV_FIELD_BLANK) : f.text.Trim();
        label = LocalizationUtils.Format(LocalizationKey.NAV_EDIT_FIELD_FORMAT, Title(), content);
        return f.interactable ? label : Announce.WithState(label, LocalizationKey.GLOBAL_DISABLED);
    }

    // A real control, not a decorative child: PTCGL prefabs often put a clickable icon or background inside the
    // button that already handles the click.
    private static bool IsRealButton(Selectable b, Transform popup)
    {
        for (var t = b.transform.parent; t != null && t != popup; t = t.parent)
            if (t.GetComponent<Button>() != null) return false;
        return true;
    }

    private bool IsVisible()
    {
        if (m_popup == null || !m_popup.gameObject.activeInHierarchy) return false;
        // Deactivate() runs Anim.Hide() before the GameObject goes inactive, and Hide() leaves the object active while
        // the close animation plays. currentlyVisible flips immediately, so it's the earlier, truer signal.
        var anim = m_popup.GetComponent<AnimatedSetActive>();
        return anim == null || anim.currentlyVisible;
    }

    // No internal catch: an inspection failure must reach Rebuild's try so it can fail CLOSED.
    private static bool HasPersistent(UnityEventBase? e) => e != null && e.GetPersistentEventCount() > 0;

    private void Hide()
    {
        ClearGroups();
        m_popup = null;
        AccessibilityMgr.HideUI(this);
    }

    private void Open(LandscapeEditDeckNamePopupGroup popup)
    {
        m_popup = popup;
        m_committing = false;
        // ShowPopup has already written the current deck name into the box, so the box IS the name we were opened on.
        var field = ReadField<TMP_InputField>(popup, "deckTitleInputField");
        m_openedWith = field != null ? (field.text ?? "").Trim() : "";
        if (AccessibilityMgr.IsFocused(this)) OnGainedFocus();
        else AccessibilityMgr.ShowUI(this);
    }

    private void Closed()
    {
        // OnDone has already run GetValidDeckName + the ShowPopup callback by now, so the field holds the name the
        // deck actually took. An empty box falls back to the old name, which is no rename.
        var committed = m_committing;
        var newName = m_field != null ? (m_field.text ?? "").Trim() : "";
        var openedWith = m_openedWith;
        m_committing = false;
        Hide();
        // Spoken last, after the screen underneath re-announces where the player is, so the confirmation is the thing
        // they're left with.
        if (committed && newName.Length > 0 && newName != openedWith)
            AccessibilityMgr.OutputNotification(LocalizationUtils.Format(LocalizationKey.COLLECTION_DECK_RENAMED, newName), interrupt: true);
    }

    [HarmonyPatch(typeof(LandscapeEditDeckNamePopupGroup), "ShowPopup")]
    private static class ShowPopupHook
    {
        private static void Postfix(LandscapeEditDeckNamePopupGroup __instance) => s_instance.Open(__instance);
    }

    // Every way this popup closes — DONE, CANCEL, the editor being torn down (HUBEditDeckController.OnDeactivate) and
    // DeckPanel_L.HideDeckRenamePopup — goes through Deactivate(), so that's the close hook.
    [HarmonyPatch(typeof(LandscapeEditDeckNamePopupGroup), "Deactivate")]
    private static class DeactivateHook
    {
        private static void Postfix(LandscapeEditDeckNamePopupGroup __instance)
        {
            if (s_instance.m_popup != null && __instance == s_instance.m_popup) s_instance.Closed();
        }
    }

    // OnDone ends by calling Deactivate, so by itself a close tells us nothing about WHY. The prefix marks the close
    // that follows as a commit, so we only confirm a rename that really happened — never one the player cancelled,
    // and never one the editor tore down mid-edit. The finalizer clears the flag only if OnDone threw before reaching
    // Deactivate (on the normal path the Deactivate postfix already consumed it), so a later unrelated teardown can't
    // falsely announce a rename.
    [HarmonyPatch(typeof(LandscapeEditDeckNamePopupGroup), "OnDone")]
    private static class OnDoneHook
    {
        private static void Prefix(LandscapeEditDeckNamePopupGroup __instance)
        {
            if (__instance == s_instance.m_popup) s_instance.m_committing = true;
        }

        private static void Finalizer(LandscapeEditDeckNamePopupGroup __instance)
        {
            if (__instance == s_instance.m_popup) s_instance.m_committing = false;
        }
    }
}
