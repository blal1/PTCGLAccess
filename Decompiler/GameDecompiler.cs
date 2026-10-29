using System.Security.Cryptography;
using CommonUtils;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.CSharp.ProjectDecompiler;
using ICSharpCode.Decompiler.Metadata;

namespace Decompiler;

public sealed class GameDecompiler
{
    // Engine, framework and third-party assemblies: not the game's own code, and huge. Everything else in Managed is decompiled.
    private static readonly string[] s_skippedPrefixes =
    {
        "Unity", "System", "Mono.", "mscorlib", "netstandard", "Microsoft.", "Newtonsoft.", "Google.", "Firebase",
        "Facebook", "AppsFlyer", "Sentry", "DOTween", "Cinemachine", "Coffee.", "Spine", "TextMeshPro", "Rewired",
    };

    private readonly string _gameDir;
    private readonly string _managedDir;
    private readonly string _decompiledDir;

    public GameDecompiler(string gameDir, string decompiledDir)
    {
        _gameDir = gameDir;
        _managedDir = GameUtils.GetManagedDir(gameDir);
        _decompiledDir = decompiledDir;
    }

    public void Run()
    {
        var version = GameUtils.ExtractGameVersion(_gameDir);
        Console.WriteLine($"Game version {version}, {Constants.MainGameAssembly} SHA-1 {Checksum(Path.Combine(_managedDir, Constants.MainGameAssembly))}");

        PrepareBranch(version);

        var assemblies = Directory.GetFiles(_managedDir, "*.dll").Where(IsGameAssembly).OrderBy(p => p).ToList();
        for (var i = 0; i < assemblies.Count; i++)
        {
            Console.WriteLine($"({i + 1}/{assemblies.Count}) {Path.GetFileName(assemblies[i])}");
            Decompile(assemblies[i]);
        }

        File.WriteAllText(Path.Combine(_decompiledDir, "GAME_VERSION"), version + Environment.NewLine);
        GitUtils.CommitAll(_decompiledDir, $"Decompiled {version}");
        Console.WriteLine($"Decompiled Pokémon TCG Live {version} into {_decompiledDir} (branch {version}).");
    }

    // One branch per game version, forked from the previous one, so `git diff <old> <new>` shows exactly what an update changed.
    private void PrepareBranch(string version)
    {
        if (!GitUtils.IsClean(_decompiledDir))
        {
            ProgramUtils.ExitProgramWith($"Your {_decompiledDir} directory has uncommitted changes. Commit or discard them first.");
        }
        if (GitUtils.BranchExists(_decompiledDir, version))
        {
            ProgramUtils.ExitProgramWith($"Version {version} has already been decompiled.",
                $"To decompile it again: cd {_decompiledDir} && git checkout <another branch> && git branch -D {version}");
        }
        GitUtils.CheckoutNewBranch(_decompiledDir, version);
        GitUtils.CleanupDirectory(_decompiledDir);
    }

    private static bool IsGameAssembly(string path)
    {
        var name = Path.GetFileName(path);
        return !s_skippedPrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase));
    }

    private void Decompile(string assemblyPath)
    {
        var outputDir = Path.Combine(_decompiledDir, Path.GetFileNameWithoutExtension(assemblyPath));
        Directory.CreateDirectory(outputDir);
        try
        {
            using var module = new PEFile(assemblyPath);
            var resolver = new UniversalAssemblyResolver(assemblyPath, false, module.Metadata.DetectTargetFrameworkId());
            resolver.AddSearchDirectory(_managedDir);
            var settings = new DecompilerSettings(LanguageVersion.CSharp9_0)
            {
                UseSdkStyleProjectFormat = WholeProjectDecompiler.CanUseSdkStyleProjectFormat(module),
                UseNestedDirectoriesForNamespaces = true,
            };
            new WholeProjectDecompiler(settings, resolver, resolver, null).DecompileProject(module, outputDir);
        }
        catch (Exception e)
        {
            // One unreadable assembly (native stub, obfuscated helper) must not cost the whole version.
            Console.WriteLine($"  skipped: {e.GetType().Name}: {e.Message}");
            Directory.Delete(outputDir, true);
        }
    }

    private static string Checksum(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA1.HashData(stream));
    }
}
