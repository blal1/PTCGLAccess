namespace CommonUtils;

public static class Constants
{
    public const string GameLinkDir = "Game";

    public const string DecompiledDir = "Decompiled";

    public const string GameExeName = "Pokemon TCG Live.exe";

    public const string GameDataDirName = "Pokemon TCG Live_Data";

    public const string ModProjectDir = "PokemonTCGLiveAccess";

    // Its presence in Managed proves a folder is the game; most of the game's code lives in it.
    public const string MainGameAssembly = "TPCI.RainierClient.dll";

    public static readonly string ExampleLinkMessage = $"Example: mklink /J {GameLinkDir} \"{GameUtils.TypicalInstallDir}\"";
}
