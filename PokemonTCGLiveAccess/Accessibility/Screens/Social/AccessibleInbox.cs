using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using static Accessibility.GameReflect;

namespace Accessibility;

// Inbox (HUBInboxScreenController, opened by the overflow menu's button through the prefab). A list of message rows,
// each with its own buttons. Up/Down walk the messages; Left/Right the current message's actions, always through the
// game's own buttons: the row's background button (full text in a modal), its call-to-action buttons, its remove
// button. Clear All sits after the last message and confirms through the modal component.
//
// A UI, not a screen: it opens from the overflow menu, which may still be up, and must sit on top of it.
// OnActivate runs inside Activate, BEFORE the messages are loaded, so the first reading waits a frame.
internal sealed class AccessibleInbox : AccessibleUI
{
    private sealed class Row
    {
        public InboxMessageListItem? Item;
        public readonly List<Button> Actions = new();
        public readonly List<string> Labels = new();
    }

    private static readonly AccessibleInbox s_instance = new();
    private static int s_lastUnseen;

    private readonly List<Row> m_rows = new();
    private HUBInboxScreenController? m_controller;
    private int m_row;
    private int m_action;
    private bool m_pending;
    private bool m_titleSpoken;

    public void HandleInput()
    {
        if (m_controller == null || !m_controller.isActiveAndEnabled)
        {
            Close();
            return;
        }
        if (m_pending)
        {
            m_pending = false;
            Rebuild();
            AnnounceList();
            return;
        }
        if (AccessibleKey.ESCAPE.IsPressed() || AccessibleKey.BACK.IsPressed()) m_controller.Deactivate();
        else if (m_rows.Count == 0) return;
        else if (AccessibleKey.READ_CUR_LINE.IsPressed()) SpeakRow(interrupt: true);
        else if (AccessibleKey.READ_NEXT_MENU_OPTION.IsPressed()) MoveRow(m_row + 1);
        else if (AccessibleKey.READ_PREV_MENU_OPTION.IsPressed()) MoveRow(m_row - 1);
        else if (AccessibleKey.READ_FIRST_ITEM.IsPressed()) MoveRow(0);
        else if (AccessibleKey.READ_LAST_ITEM.IsPressed()) MoveRow(m_rows.Count - 1);
        else if (AccessibleKey.READ_NEXT_ITEM.IsPressed()) MoveAction(m_action + 1);
        else if (AccessibleKey.READ_PREV_ITEM.IsPressed()) MoveAction(m_action - 1);
        else if (AccessibleKey.CONFIRM.IsPressed() || AccessibleKey.SPACE.IsPressed()) Activate();
    }

    public string GetHelp() => LocalizationUtils.Get(LocalizationKey.SOCIAL_INBOX_HELP);

    // Also on return from a modal (a removal or Clear All changed the list).
    public void OnGainedFocus() => m_pending = true;

    private void Close()
    {
        m_controller = null;
        m_rows.Clear();
        AccessibilityMgr.HideUI(this);
    }

    private void Rebuild()
    {
        m_rows.Clear();
        var items = ReadField<List<IInboxMessageItem>>(m_controller, "_messageListUI");
        if (items != null)
            foreach (var entry in items)
                if (entry is InboxMessageListItem item && item != null) m_rows.Add(BuildRow(item));

        var clearAll = ReadField<Button>(m_controller, "_clearAllButton");
        if (m_rows.Count > 0 && clearAll != null && clearAll.isActiveAndEnabled)
        {
            var row = new Row();
            row.Actions.Add(clearAll);
            row.Labels.Add(SocialUi.HasWordedLabel(clearAll) ? KeyboardNavigator.ResolveBaseLabel(clearAll) : LocalizationUtils.Get(LocalizationKey.SOCIAL_INBOX_CLEAR_ALL));
            m_rows.Add(row);
        }
        m_row = Mathf.Clamp(m_row, 0, Mathf.Max(0, m_rows.Count - 1));
        m_action = 0;
    }

    private static Row BuildRow(InboxMessageListItem item)
    {
        var row = new Row { Item = item };
        void Add(Button? b, string label)
        {
            if (b == null || !b.isActiveAndEnabled || row.Actions.Contains(b)) return;
            row.Actions.Add(b);
            row.Labels.Add(label);
        }
        Add(ReadField<Button>(item, "_bgButton"), LocalizationUtils.Get(LocalizationKey.SOCIAL_INBOX_DETAILS));
        var configs = ReadField<InboxMessageListItem.ButtonCTAConfig[]>(item, "_buttonCTAConfigs");
        if (configs != null)
            foreach (var c in configs)
            {
                if (c?.button == null) continue;
                var text = c.textComponent != null ? c.textComponent.text?.Trim() : null;
                Add(c.button, !string.IsNullOrEmpty(text) ? text! : LocalizationUtils.TryGetGame(c.buttonText) ?? KeyboardNavigator.ResolveBaseLabel(c.button));
            }
        Add(ReadField<Button>(item, "_removeButton"), LocalizationUtils.GetGame("global_remove", LocalizationKey.SOCIAL_INBOX_REMOVE));
        return row;
    }

