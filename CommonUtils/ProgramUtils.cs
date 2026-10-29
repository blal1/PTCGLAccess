namespace CommonUtils;

public static class ProgramUtils
{
    public static void ExitProgramWith(params string[] errorMessages)
    {
        foreach (var msg in errorMessages) Console.WriteLine(msg);
        Environment.Exit(1);
    }

    // Every tool runs from the repository root, where the Game link and the Decompiled directory live.
    public static void EnsureRunFromRepositoryRoot()
    {
        if (!Directory.Exists(Constants.ModProjectDir))
        {
            ExitProgramWith("Run this tool from the repository root, e.g.: dotnet run --project Decompiler");
        }
    }

    public static string EnsureGameLinked()
    {
        if (!Directory.Exists(Constants.GameLinkDir))
        {
            ExitProgramWith("Please link your game installation first: dotnet run --project LinkGameInstallation",
                Constants.ExampleLinkMessage);
        }
        if (!GameUtils.IsGameDirectory(Constants.GameLinkDir))
        {
            ExitProgramWith($"'{Constants.GameLinkDir}' does not point to a Pokémon TCG Live installation.", Constants.ExampleLinkMessage);
        }
        return Constants.GameLinkDir;
    }

    public static void EnsureSetupHasBeenRun()
    {
        if (!GitUtils.IsRepository(Constants.DecompiledDir))
        {
            ExitProgramWith("You need to set up your environment first.", "Please run: dotnet run --project EnvironmentSetup");
        }
    }
}
