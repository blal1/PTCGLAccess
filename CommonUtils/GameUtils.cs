using System.Text;
using System.Text.RegularExpressions;

namespace CommonUtils;

public static class GameUtils
{
    public static readonly string TypicalInstallDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "The Pokemon Company International", "Pokémon Trading Card Game Live");

    // Where people usually put an ASCII-only copy or junction (MelonLoader cannot load from non-ASCII paths).
    private static readonly string[] s_knownDirs = { @"C:\PokemonTCGLive", TypicalInstallDir };

    public static string GetManagedDir(string gameDir) => Path.Combine(gameDir, Constants.GameDataDirName, "Managed");

    public static bool IsGameDirectory(string path) =>
        File.Exists(Path.Combine(path, Constants.GameExeName))
        && File.Exists(Path.Combine(GetManagedDir(path), Constants.MainGameAssembly));

    public static List<string> FindGameCandidateDirectories() =>
        s_knownDirs.Where(IsGameDirectory).Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    // The game's Application.version (e.g. "1.43.0") is a length-prefixed, zero-padded string in globalgamemanagers.
    // The Unity version ("6000.3.5f2") lives in the same file but never matches the plain x.y.z pattern.
    public static string ExtractGameVersion(string gameDir)
    {
        var path = Path.Combine(gameDir, Constants.GameDataDirName, "globalgamemanagers");
        var text = Encoding.ASCII.GetString(File.ReadAllBytes(path));
        foreach (Match m in Regex.Matches(text, @"\0(\d{1,3}\.\d{1,3}\.\d{1,4})\0"))
        {
            if (m.Groups[1].Value != "1.0.0") return m.Groups[1].Value;
        }
        throw new InvalidDataException($"Could not find the game version in {path}");
    }

    public static bool IsMelonLoaderInstalled(string gameDir) => Directory.Exists(Path.Combine(gameDir, "MelonLoader"));

    public static bool IsAsciiPath(string path) => path.All(c => c < 128);
}
