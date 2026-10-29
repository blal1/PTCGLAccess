using System;
using System.IO;
using MelonLoader;

namespace Accessibility;

// Every line goes to the MelonLoader console and to Mods\PokemonTCGLiveAccess.log, recreated at each launch so a
// bug report is one small file instead of Latest.log full of loader noise. Logging never throws.
internal static class Log
{
    private static MelonLogger.Instance? s_logger;
    private static StreamWriter? s_file;

    public static void Init(MelonLogger.Instance logger)
    {
        s_logger = logger;
        try
        {
            var path = Path.Combine(Path.GetDirectoryName(typeof(Log).Assembly.Location)!, "PokemonTCGLiveAccess.log");
            s_file = new StreamWriter(path, append: false) { AutoFlush = true };
            s_file.WriteLine($"=== PokemonTCGLiveAccess {BuildInfo.Version} log opened {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
        }
        catch (Exception e)
        {
            logger.Warning($"Log: file init failed: {e.Message}");
        }
    }

    public static void Info(string msg)
    {
        s_logger?.Msg(msg);
        Write("INFO ", msg);
    }

    public static void Warn(string msg)
    {
        s_logger?.Warning(msg);
        Write("WARN ", msg);
    }

    public static void Error(string msg)
    {
        s_logger?.Error(msg);
        Write("ERROR", msg);
    }

    private static void Write(string level, string msg)
    {
        if (s_file == null) return;
        try
        {
            lock (s_file) s_file.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {level} {msg}");
        }
        catch { /* logging must never break the mod */ }
    }
}
