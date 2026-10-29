using System.Text.RegularExpressions;
using Accessibility;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Tests;

// Every LocalizationKey has English text, every text line has a key, and translations keep their placeholders.
public class LocalizationTests
{
    private static readonly string LocalizationDir = Path.Combine(ModSources.ModDir, "Localization");

    // Keys are declared as `new(nameof(KEY))` fields of the partial LocalizationKey class.
    private static readonly HashSet<string> DeclaredKeys = ModSources.Trees
        .SelectMany(t => t.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>())
        .Where(c => c.Identifier.Text == "LocalizationKey")
        .SelectMany(c => c.Members.OfType<FieldDeclarationSyntax>())
        .SelectMany(f => f.Declaration.Variables.Select(v => v.Identifier.Text))
        .ToHashSet();

    private static Dictionary<string, (string Text, string File)> Load(string language)
    {
        var result = new Dictionary<string, (string, string)>();
        foreach (var file in Directory.GetFiles(Path.Combine(LocalizationDir, language), "*.txt"))
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

    [Fact]
    public void Every_key_has_english_text_and_every_english_line_has_a_key()
    {
        Assert.NotEmpty(DeclaredKeys);
        var en = Load("en");
        var missing = DeclaredKeys.Where(k => !en.ContainsKey(k)).OrderBy(k => k).ToList();
        var orphans = en.Keys.Where(k => !DeclaredKeys.Contains(k)).OrderBy(k => k).ToList();
        Assert.True(missing.Count == 0, "Keys without English text: " + string.Join(", ", missing));
        Assert.True(orphans.Count == 0, "English lines without a LocalizationKey: " + string.Join(", ", orphans));
    }

    [Fact]
    public void Translations_cover_english_and_keep_placeholders()
    {
        var en = Load("en");
        foreach (var dir in Directory.GetDirectories(LocalizationDir).Select(Path.GetFileName).Where(l => l != "en"))
        {
            var tr = Load(dir!);
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
    [Fact]
    public void Nothing_speaks_a_string_literal()
    {
        var offenders = ModSources.Trees
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
