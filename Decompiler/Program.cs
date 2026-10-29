using CommonUtils;

// Decompiles the linked game into a new branch of the Decompiled repository, named after the game version.
// Usage: dotnet run --project Decompiler
ProgramUtils.EnsureRunFromRepositoryRoot();
var gameDir = ProgramUtils.EnsureGameLinked();
ProgramUtils.EnsureSetupHasBeenRun();

new Decompiler.GameDecompiler(gameDir, Constants.DecompiledDir).Run();
