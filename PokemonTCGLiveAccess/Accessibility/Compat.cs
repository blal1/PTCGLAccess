using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;

namespace Accessibility;

// Game-update report. The mod reaches into the game by member name (Harmony targets, private fields); an update
// that renames one used to make a screen go silent with no explanation. Every such break is collected here:
//   - PatchAll(): each [HarmonyPatch] class is applied on its own, so one broken target never stops the others.
//   - ScanStatics(): every `static readonly` FieldInfo/MethodInfo resolved by name that came back null.
//   - MissingAtRuntime(): a GameReflect name that no longer exists, logged once each.
// The count is spoken once at startup and appended to F1; the log lists every member (COMPAT: lines).
// Offline, before launching a new game version: dotnet test Tests.
internal static class Compat
{
    private static readonly List<string> s_problems = new();
    private static readonly HashSet<string> s_runtimeSeen = new();

    public static void PatchAll(HarmonyLib.Harmony harmony, Assembly asm)
    {
        foreach (var type in SafeTypes(asm))
        {
            if (!type.GetCustomAttributes(typeof(HarmonyPatch), false).Any()) continue;
            try { harmony.CreateClassProcessor(type).Patch(); }
            catch (Exception e) { Add($"{type.Name}: patch failed ({(e.InnerException ?? e).Message})"); }
        }
    }

    public static void ScanStatics(Assembly asm)
    {
        foreach (var type in SafeTypes(asm))
        {
            try { RuntimeHelpers.RunClassConstructor(type.TypeHandle); }
            catch (Exception e)
            {
                Add($"{type.Name}: static init failed ({e.InnerException?.Message ?? e.Message})");
                continue;
            }
            foreach (var f in type.GetFields(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public))
            {
                if (f.IsInitOnly && typeof(MemberInfo).IsAssignableFrom(f.FieldType) && f.GetValue(null) == null)
                {
                    Add($"{type.Name}.{f.Name}: game member not found");
                }
            }
        }
    }

    public static void MissingAtRuntime(Type owner, string member)
    {
        var key = $"{owner.FullName}.{member}";
        lock (s_problems)
        {
            if (!s_runtimeSeen.Add(key)) return;
        }
        Add($"{key}: not found at runtime");
    }

    public static void ReportStartup()
    {
        lock (s_problems)
        {
            if (s_problems.Count == 0)
            {
                Log.Info("COMPAT: every game member the mod uses was found");
                return;
            }
            Log.Warn($"COMPAT: {s_problems.Count} problem(s) with this game version");
        }
        AccessibilityMgr.OutputNotification(Summary()!);
    }

    // Null when everything resolved.
    public static string? Summary()
    {
        int count;
        lock (s_problems) count = s_problems.Count;
        return count == 0 ? null : LocalizationUtils.Format(LocalizationKey.GLOBAL_COMPAT_PROBLEMS, count);
    }

    private static void Add(string problem)
    {
        lock (s_problems) s_problems.Add(problem);
        Log.Warn($"COMPAT: {problem}");
    }

    private static IEnumerable<Type> SafeTypes(Assembly asm)
    {
        try { return asm.GetTypes(); }
        catch (ReflectionTypeLoadException e) { return e.Types.Where(t => t != null)!; }
    }
}
