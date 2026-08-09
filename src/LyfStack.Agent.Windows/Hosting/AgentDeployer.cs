using LyfStack.Agent.Windows.Configuration;

namespace LyfStack.Agent.Windows.Hosting;

/// <summary>
/// Copies the current build into LocalAppData so startup can use a stable path.
/// </summary>
public static class AgentDeployer
{
    public static string DeployCurrentBuild()
    {
        string sourceDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string targetDir = AgentPaths.AppDirectory;

        Directory.CreateDirectory(targetDir);
        CopyDirectory(sourceDir, targetDir);

        string exePath = AgentPaths.InstalledExePath;
        if (!File.Exists(exePath))
        {
            throw new FileNotFoundException(
                "Deploy finished but LyfStack.Agent.Windows.exe was not found in the install folder.",
                exePath);
        }

        WriteHiddenLauncher(exePath);
        return exePath;
    }

    public static string HiddenLauncherPath => Path.Combine(AgentPaths.AppDirectory, "start-hidden.vbs");

    private static void WriteHiddenLauncher(string exePath)
    {
        // WindowStyle 0 = hidden — avoids a console flash at Windows logon.
        string script =
            "Set shell = CreateObject(\"WScript.Shell\")" + Environment.NewLine +
            "shell.Run \"\"\"" + exePath + "\"\" --tray\", 0, False" + Environment.NewLine;

        File.WriteAllText(HiddenLauncherPath, script);
    }

    private static void CopyDirectory(string sourceDir, string targetDir)
    {
        foreach (string directory in Directory.GetDirectories(sourceDir, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(sourceDir, directory);
            Directory.CreateDirectory(Path.Combine(targetDir, relative));
        }

        foreach (string file in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(sourceDir, file);
            string extension = Path.GetExtension(file);

            if (string.Equals(extension, ".pdb", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string destination = Path.Combine(targetDir, relative);
            string? destinationDir = Path.GetDirectoryName(destination);
            if (!string.IsNullOrWhiteSpace(destinationDir))
            {
                Directory.CreateDirectory(destinationDir);
            }

            File.Copy(file, destination, overwrite: true);
        }
    }
}
