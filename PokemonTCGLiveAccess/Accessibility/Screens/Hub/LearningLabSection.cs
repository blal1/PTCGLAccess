using System;
using System.Collections.Generic;
using System.Reflection;
using _Rainier.Features.LearningLab;
using HarmonyLib;
using SharedLogicUtils.source.LearningLab;
using UnityEngine.UI;
using static Accessibility.GameReflect;

namespace Accessibility;

// The Learning Lab lesson list, modeled as a SECTION of the HOME screen (it lives under HOME's LEARN game-mode tab).
// AccessibleHome adds one of these as its main-content section whenever the list is showing, so Down from the
// game-mode tabs (or Enter on the active LEARN tab) drops into the lessons and Up returns to the tabs — exactly like
// every other HOME section boundary.
//
// It's a DRILL-DOWN: each level is a ClassroomData whose LearningLabData holds child classrooms (BASIC LESSONS, …)
// and/or lessons. Like the deck library we navigate the DATA (captured from SetupDisplay), not the virtualized
// scroller cells. The SetupDisplay hook is the single source of truth for "the level changed" — open / drill-in /
// back all route through it — and HOME forwards that to the live section so a drill re-reads the new level.
//
// Keys: Up/Down move between items (at the edges HOME escapes to the adjacent section), Right/Left read the item's
// description, Enter drills in or starts the lesson, Backspace/Escape goes up a level (to the tabs at the top).
internal sealed class LearningLabSection : AccessibleElement, INavSection
{
    // ---- static state, fed by the SetupDisplay hook ----
    public static LearningLabClassroomListDisplay? Display { get; private set; }
    public static LearningLabScreenController? Controller { get; private set; }
    public static ClassroomData? Current { get; private set; }
    public static event Action? LevelChanged;

    // The list is "showing" only while its GameObject is live in the hierarchy (LEARN selected).
    public static bool IsShowing => Display != null && Display.gameObject.activeInHierarchy && Current != null;

    private static readonly MethodInfo? s_classroomButtonAction = AccessTools.Method(typeof(LearningLabScreenController), "ClassroomButtonAction");

    [HarmonyPatch(typeof(LearningLabClassroomListDisplay), "SetupDisplay")]
    private static class SetupDisplayHook
    {
        private static void Postfix(LearningLabClassroomListDisplay __instance, ClassroomData classroomData)
        {
            Display = __instance;
            Controller = ReadField<LearningLabScreenController>(__instance, "learningLabScreenController");
            Current = classroomData;
            LevelChanged?.Invoke();
        }
    }

    // ---- instance: the section nav ----
    private readonly Action m_escapeToParent;
    private int m_index;
    private int m_lineIndex = -1;
    private readonly List<string> m_lines = new();

    public LearningLabSection(AccessibleComponent parent, Action escapeToParent) : base(parent)
    {
        m_escapeToParent = escapeToParent;
    }

    private static List<ILearningLabData> Items => Current?.LearningLabData ?? new List<ILearningLabData>();

    public int Index => m_index;
    public int Count => Items.Count;
    public bool IsAtTopRow => m_index <= 0;               // single column → top row is item 0
    public bool IsAtBottomRow => m_index >= Count - 1;    // …and bottom row is the last item
    public int CurrentColumn => 0;

    public void FocusFirst(bool announce = true, bool interruptPrior = true)
    {
        m_index = 0;
        m_lineIndex = -1;
        if (announce) AnnounceItem(interruptPrior, withLevelName: true);
    }

    // Entering from an adjacent section. Per-section memory: keep our own last lesson position if it's still valid,
    // so leaving and coming back lands where the player was. (Level changes reset the index via OnLevelChanged.)
    public void FocusFirstRow(int preferredColumn = -1, bool announce = true, bool interruptPrior = true)
    {
        if (m_index < 0 || m_index >= Count) m_index = 0;
        m_lineIndex = -1;
        if (announce) AnnounceItem(interruptPrior, withLevelName: true);
    }

    public void FocusLastRow(int preferredColumn = -1, bool announce = true, bool interruptPrior = true)
    {
        if (m_index < 0 || m_index >= Count) m_index = Math.Max(0, Count - 1);
        m_lineIndex = -1;
        if (announce) AnnounceItem(interruptPrior, withLevelName: true);
    }

    public void RefocusCurrent(bool announce = true, bool interrupt = true)
    {
        if (m_index < 0 || m_index >= Count) m_index = 0;
        if (announce) AnnounceItem(interrupt, withLevelName: false);
    }

    // A drill-in or a back-out changed the level: reset to the new level and read it.
    public void OnLevelChanged()
    {
        m_index = 0;
        m_lineIndex = -1;
        AnnounceItem(interrupt: true, withLevelName: true);
    }

    // Up/Down at the section's edge are consumed by AccessibleHome for section escape; we only see them mid-list.
    public bool HandleInput()
    {
        if (AccessibleKey.READ_NEXT_MENU_OPTION.IsPressed()) Move(+1);
        else if (AccessibleKey.READ_PREV_MENU_OPTION.IsPressed()) Move(-1);
        else if (AccessibleKey.READ_NEXT_ITEM.IsPressed()) ReadLine(+1);
        else if (AccessibleKey.READ_PREV_ITEM.IsPressed()) ReadLine(-1);
        else if (AccessibleKey.CONFIRM.IsPressed()) Activate();
        else if (AccessibleKey.BACK.IsPressed() || AccessibleKey.ESCAPE.IsPressed()) Back();
        else return false;
        return true;
    }