    private void AnnounceList()
    {
        var messages = m_rows.Count(r => r.Item != null);
        if (!m_titleSpoken)
        {
            m_titleSpoken = true;
            var unread = m_rows.Count(r => r.Item?.GetInboxData() is { HasBeenRead: false });
            AccessibilityMgr.Output(this, LocalizationUtils.Format(LocalizationKey.SOCIAL_INBOX_COUNT_FORMAT, LocalizationUtils.GetGame("overflow_inbox", LocalizationKey.SOCIAL_INBOX), messages, unread), interrupt: true);
        }
        if (messages == 0)
        {
            AccessibilityMgr.Output(this, EmptyMessage(), interrupt: false);
            return;
        }
        SpeakRow(interrupt: false);
    }

    private string EmptyMessage()
    {
        var empty = ReadField<GameObject>(m_controller, "_emptyInbox");
        var text = empty != null ? empty.GetComponentsInChildren<TMP_Text>(includeInactive: false).Select(t => t.text?.Trim()).FirstOrDefault(s => !string.IsNullOrEmpty(s)) : null;
        return text ?? LocalizationUtils.Get(LocalizationKey.SOCIAL_INBOX_EMPTY);
    }

    private void MoveRow(int index)
    {
        if (index < 0 || index >= m_rows.Count) return;
        m_row = index;
        m_action = 0;
        ScrollToRow();
        SpeakRow(interrupt: true);
    }

    private void MoveAction(int index)
    {
        var labels = m_rows[m_row].Labels;
        if (index < 0 || index >= labels.Count) return;
        m_action = index;
        AccessibilityMgr.Output(this, Announce.Item(labels[index], description: null, index: index + 1, total: labels.Count), interrupt: true);
    }

    private void Activate()
    {
        var row = m_rows[m_row];
        if (m_action >= row.Actions.Count) return;
        var button = row.Actions[m_action];
        if (button != null && button.IsInteractable()) button.onClick.Invoke();
        // Removing a row or opening a modal changes what is on screen; next frame reads the live list.
        m_pending = true;
    }

    // The game marks a message read once it has scrolled into view, so scroll as a sighted player would.
    private void ScrollToRow()
    {
        var scroll = ReadField<ScrollRect>(m_controller, "_scrollRect");
        if (scroll != null && m_rows.Count > 1) scroll.verticalNormalizedPosition = 1f - (float)m_row / (m_rows.Count - 1);
    }

    private void SpeakRow(bool interrupt)
    {
        var row = m_rows[m_row];
        if (row.Item == null)
        {
            AccessibilityMgr.Output(this, row.Labels[0], interrupt);
            return;
        }
        var parts = new List<string?>
        {
            ReadField<TMP_Text>(row.Item, "_titleText")?.text,
            ReadField<TMP_Text>(row.Item, "_descriptionText")?.text,
            ReadField<TMP_Text>(row.Item, "_readableUntilText")?.text,
            row.Item.GetInboxData() is { HasBeenRead: false } ? LocalizationUtils.Get(LocalizationKey.SOCIAL_INBOX_UNREAD) : null,
        };
        var label = string.Join(". ", parts.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p!.Trim()));
        var messages = m_rows.Count(r => r.Item != null);
        AccessibilityMgr.Output(this, Announce.Item(label, description: null, index: m_row + 1, total: messages), interrupt);
    }

    [HarmonyPatch(typeof(HUBScreenController), "OnActivate", new[] { typeof(HUBGroupController) })]
    private static class ActivateHook
    {
        private static void Postfix(HUBScreenController __instance)
        {
            if (__instance is not HUBInboxScreenController inbox) return;
            s_instance.m_controller = inbox;
            s_instance.m_row = 0;
            s_instance.m_titleSpoken = false;
            PopupUtils.ShowOrRefresh(s_instance);
        }
    }

    [HarmonyPatch(typeof(HUBScreenController), "OnDeactivate", new[] { typeof(HUBGroupController) })]
    private static class DeactivateHook
    {
        private static void Postfix(HUBScreenController __instance)
        {
            if (s_instance.m_controller != null && __instance == s_instance.m_controller) s_instance.Close();
        }
    }

    // New mail only makes a sound in the game (a distinct one for emergency maintenance). This is the method that
    // plays it, so wherever the player is they hear about it too.
    [HarmonyPatch(typeof(MainMenuController), "PlayInboxAudio")]
    private static class NewMessageHook
    {
        private static void Postfix()
        {
            var unseen = ManagerSingleton<InboxEvents>.instance?.GetMessageSet(isSeen: false)?.ToList();
            if (unseen == null) return;
            if (unseen.Count > s_lastUnseen)
            {
                AccessibilityMgr.OutputNotification(LocalizationUtils.Format(LocalizationKey.SOCIAL_INBOX_NEW_FORMAT, unseen.Count));
                foreach (var m in unseen.OfType<InboxEmergencyMaintenance>())
                    if ((LocalizationUtils.TryGetGame(m.Body) ?? m.Body) is { Length: > 0 } body) AccessibilityMgr.OutputNotification(body);
            }
            s_lastUnseen = unseen.Count;
        }
    }
}
