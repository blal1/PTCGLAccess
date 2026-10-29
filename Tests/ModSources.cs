using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Tests;

// The mod's sources, parsed once, and for GameCompatTests a semantic model compiled against the INSTALLED game's
// assemblies (metadata only: nothing from the game runs).
internal static class ModSources
{
    public static readonly string ModDir = FindProjectDir("PokemonTCGLiveAccess");

    public static readonly IReadOnlyList<SyntaxTree> Trees = Parse(ModDir);

    // Every project that ships spoken text: the main mod and the updater mod (its own files only, not the linked ones).
    public static readonly string[] SpeakingProjects = { "PokemonTCGLiveAccess", "PokemonTCGLiveUpdaterAccess" };

    public static IReadOnlyList<SyntaxTree> Parse(string dir) => Directory
        .EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories)
        .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
        .Select(p => CSharpSyntaxTree.ParseText(File.ReadAllText(p), new CSharpParseOptions(LanguageVersion.Latest), path: Path.GetRelativePath(dir, p)))
        .ToList();

    public static string? GameDir()
    {
        var env = Environment.GetEnvironmentVariable("PTCGL_GAME_DIR");
        if (!string.IsNullOrEmpty(env)) return env;
        return typeof(ModSources).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "GameDir")?.Value;
    }

    private static CSharpCompilation? s_compilation;

    // Null when the game isn't installed where Directory.Build.props says.
    public static CSharpCompilation? Compilation()
    {
        if (s_compilation != null) return s_compilation;
        var game = GameDir();
        var managed = game == null ? null : Path.Combine(game, "Pokemon TCG Live_Data", "Managed");
        if (managed == null || !Directory.Exists(managed)) return null;

        var refs = Directory.EnumerateFiles(managed, "*.dll")
            .Concat(Directory.EnumerateFiles(Path.Combine(game!, "MelonLoader", "net35"), "*.dll"))
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
            .ToList();
        s_compilation = CSharpCompilation.Create("ModUnderTest", Trees, refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true, nullableContextOptions: NullableContextOptions.Enable)
                // Private members are what the mod reaches for; Roslyn skips them by default.
                .WithMetadataImportOptions(MetadataImportOptions.All));
        return s_compilation;
    }

    public static string FindProjectDir(string project)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            var dir = Path.Combine(d.FullName, project);
            if (File.Exists(Path.Combine(dir, project + ".csproj"))) return dir;
        }
        throw new InvalidOperationException($"{project} project not found above the test binaries");
    }
}
