using System;
using System.Collections.Concurrent;
using System.Reflection;

namespace Accessibility;

// The one way the mod reads a non-public game field by name (`using static Accessibility.GameReflect;`).
// Lookups are cached; a name a game update removed is reported to Compat once instead of going silent.
// Tests/GameCompatTests resolves every ReadField name against the installed game before anyone launches it.
internal static class GameReflect
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    private static readonly ConcurrentDictionary<(Type, string), FieldInfo?> s_fields = new();

    public static T? ReadField<T>(object? instance, string name) where T : class
    {
        if (instance == null) return null;
        var type = instance.GetType();
        var field = s_fields.GetOrAdd((type, name), key => Find(key.Item1, key.Item2));
        if (field == null)
        {
            Compat.MissingAtRuntime(type, name);
            return null;
        }
        return field.GetValue(instance) as T;
    }

    private static FieldInfo? Find(Type type, string name)
    {
        for (var t = type; t != null; t = t.BaseType)
        {
            var f = t.GetField(name, Flags);
            if (f != null) return f;
        }
        return null;
    }
}
