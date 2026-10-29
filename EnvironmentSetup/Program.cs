using CommonUtils;

// One-time setup: checks the linked game is ready for the mod, creates the private Decompiled repository
// and decompiles the installed version into it.
// Usage: dotnet run --project EnvironmentSetup
ProgramUtils.EnsureRunFromRepositoryRoot();
var gameDir = ProgramUtils.EnsureGameLinked();

var problems = new List<string>();
if (!GameUtils.IsMelonLoaderInstalled(gameDir))
{
    problems.Add("MelonLoader 0.7+ is not installed in the game folder: run the MelonLoader installer on Pokemon TCG Live.exe.");
}
if (!File.Exists(Path.Combine(gameDir, "prism.dll")))
{
    problems.Add("prism.dll is missing next to the game executable: speech will be disabled until you add it (see README).");
}
if (!GameUtils.IsAsciiPath(Path.GetFullPath(gameDir)) && !GameUtils.IsAsciiPath(new DirectoryInfo(gameDir).LinkTarget ?? ""))
{
    problems.Add("The game path contains non-ASCII characters: MelonLoader will not load. Link an ASCII junction instead.");
}
foreach (var p in problems) Console.WriteLine($"Warning: {p}");

if (GitUtils.IsRepository(Constants.DecompiledDir))
{
    ProgramUtils.ExitProgramWith($"{Constants.DecompiledDir} already exists. To decompile a new game version, run: dotnet run --project Decompiler");
}

GitUtils.Init(Constants.DecompiledDir);
new Decompiler.GameDecompiler(gameDir, Constants.DecompiledDir).Run();

Console.WriteLine("Environment ready. Build and deploy the mod with: dotnet build PokemonTCGLiveAccess");
