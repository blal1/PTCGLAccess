using System;
using System.Collections.Generic;
using System.IO;
using TPCI.Localization;

namespace Accessibility;

// The mod speaks the game's language: Get(LocalizationKey.X) / Format(LocalizationKey.X, args) read
// Localization/<tag>/*.txt for the game's current language tag (en, fr, de, es, it, ptbr, ru, ja), falling back
// to English for a missing language or line. GetGame("game_resource_id", fallbackKey) prefers the game's own
// string: it is official and covers every language the game ships.
//
// The language is re-read once a second on the main thread; the lookups never touch Unity. Never cache a
// translated string in a static field: it would keep the language that was active when it was first read.
internal static class LocalizationUtils
{
    private const string FallbackLanguage = "en";

    private static readonly Dictionary<string, LocalizationTable?> s_tables = new();
    private static string s_language = FallbackLanguage;
    private static float s_nextLanguageCheck;

    public static string Language => s_language;

    public static string Get(LocalizationKey key)
    {
        if (Table(s_language)?.TryGet(key.Key, out var text) == true) return text;
        if (Table(FallbackLanguage)?.TryGet(key.Key, out text) == true) return text;
        Log.Warn($"Localization: no text for {key}");
        return key.Key;
    }

    public static string Format(LocalizationKey key, params object?[] args)
    {
        var format = Get(key);
        try { return string.Format(format, args); }
        catch (FormatException)
        {
            // A broken translation must never cost the announcement.
            Log.Warn($"Localization: bad placeholders in the '{s_language}' text of {key}");
            return string.Format(Table(FallbackLanguage)!.TryGet(key.Key, out var en) ? en : key.Key, args);
        }
    }

    public static string GetGame(string resourceId, LocalizationKey fallback) => TryGetGame(resourceId) ?? Get(fallback);

    // The game's own string, or null when the id is empty or the game has no text for it (it answers a missing id
    // with a "(Missing)" / "MISSING STRING" marker that must never be spoken). For game data ids (set, rarity,
    // league names) where the caller has its own fallback.
    public static string? TryGetGame(string? resourceId)
    {
        if (string.IsNullOrEmpty(resourceId)) return null;
        string? text = null;
        try { text = LocalizationManager.Instance?.GetString(resourceId); }
        catch (Exception e) { Log.Warn($"Localization: game string {resourceId} threw {e.GetType().Name}"); }
        if (string.IsNullOrWhiteSpace(text) || text!.Contains("(Missing)") || text.StartsWith("MISSING STRING")) return null;
        return text;
    }

    // Called every frame by AccessibilityMgr; polls the game once a second.
    internal static void Update()
    {
        if (UnityEngine.Time.unscaledTime < s_nextLanguageCheck) return;
        s_nextLanguageCheck = UnityEngine.Time.unscaledTime + 1f;
        try
        {
            var tag = LocalizationManager.Instance?.GetCurrentLanguage();
            if (!string.IsNullOrEmpty(tag) && tag != s_language)
            {
                s_language = tag!;
                Log.Info($"Localization: game language is now '{s_language}'");
            }
        }
        catch { /* manager not up yet: keep the previous language */ }
    }

    // Resources are named Localization/<tag>/<Feature>.txt (see the csproj).
    // Locked: game event hooks may speak from a worker thread.
    private static LocalizationTable? Table(string language)
    {
        lock (s_tables) return LoadTable(language);
    }

    private static LocalizationTable? LoadTable(string language)
    {
        if (s_tables.TryGetValue(language, out var cached)) return cached;
        LocalizationTable? table = null;
        var asm = typeof(LocalizationUtils).Assembly;
        var prefix = $"Localization/{language}/";
        foreach (var name in asm.GetManifestResourceNames())
        {
            if (!name.Replace('\\', '/').StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            using var reader = new StreamReader(asm.GetManifestResourceStream(name)!);
            (table ??= new LocalizationTable()).Add(reader.ReadToEnd());
        }
        Log.Info(table == null ? $"Localization: no '{language}' text shipped, using English" : $"Localization: loaded {table.Entries.Count} '{language}' lines");
        s_tables[language] = table;
        return table;
    }
}
