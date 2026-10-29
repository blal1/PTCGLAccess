using System.Diagnostics;

namespace CommonUtils;

public static class ProcessUtils
{
    public static int ExecuteProcessWithReturnCode(string fileName, string args, string workDir) =>
        ExecuteProcessWithReturnCode(fileName, args, workDir, new List<string>());

    public static int ExecuteProcessWithReturnCode(string fileName, string args, string workDir, List<string> stdout)
    {
        using var proc = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = args,
                WorkingDirectory = workDir,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
        };
        proc.OutputDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) stdout.Add(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) Console.Error.WriteLine($"[{fileName}] {e.Data}"); };
        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        proc.WaitForExit();
        return proc.ExitCode;
    }

    public static void ExecuteProcess(string fileName, string args, string workDir)
    {
        var exitCode = ExecuteProcessWithReturnCode(fileName, args, workDir);
        if (exitCode != 0) throw new InvalidOperationException($"'{fileName} {args}' ended with exit code {exitCode}");
    }

    // A directory junction, unlike mklink /D, needs no administrator rights.
    public static void MkJunction(string link, string targetDir) =>
        ExecuteProcess("cmd.exe", $"/c mklink /J \"{link}\" \"{targetDir}\"", Environment.CurrentDirectory);
}
