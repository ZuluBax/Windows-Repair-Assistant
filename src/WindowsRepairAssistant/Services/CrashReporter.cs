using System.IO;
using System.Text;
using WindowsRepairAssistant.Core;

namespace WindowsRepairAssistant.Services;

/// <summary>Writes unhandled exceptions to a crash log alongside the session logs.</summary>
public sealed class CrashReporter(LogStore logStore)
{
    /// <returns>The crash log path, or null if it could not be written.</returns>
    public string? Write(Exception? exception)
    {
        if (exception is null)
        {
            return null;
        }

        try
        {
            logStore.EnsureDirectory();
            string path = Path.Combine(logStore.LogDirectory, $"{LogStore.CrashFilePrefix}{DateTime.Now:yyyyMMdd-HHmmss-fff}{LogStore.FileExtension}");
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            using var writer = new StreamWriter(stream, new UTF8Encoding(true));
            writer.WriteLine($"Windows Repair Assistant {AppInfo.Version} crash report");
            writer.WriteLine($"Time:    {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
            writer.WriteLine($"Windows: {SystemInfo.DescribeWindows()}");
            writer.WriteLine();
            writer.WriteLine(exception.ToString());
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nowhere left to report to; the user still sees the on-screen message.
            return null;
        }
    }
}
