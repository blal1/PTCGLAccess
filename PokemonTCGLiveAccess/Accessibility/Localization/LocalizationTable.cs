using System.Collections.Generic;

namespace Accessibility;

// One language's text, parsed from the tab-separated files Hearthstone Access uses for translators:
//   KEY<TAB>TEXT<TAB>COMMENT     (a header line "TAG<TAB>TEXT<TAB>COMMENT", blank lines and # lines are skipped)
// \n and \t in TEXT are escapes. Pure — no Unity, no game types — so the test project compiles it directly.
internal sealed class LocalizationTable
{
    private readonly Dictionary<string, string> m_entries = new();

    public IReadOnlyDictionary<string, string> Entries => m_entries;

    public bool TryGet(string key, out string text) => m_entries.TryGetValue(key, out text!) && text.Length > 0;

    // Later files win on a duplicate key; the tests forbid duplicates anyway.
    public void Add(string fileText)
    {
        foreach (var raw in fileText.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0 || line[0] == '#' || line.StartsWith("TAG\t")) continue;
            var cols = line.Split('\t');
            if (cols.Length < 2 || cols[0].Length == 0) continue;
            m_entries[cols[0]] = cols[1].Replace("\\n", "\n").Replace("\\t", "\t");
        }
    }
}
