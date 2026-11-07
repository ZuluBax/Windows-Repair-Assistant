using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace WindowsRepairAssistant.Services;

public interface IShellLauncher
{
    /// <returns>An error message, or null on success.</returns>
    string? OpenFolder(string folderPath);

    /// <returns>An error message, or null on success.</returns>
    string? OpenTextFile(string filePath);
}

public sealed class ShellLauncher : IShellLauncher
{
    // Full paths: this process is elevated, so never resolve executables through PATH.
    private static readonly string Explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
    private static readonly string Notepad = Path.Combine(Environment.SystemDirectory, "notepad.exe");

    public string? OpenFolder(string folderPath) =>
        Directory.Exists(folderPath)
            ? Launch(Explorer, folderPath)
            : "The log folder does not exist yet. It is created when you run your first repair.";

    public string? OpenTextFile(string filePath) =>
        File.Exists(filePath) ? Launch(Notepad, filePath) : "The log file could not be found.";

    private static string? Launch(string executable, string argument)
    {
        try
        {
            var startInfo = new ProcessStartInfo(executable) { UseShellExecute = false };
            startInfo.ArgumentList.Add(argument);
            using Process? process = Process.Start(startInfo);
            return null;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return ex.Message;
        }
    }
}
