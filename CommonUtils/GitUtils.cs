namespace CommonUtils;

public static class GitUtils
{
    public static bool IsRepository(string dir) => Directory.Exists(Path.Combine(dir, ".git"));

    public static void Init(string dir)
    {
        Directory.CreateDirectory(dir);
        ProcessUtils.ExecuteProcess("git", "init -q", dir);
    }

    public static string? GetCurrentBranchName(string gitDir)
    {
        var stdout = new List<string>();
        return ProcessUtils.ExecuteProcessWithReturnCode("git", "symbolic-ref --short HEAD", gitDir, stdout) == 0 && stdout.Count == 1
            ? stdout[0]
            : null;
    }

    public static bool HasCommits(string gitDir) =>
        ProcessUtils.ExecuteProcessWithReturnCode("git", "rev-parse --verify --quiet HEAD", gitDir) == 0;

    public static bool BranchExists(string gitDir, string branchName) =>
        ProcessUtils.ExecuteProcessWithReturnCode("git", $"show-ref --verify --quiet refs/heads/{branchName}", gitDir) == 0;

    public static bool IsClean(string gitDir)
    {
        var stdout = new List<string>();
        ProcessUtils.ExecuteProcessWithReturnCode("git", "status --porcelain", gitDir, stdout);
        return stdout.Count == 0;
    }

    // An empty repository has nothing to fork from, so its first branch is created as an orphan.
    public static void CheckoutNewBranch(string gitDir, string branchName) =>
        ProcessUtils.ExecuteProcess("git", HasCommits(gitDir) ? $"checkout -q -b {branchName}" : $"checkout -q --orphan {branchName}", gitDir);

    // Empties a working tree but keeps .git, so a new game version replaces the previous one file for file.
    public static void CleanupDirectory(string gitDir)
    {
        var dir = new DirectoryInfo(gitDir);
        foreach (var file in dir.GetFiles()) file.Delete();
        foreach (var child in dir.GetDirectories())
        {
            if (child.Name != ".git") child.Delete(true);
        }
    }

    public static void CommitAll(string gitDir, string message)
    {
        ProcessUtils.ExecuteProcess("git", "add -A", gitDir);
        ProcessUtils.ExecuteProcess("git", $"-c user.name=Decompiler -c user.email=decompiler@localhost commit -q -m \"{message}\"", gitDir);
    }
}
