using System.Diagnostics;

try
{
    var repoRoot = FindRepoRoot(AppContext.BaseDirectory);
    var scriptPath = Path.Combine(repoRoot, "scripts", "dev-start.ps1");
    if (!File.Exists(scriptPath))
    {
        Console.Error.WriteLine($"Could not find dev launcher script: {scriptPath}");
        return 1;
    }

    Console.WriteLine("Starting VideoStudio full local stack...");
    Console.WriteLine($"Repository: {repoRoot}");
    Console.WriteLine("Backend:  http://localhost:5281/swagger");
    Console.WriteLine("Frontend: http://localhost:5173");
    Console.WriteLine();

    using var process = Process.Start(new ProcessStartInfo
    {
        FileName = "powershell.exe",
        WorkingDirectory = repoRoot,
        UseShellExecute = false,
        ArgumentList =
        {
            "-ExecutionPolicy",
            "Bypass",
            "-File",
            scriptPath
        }
    });

    if (process is null)
    {
        Console.Error.WriteLine("Failed to start PowerShell dev launcher.");
        return 1;
    }

    process.WaitForExit();
    return process.ExitCode;
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}

static string FindRepoRoot(string startPath)
{
    var directory = new DirectoryInfo(startPath);
    while (directory is not null)
    {
        var solutionPath = Path.Combine(directory.FullName, "VideoStudio.slnx");
        var launcherPath = Path.Combine(directory.FullName, "scripts", "dev-start.ps1");
        if (File.Exists(solutionPath) && File.Exists(launcherPath))
        {
            return directory.FullName;
        }

        directory = directory.Parent;
    }

    throw new InvalidOperationException("Could not locate repository root containing VideoStudio.slnx and scripts/dev-start.ps1.");
}
