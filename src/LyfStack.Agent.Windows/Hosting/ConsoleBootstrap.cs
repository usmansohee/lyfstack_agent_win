namespace LyfStack.Agent.Windows.Hosting;

internal static class ConsoleBootstrap
{
    public static IDisposable RedirectToLogFile(string logPath)
    {
        string? directory = Path.GetDirectoryName(logPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var writer = new StreamWriter(
            new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
        {
            AutoFlush = true
        };

        Console.SetOut(writer);
        Console.SetError(writer);
        return writer;
    }
}
