using System;

namespace Accessibility;

// The screen reader bridge (Prism: NVDA, JAWS, SAPI, ...). Only AccessibilityMgr calls it, so focus rules always apply.
internal static class ScreenReader
{
    private static IntPtr s_ctx;
    private static IntPtr s_backend;

    private static bool Ready => s_backend != IntPtr.Zero;

    internal static void Load()
    {
        try
        {
            s_ctx = Prism.Init(IntPtr.Zero);
            if (s_ctx == IntPtr.Zero)
            {
                Log.Error("ScreenReader: Prism.Init returned no context");
                return;
            }
            var backend = Prism.RegistryCreateBest(s_ctx);
            if (backend == IntPtr.Zero)
            {
                Log.Error("ScreenReader: no speech backend available");
                return;
            }
            var err = Prism.BackendInitialize(backend);
            if (err != Prism.PrismError.Ok && err != Prism.PrismError.AlreadyInitialized)
            {
                Log.Error($"ScreenReader: backend init failed: {err} ({Prism.ErrorString(err)})");
                Prism.BackendFree(backend);
                return;
            }
            s_backend = backend;
            Log.Info($"ScreenReader: using {Prism.BackendName(s_backend)}");
        }
        catch (DllNotFoundException)
        {
            Log.Error("ScreenReader: prism.dll not found next to the game executable, speech is disabled");
        }
    }

    internal static void Output(string text, bool interrupt)
    {
        if (!Ready) return;
        var processed = TextProcessor.Process(text);
        if (processed == null) return;
        Log.Info($"SAY{(interrupt ? "!" : " ")} {processed}");
        var err = Prism.BackendOutput(s_backend, processed, interrupt);
        if (err != Prism.PrismError.Ok) Log.Warn($"ScreenReader: output failed: {err} ({Prism.ErrorString(err)})");
    }

    internal static void Stop()
    {
        if (Ready) Prism.BackendStop(s_backend);
    }

    // Idempotent: MelonLoader may call both quit paths.
    internal static void Unload()
    {
        if (s_backend != IntPtr.Zero)
        {
            Prism.BackendFree(s_backend);
            s_backend = IntPtr.Zero;
        }
        if (s_ctx != IntPtr.Zero)
        {
            Prism.Shutdown(s_ctx);
            s_ctx = IntPtr.Zero;
        }
    }
}
