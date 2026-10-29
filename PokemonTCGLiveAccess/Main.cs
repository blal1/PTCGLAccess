using Accessibility;
using MelonLoader;

[assembly: MelonInfo(typeof(PokemonTCGLiveAccessMod), BuildInfo.Name, BuildInfo.Version, BuildInfo.Author)]
[assembly: MelonGame("pokemon", "Pokemon TCG Live")]

namespace Accessibility;

public static class BuildInfo
{
    public const string Name = "Pokemon TCG Live Access";
    public const string Author = "PTCGL Access contributors";
    public const string Version = "0.2.0";
}

// Entry point. Everything game-specific lives in [HarmonyPatch] hook classes that hand screens and UIs to
// AccessibilityMgr; this class only starts the pieces and drives the per-frame update.
public sealed class PokemonTCGLiveAccessMod : MelonMod
{
    private readonly WindowFix _windowFix = new();

    public override void OnInitializeMelon()
    {
        Log.Init(LoggerInstance);
        Log.Info($"{BuildInfo.Name} {BuildInfo.Version} starting");

        AccessibilityMgr.Initialize(typeof(PokemonTCGLiveAccessMod).Assembly);
        Compat.PatchAll(HarmonyInstance, typeof(PokemonTCGLiveAccessMod).Assembly);
        Compat.ScanStatics(typeof(PokemonTCGLiveAccessMod).Assembly);
        Compat.ReportStartup();
    }

    public override void OnUpdate()
    {
        _windowFix.Tick();
        AccessibilityMgr.Update();
    }

    // Un-subclass the window before the managed domain goes away: our WndProc left installed crashed the game on
    // every exit. MelonLoader may use either quit path, and both cleanups are idempotent.
    public override void OnApplicationQuit() => Cleanup();

    public override void OnDeinitializeMelon() => Cleanup();

    private void Cleanup()
    {
        _windowFix.Shutdown();
        AccessibilityMgr.Shutdown();
    }
}
