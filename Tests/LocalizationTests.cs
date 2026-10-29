using System.Text.RegularExpressions;
using Accessibility;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Tests;

// Every LocalizationKey has English text, every text line has a key, and translations keep their placeholders —
// for each mod project (the main mod and the updater mod), against that project's own keys and Localization folder.
public class LocalizationTests
{
    public static TheoryData<string> Projects => new(ModSources.SpeakingProjects);

    private static IReadOnlyList<SyntaxTree> Trees(string project) =>
        project == "PokemonTCGLiveAccess" ? ModSources.Trees : ModSources.Parse(ModSources.FindProjectDir(project));

    // Keys are declared as `new(nameof(KEY))` fields of the partial LocalizationKey class.
    private static HashSet<string> DeclaredKeys(string project) => Trees(project)
        .SelectMany(t => t.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>())
        .Where(c => c.Identifier.Text == "LocalizationKey")
        .SelectMany(c => c.Members.OfType<FieldDeclarationSyntax>())
        .SelectMany(f => f.Declaration.Variables.Select(v => v.Identifier.Text))
        .ToHashSet();

    private static string LocalizationDir(string project) => Path.Combine(ModSources.FindProjectDir(project), "Localization");

    private static Dictionary<string, (string Text, string File)> Load(string project, string language)
    {
        var result = new Dictionary<string, (string, string)>();
        foreach (var file in Directory.GetFiles(Path.Combine(LocalizationDir(project), language), "*.txt"))
        {
            var table = new LocalizationTable();
            table.Add(File.ReadAllText(file));
            foreach (var (key, text) in table.Entries)
            {
                Assert.False(result.ContainsKey(key), $"{key} is defined twice ({language}/{Path.GetFileName(file)})");
                result[key] = (text, Path.GetFileName(file));
            }
        }
        return result;
    }

    [Theory]
    [MemberData(nameof(Projects))]
    public void Every_key_has_english_text_and_every_english_line_has_a_key(string project)
    {
        var declared = DeclaredKeys(project);
        Assert.NotEmpty(declared);
        var en = Load(project, "en");
        var missing = declared.Where(k => !en.ContainsKey(k)).OrderBy(k => k).ToList();
        var orphans = en.Keys.Where(k => !declared.Contains(k)).OrderBy(k => k).ToList();
        Assert.True(missing.Count == 0, "Keys without English text: " + string.Join(", ", missing));
        Assert.True(orphans.Count == 0, "English lines without a LocalizationKey: " + string.Join(", ", orphans));
    }

    [Theory]
    [MemberData(nameof(Projects))]
    public void Translations_cover_english_and_keep_placeholders(string project)
    {
        var en = Load(project, "en");
        foreach (var dir in Directory.GetDirectories(LocalizationDir(project)).Select(Path.GetFileName).Where(l => l != "en"))
        {
            var tr = Load(project, dir!);
            var untranslated = en.Keys.Where(k => !tr.ContainsKey(k)).OrderBy(k => k).ToList();
            Assert.True(untranslated.Count == 0, $"{dir}: no translation for " + string.Join(", ", untranslated));
            foreach (var (key, (text, file)) in tr)
            {
                Assert.True(en.ContainsKey(key), $"{dir}/{file}: {key} does not exist in English");
                Assert.Equal(Placeholders(en[key].Text), Placeholders(text));
            }
        }
    }

    // Lines spoken in the mod's own words must never be raw English string literals.
    [Theory]
    [MemberData(nameof(Projects))]
    public void Nothing_speaks_a_string_literal(string project)
    {
        var offenders = Trees(project)
            .SelectMany(t => t.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>()
                .Where(c => c.Expression.ToString() is "AccessibilityMgr.Output" or "AccessibilityMgr.OutputNotification" or "Output")
                .Where(c => c.ArgumentList.Arguments.Any(a =>
                    a.Expression is InterpolatedStringExpressionSyntax
                    || (a.Expression is LiteralExpressionSyntax l && l.IsKind(SyntaxKind.StringLiteralExpression))))
                .Select(c => $"{t.FilePath}:{c.GetLocation().GetLineSpan().StartLinePosition.Line + 1}"))
            .ToList();
        Assert.True(offenders.Count == 0, "Spoken literals (use LocalizationUtils): " + string.Join(", ", offenders));
    }

    private static string Placeholders(string text) =>
        string.Join(",", Regex.Matches(text, @"\{(\d+)[^}]*\}").Select(m => m.Groups[1].Value).Distinct().OrderBy(x => x));
}