    private void Move(int dir)
    {
        var next = m_index + dir;
        if (next < 0 || next >= Count) return;
        m_index = next;
        m_lineIndex = -1;
        AnnounceItem(interrupt: true, withLevelName: false);
    }

    private void Activate()
    {
        var items = Items;
        if (m_index < 0 || m_index >= items.Count || Controller == null) return;
        var item = items[m_index];
        // Drill in → the controller re-runs SetupDisplay → our hook → OnLevelChanged reads it.
        if (item is ClassroomData c) s_classroomButtonAction?.Invoke(Controller, new object[] { c });
        else if (item is LessonData l) Controller.RunLesson(l.LessonControllerName, l.LessonKey, m_index);
    }

    private void Back()
    {
        // NavigationStepBack at the top level empties the nav stack (TryPop then a failing TryPeek), so gate on the
        // game's own backButton.interactable (true only when there's a level to return to). At the top level, hand
        // control back up to the game-mode tabs.
        var backBtn = Display != null ? ReadField<Button>(Display, "backButton") : null;
        if (backBtn != null && backBtn.interactable) Controller?.NavigationStepBack();
        else m_escapeToParent();
    }

    private void AnnounceItem(bool interrupt, bool withLevelName)
    {
        var items = Items;
        m_lines.Clear();
        m_lineIndex = -1;
        if (m_index < 0 || m_index >= items.Count)
        {
            Output(withLevelName ? LocalizationUtils.Format(LocalizationKey.HUB_EMPTY_FORMAT, LevelName()) : LocalizationUtils.Get(LocalizationKey.HUB_EMPTY), interrupt);
            return;
        }
        var text = Announce.Item(ItemLabel(items[m_index]), null, m_index + 1, items.Count);
        if (withLevelName)
        {
            Output(LevelName(), interrupt);
            Output(text, interrupt: false);
        }
        else Output(text, interrupt);
    }

    private void ReadLine(int dir)
    {
        var items = Items;
        if (m_index < 0 || m_index >= items.Count) return;
        if (m_lines.Count == 0) BuildLines(items[m_index]);
        var next = m_lineIndex + dir;
        if (next < -1 || next >= m_lines.Count) return;
        m_lineIndex = next;
        Output(next == -1 ? ItemLabel(items[m_index]) : m_lines[next], interrupt: true);
    }

    private void BuildLines(ILearningLabData item)
    {
        m_lines.Clear();
        var desc = item is ClassroomData c ? Loc(c.DescriptionLocId) : item is LessonData l ? Loc(l.DescriptionLocId) : null;
        if (!string.IsNullOrWhiteSpace(desc)) m_lines.Add(desc!);
    }

    private static string LevelName() => Loc(Current?.NameLocId) ?? LocalizationUtils.Get(LocalizationKey.HUB_LEARNING_LAB);

    private static string ItemLabel(ILearningLabData item)
    {
        if (item is ClassroomData c)
        {
            var (done, total) = Progress(c);
            var name = Loc(c.NameLocId) ?? c.NameLocId;
            return Difficulty(c.Difficulty) is { } d
                ? LocalizationUtils.Format(LocalizationKey.HUB_LL_CLASSROOM_FORMAT, name, d, done, total)
                : LocalizationUtils.Format(LocalizationKey.HUB_LL_CLASSROOM_NO_DIFFICULTY_FORMAT, name, done, total);
        }
        if (item is LessonData l)
        {
            var name = Loc(l.NameLocId) ?? l.NameLocId;
            return LocalizationUtils.Format(l.LessonStatus == LessonStatus.Complete ? LocalizationKey.HUB_LL_LESSON_COMPLETE : LocalizationKey.HUB_LL_LESSON_NOT_COMPLETE, name);
        }
        return LocalizationUtils.Get(LocalizationKey.HUB_ITEM);
    }

    private static string? Difficulty(DifficultyType d) => d switch
    {
        DifficultyType.beginner => LocalizationUtils.Get(LocalizationKey.HUB_DIFFICULTY_BEGINNER),
        DifficultyType.novice => LocalizationUtils.Get(LocalizationKey.HUB_DIFFICULTY_NOVICE),
        DifficultyType.intermediate => LocalizationUtils.Get(LocalizationKey.HUB_DIFFICULTY_INTERMEDIATE),
        DifficultyType.advanced => LocalizationUtils.Get(LocalizationKey.HUB_DIFFICULTY_ADVANCED),
        DifficultyType.expert => LocalizationUtils.Get(LocalizationKey.HUB_DIFFICULTY_EXPERT),
        DifficultyType.master => LocalizationUtils.Get(LocalizationKey.HUB_DIFFICULTY_MASTER),
        _ => null,
    };

    private static (int done, int total) Progress(ClassroomData c)
    {
        int done = 0, total = 0;
        void Walk(List<ILearningLabData> list)
        {
            foreach (var d in list)
            {
                if (d is LessonData ld)
                {
                    total++;
                    if (ld.LessonStatus == LessonStatus.Complete) done++;
                }
                else if (d is ClassroomData cd) Walk(cd.LearningLabData);
            }
        }
        Walk(c.LearningLabData);
        return (done, total);
    }

    private static string? Loc(string? id) => LocalizationUtils.TryGetGame(id)?.Trim();
}
