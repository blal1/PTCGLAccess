using CommonUtils;

// Creates the "Game" junction at the repository root that every other tool and the mod build use.
// Usage: dotnet run --project LinkGameInstallation [path-to-game]
ProgramUtils.EnsureRunFromRepositoryRoot();

if (Directory.Exists(Constants.GameLinkDir))
{
    ProgramUtils.ExitProgramWith($"'{Constants.GameLinkDir}' already exists. Delete it first if you want to link another installation.");
}

string target;
if (args.Length > 0)
{
    target = Path.GetFullPath(args[0]);
    if (!GameUtils.IsGameDirectory(target))
    {
        ProgramUtils.ExitProgramWith($"'{target}' is not a Pokémon TCG Live installation (no {Constants.GameExeName} or {Constants.MainGameAssembly}).");
    }
}
else
{
    var candidates = GameUtils.FindGameCandidateDirectories();
    if (candidates.Count == 0)
    {
        ProgramUtils.ExitProgramWith("Could not find your Pokémon TCG Live installation. Pass its path as an argument.", Constants.ExampleLinkMessage);
    }
    if (candidates.Count > 1)
    {
        ProgramUtils.ExitProgramWith(new[] { "Found several installations, pass the one to use as an argument:" }.Concat(candidates).ToArray());
    }
    target = candidates[0];
}

// MelonLoader fails to bootstrap from a non-ASCII path; the mod build also deploys through this link.
if (!GameUtils.IsAsciiPath(target))
{
    Console.WriteLine($"Warning: '{target}' contains non-ASCII characters, which MelonLoader cannot load from.");
    Console.WriteLine(@"Create an ASCII junction to it first (e.g. mklink /J C:\PokemonTCGLive ""<install>"") and link that instead.");
}

ProcessUtils.MkJunction(Constants.GameLinkDir, target);
Console.WriteLine($"Linked {Constants.GameLinkDir} -> {target} (game version {GameUtils.ExtractGameVersion(target)})");
