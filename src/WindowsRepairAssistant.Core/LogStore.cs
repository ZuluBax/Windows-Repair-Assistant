using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace WindowsRepairAssistant.Core;

/// <summary>
/// Owns the log folder: creation, hardening, naming, retention and crash detection.
/// </summary>
/// <remarks>
/// Security: this code runs as administrator inside %ProgramData%, which standard users can write to
/// by default. A standard user could pre-create our folder as a junction/symlink to redirect our file
/// writes or deletes elsewhere (a well-known privilege-escalation pattern). We therefore lock the
/// folder's ACL to Administrators/SYSTEM, refuse to use it if it is a reparse point, create log files
/// with CreateNew and skip reparse points when pruning.
/// </remarks>
public sealed class LogStore
{
    public const string FilePrefix = "repair-";
    public const string CrashFilePrefix = "crash-";
    public const string FileExtension = ".log";

    /// <summary>How many logs of each kind (repair, crash) are kept; older ones are deleted.</summary>
    public const int LogsToKeepPerKind = 5;
    private const int TailBytesToCheck = 4096;

    private readonly string _rootDirectory;
    private readonly bool _hardenPermissions;

    public LogStore(string rootDirectory, bool hardenPermissions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        _rootDirectory = rootDirectory;
        _hardenPermissions = hardenPermissions;
        LogDirectory = Path.Combine(rootDirectory, "Logs");
    }

    public static LogStore CreateDefault() => new(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WindowsRepairAssistant"),
        hardenPermissions: true);

    public string LogDirectory { get; }

    /// <exception cref="IOException">The folder could not be created or is not safe to use.</exception>
    public void EnsureDirectory()
    {
        try
        {
            EnsureSafeDirectory(_rootDirectory, applyAcl: _hardenPermissions);
            EnsureSafeDirectory(LogDirectory, applyAcl: false);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new IOException($"Access to the log folder was denied: {ex.Message}", ex);
        }
    }

    public FileSessionLog CreateSessionLog(DateTimeOffset now, TimeProvider time)
    {
        EnsureDirectory();
        string baseName = $"{FilePrefix}{now:yyyyMMdd-HHmmss}";
        string path = Path.Combine(LogDirectory, baseName + FileExtension);
        for (int attempt = 2; File.Exists(path) && attempt < 100; attempt++)
        {
            path = Path.Combine(LogDirectory, $"{baseName}-{attempt}{FileExtension}");
        }

        return new FileSessionLog(path, time);
    }

    /// <summary>Detects whether the most recent session's log never reached its end marker.</summary>
    public string? FindInterruptedSessionLog()
    {
        try
        {
            if (!Directory.Exists(LogDirectory) || IsReparsePoint(LogDirectory))
            {
                return null;
            }

            FileInfo? latest = new DirectoryInfo(LogDirectory)
                .EnumerateFiles($"{FilePrefix}*{FileExtension}")
                .Where(f => !f.Attributes.HasFlag(FileAttributes.ReparsePoint))
                .OrderByDescending(f => f.CreationTimeUtc)
                .FirstOrDefault();

            if (latest is null)
            {
                return null;
            }

            using var stream = new FileStream(latest.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            stream.Seek(Math.Max(0, stream.Length - TailBytesToCheck), SeekOrigin.Begin);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            string tail = reader.ReadToEnd();
            return tail.Contains(FileSessionLog.EndMarker, StringComparison.Ordinal) ? null : latest.FullName;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Crash detection is a courtesy; failing to read an old log must not block the app.
            return null;
        }
    }

    /// <summary>
    /// Keeps the newest <paramref name="keep"/> repair logs and, separately, the newest <paramref name="keep"/>
    /// crash logs, so a burst of crashes can never push out the user's repair history.
    /// </summary>
    public void PruneOldLogs(int keep = LogsToKeepPerKind)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(keep);
        PruneOldLogs(FilePrefix, keep);
        PruneOldLogs(CrashFilePrefix, keep);
    }

    private void PruneOldLogs(string prefix, int keep)
    {
        List<FileInfo> stale;
        try
        {
            if (!Directory.Exists(LogDirectory) || IsReparsePoint(LogDirectory))
            {
                return;
            }

            stale = new DirectoryInfo(LogDirectory)
                .EnumerateFiles($"{prefix}*{FileExtension}")
                .Where(f => !f.Attributes.HasFlag(FileAttributes.ReparsePoint))
                .OrderByDescending(f => f.CreationTimeUtc)
                .Skip(keep)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        foreach (FileInfo file in stale)
        {
            try
            {
                file.Delete();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Retention is best effort; a locked old log is harmless.
            }
        }
    }

    private static void EnsureSafeDirectory(string path, bool applyAcl)
    {
        if (Directory.Exists(path))
        {
            if (IsReparsePoint(path))
            {
                throw new IOException($"The log folder \"{path}\" is a link to another location and will not be used for safety reasons.");
            }

            if (applyAcl)
            {
                new DirectoryInfo(path).SetAccessControl(CreateRestrictedSecurity());
            }

            return;
        }

        if (applyAcl)
        {
            new DirectoryInfo(path).Create(CreateRestrictedSecurity());
        }
        else
        {
            Directory.CreateDirectory(path);
        }
    }

    private static bool IsReparsePoint(string path) =>
        new DirectoryInfo(path).Attributes.HasFlag(FileAttributes.ReparsePoint);

    private static DirectorySecurity CreateRestrictedSecurity()
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;

        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        // Standard users may read logs (e.g. to send to a technician) but not alter or plant files.
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
            FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));

        return security;
    }
}
