using Accessibility;
using MelonLoader;

[assembly: MelonInfo(typeof(PokemonTCGLiveUpdaterAccessMod), BuildInfo.Name, BuildInfo.Version, BuildInfo.Author)]
// The standalone updater's app.info reads `pokemon` / `Updater` (Updater\<ver>\Updater_Data\app.info) — must match
// exactly or MelonLoader rejects the mod as incompatible, same rule as the main game's MelonGame attribute.
[assembly: MelonGame("pokemon", "Updater")]

namespace Accessibility;

public static class BuildInfo
{
    public const string Name = "Pokemon TCG Live Updater Access";
    public const string Author = "PTCGL Access contributors";
    public const string Version = "0.2.0";
}

// Entry point for Updater.exe. The updater is a single screen, so there is no AccessibilityMgr: speech, the window
// fix, and one polled screen (AccessibleUpdater).
public sealed class PokemonTCGLiveUpdaterAccessMod : MelonMod
{
    private readonly WindowFix _windowFix = new();
    private readonly AccessibleUpdater _screen = new();

    public override void OnInitializeMelon()
    {
        Log.Init(LoggerInstance);
        Log.Info($"{BuildInfo.Name} {BuildInfo.Version} starting");
        ScreenReader.Load();
    }

    public override void OnUpdate()
    {
        _windowFix.Tick();
        LocalizationUtils.Update();
        _screen.Tick();
    }

    // Same order as the main mod: drop the WndProc subclass before the managed domain goes away, or Windows
    // dispatches teardown messages into dead managed code and the process crashes on exit. Both quit paths, idempotent.
    public override void OnApplicationQuit() => Cleanup();

    public override void OnDeinitializeMelon() => Cleanup();

    private void Cleanup()
    {
        _windowFix.Shutdown();
        ScreenReader.Unload();
    }
}
