using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Principal;
using Microsoft.Win32;

namespace WindowsRepairAssistant.Core;

/// <summary>A folder to clean. Exactly one of <see cref="Path"/> and <see cref="Problem"/> is set.</summary>
/// <param name="Description">Lower-case, used mid-sentence: "your Temp folder".</param>
/// <param name="Problem">Completes "… was not cleaned because …".</param>
public sealed record TempFolder(string Description, string? Path, string? Problem);

public static class TempFolders
{
    public const string UserTempDescription = "your Temp folder";
    public const string WindowsTempDescription = "the Windows Temp folder";

    /// <summary>The signed-in user's Temp folder and C:\Windows\Temp.</summary>
    public static IReadOnlyList<TempFolder> Locate()
    {
        TempFolder user = LocateSignedInUserTemp();
        TempFolder windows = Validate(WindowsTempDescription,
            System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp"));

        return user.Path is not null && string.Equals(user.Path, windows.Path, StringComparison.OrdinalIgnoreCase)
            ? [user]
            : [user, windows];
    }

    /// <summary>
    /// Last line of defence against a misconfigured TEMP variable (e.g. TEMP=C:\) turning "delete
    /// everything not in use" into a disaster: only a local folder actually named Temp or Tmp is cleaned.
    /// </summary>
    public static bool IsSafeToClean(string? path, out string? reason)
    {
        reason = null;
        if (string.IsNullOrWhiteSpace(path))
        {
            reason = "no location is set for it";
        }
        else if (path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            reason = "it is on a network location";
        }
        else if (!System.IO.Path.IsPathFullyQualified(path))
        {
            reason = "its location is not a full path";
        }
        else if (path.Split('\\', '/').Contains(".."))
        {
            reason = "its location is not a plain folder path";
        }
        else
        {
            string full = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path));
            string leaf = System.IO.Path.GetFileName(full);
            if (string.Equals(System.IO.Path.GetPathRoot(full)?.TrimEnd('\\'), full.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            {
                reason = "it is set to the top of a drive";
            }
            else if (!leaf.Equals("Temp", StringComparison.OrdinalIgnoreCase) && !leaf.Equals("Tmp", StringComparison.OrdinalIgnoreCase))
            {
                reason = $"it is set to {full}, which is not a folder named Temp";
            }
        }

        return reason is null;
    }

    /// <summary>
    /// Works out another user's Temp folder from the raw (unexpanded) TMP/TEMP value in their registry.
    /// </summary>
    /// <remarks>
    /// That value is controlled by the (possibly standard) user, but we delete as administrator. So the
    /// result must lie inside their own profile, where they could delete things anyway; anything else,
    /// or anything we cannot expand on their behalf, falls back to the standard location.
    /// </remarks>
    public static string ExpandUserTempSetting(string? raw, string profilePath)
    {
        string fallback = System.IO.Path.Combine(profilePath, "AppData", "Local", "Temp");
        if (string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
        }

        string expanded = raw
            .Replace("%USERPROFILE%", profilePath, StringComparison.OrdinalIgnoreCase)
            .Replace("%LOCALAPPDATA%", System.IO.Path.Combine(profilePath, "AppData", "Local"), StringComparison.OrdinalIgnoreCase);

        bool insideProfile = !expanded.Contains('%', StringComparison.Ordinal)
            && System.IO.Path.IsPathFullyQualified(expanded)
            && !expanded.Split('\\', '/').Contains("..")
            && expanded.StartsWith(System.IO.Path.TrimEndingDirectorySeparator(profilePath) + "\\", StringComparison.OrdinalIgnoreCase);

        return insideProfile ? expanded : fallback;
    }

    /// <summary>
    /// Files Windows will move or delete at the next restart. Installers sometimes stage a new file in
    /// Temp and have Windows move it into place on restart; deleting it first would silently break that
    /// update, and nothing has it open, so the in-use check alone would not protect it.
    /// </summary>
    public static IReadOnlyList<string> ReadPendingRenamePaths()
    {
        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager");
            // Parsed separately: each value is its own list of pairs.
            return
            [
                .. ParsePendingRenamePaths(key?.GetValue("PendingFileRenameOperations") as string[] ?? []),
                .. ParsePendingRenamePaths(key?.GetValue("PendingFileRenameOperations2") as string[] ?? []),
            ];
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            return [];
        }
    }

    /// <summary>
    /// Paths to keep: both ends of every "move at restart" pair. "Delete at restart" pairs (empty
    /// destination) need no protection, and a real PC had thousands of them. The list is stored as
    /// source/destination pairs with empty entries kept (verified on Windows 11); if it ever has an odd
    /// length the pairs can't be trusted, so every path is kept instead.
    /// </summary>
    public static IReadOnlyList<string> ParsePendingRenamePaths(IReadOnlyList<string> raw)
    {
        static string Clean(string entry)
        {
            string path = entry.TrimStart('!');
            return path.StartsWith(@"\??\", StringComparison.Ordinal) ? path[4..] : path;
        }

        if (raw.Count % 2 != 0)
        {
            return raw.Select(Clean).Where(p => p.Length > 0).ToList();
        }

        var keep = new List<string>();
        for (int i = 0; i < raw.Count; i += 2)
        {
            string destination = Clean(raw[i + 1]);
            if (destination.Length > 0)
            {
                keep.Add(Clean(raw[i]));
                keep.Add(destination);
            }
        }

        return keep;
    }

    private static TempFolder Validate(string description, string? path) =>
        IsSafeToClean(path, out string? reason)
            ? new TempFolder(description, System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path!)), null)
            : new TempFolder(description, null, reason);

    private static TempFolder LocateSignedInUserTemp()
    {
        try
        {
            using WindowsIdentity current = WindowsIdentity.GetCurrent();
            SecurityIdentifier? signedIn = SignedInUser.GetSid();
            if (signedIn is null)
            {
                return new TempFolder(UserTempDescription, null, "the signed-in user could not be identified");
            }

            // Usually the person who approved the administrator prompt is the signed-in user. If a
            // different administrator account approved it, our own TEMP would be theirs, not the user's.
            if (signedIn == current.User)
            {
                return Validate(UserTempDescription, System.IO.Path.GetTempPath());
            }

            using RegistryKey? profileKey = Registry.LocalMachine.OpenSubKey($@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\{signedIn.Value}");
            if (profileKey?.GetValue("ProfileImagePath") is not string profile || !System.IO.Path.IsPathFullyQualified(profile))
            {
                return new TempFolder(UserTempDescription, null, "the signed-in user's profile folder could not be found");
            }

            using RegistryKey? environment = Registry.Users.OpenSubKey($@"{signedIn.Value}\Environment");
            // TMP first, matching the order Windows itself uses.
            string? raw = environment?.GetValue("TMP", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string
                          ?? environment?.GetValue("TEMP", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
            return Validate(UserTempDescription, ExpandUserTempSetting(raw, profile));
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException or IdentityNotMappedException or Win32Exception)
        {
            return new TempFolder(UserTempDescription, null, "its location could not be worked out");
        }
    }

    private static class SignedInUser
    {
        private const int WtsUserName = 5;
        private const int WtsDomainName = 7;

        /// <summary>The user signed in to the Windows session this app is displayed in.</summary>
        public static SecurityIdentifier? GetSid()
        {
            int sessionId;
            using (Process self = Process.GetCurrentProcess())
            {
                sessionId = self.SessionId;
            }

            string? user = Query(sessionId, WtsUserName);
            string? domain = Query(sessionId, WtsDomainName);
            if (string.IsNullOrEmpty(user))
            {
                return null;
            }

            var account = string.IsNullOrEmpty(domain) ? new NTAccount(user) : new NTAccount(domain, user);
            return (SecurityIdentifier)account.Translate(typeof(SecurityIdentifier));
        }

        private static string? Query(int sessionId, int infoClass)
        {
            if (!WTSQuerySessionInformationW(IntPtr.Zero, sessionId, infoClass, out IntPtr buffer, out _))
            {
                return null;
            }

            try
            {
                return Marshal.PtrToStringUni(buffer);
            }
            finally
            {
                WTSFreeMemory(buffer);
            }
        }

        [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool WTSQuerySessionInformationW(IntPtr hServer, int sessionId, int wtsInfoClass, out IntPtr ppBuffer, out int pBytesReturned);

        [DllImport("wtsapi32.dll", ExactSpelling = true)]
        private static extern void WTSFreeMemory(IntPtr pMemory);
    }
}

/// <summary>Deletes everything that is not in use from the Temp folders. Never fails the repair as a whole.</summary>
public sealed class TempCleanupStep(Func<IReadOnlyList<TempFolder>> locateFolders, Func<IReadOnlyList<string>>? pendingRestartPaths = null) : IInProcessStep
{
    public TempCleanupStep() : this(TempFolders.Locate, TempFolders.ReadPendingRenamePaths)
    {
    }

    public Task<StepAnalysis> RunAsync(Action<string> onOutputLine)
    {
        ArgumentNullException.ThrowIfNull(onOutputLine);
        // Can take a while on a large folder; keep it off the caller's thread.
        return Task.Run(() => Run(onOutputLine));
    }

    private StepAnalysis Run(Action<string> output)
    {
        CleanupStats total = CleanupStats.Zero;
        var problems = new List<string>();
        int cleaned = 0;
        IReadOnlyList<string> keep = pendingRestartPaths?.Invoke() ?? [];
        IReadOnlyList<TempFolder> folders = locateFolders();

        // Only mention files this cleanup could actually have touched.
        int keptHere = keep.Count(p => folders.Any(f => f.Path is not null && p.StartsWith(f.Path + "\\", StringComparison.OrdinalIgnoreCase)));
        if (keptHere > 0)
        {
            output($"{Count(keptHere, "file")} waiting to be moved into place at the next restart will be left alone.");
        }

        foreach (TempFolder folder in folders)
        {
            if (folder.Path is null)
            {
                output($"Skipping {folder.Description}: {folder.Problem}.");
                problems.Add($"{Capitalise(folder.Description)} was not cleaned because {folder.Problem}.");
                continue;
            }

            output($"Cleaning {folder.Description}: {folder.Path}");
            try
            {
                CleanupStats stats = SafeTreeCleaner.DeleteContents(folder.Path, keep,
                    progress => output($"  {Count(progress.FilesDeleted, "file")} deleted so far…"));
                total += stats;
                cleaned++;
                // Totals only: individual file names could reveal what the user has been doing.
                output($"  Deleted {Count(stats.FilesDeleted, "file")} and {Count(stats.FoldersDeleted, "folder")} ({FormatSize(stats.BytesFreed)}). " +
                       $"In use: {stats.InUse.ToString("N0", CultureInfo.InvariantCulture)}. Could not delete: {stats.NotDeleted.ToString("N0", CultureInfo.InvariantCulture)}.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                string reason = ex switch
                {
                    DirectoryNotFoundException => "it could not be found",
                    CleanupRefusedException => ex.Message,
                    _ => "it could not be opened",
                };
                output($"  Not cleaned: {ex.Message}");
                problems.Add($"{Capitalise(folder.Description)} was not cleaned because {reason}.");
            }
        }

        string message = $"Deleted {Count(total.FilesDeleted, "file")} and freed {FormatSize(total.BytesFreed)}.";
        if (total.InUse > 0)
        {
            message += $" {Count(total.InUse, "file")} in use or waiting for a restart {(total.InUse == 1 ? "was" : "were")} left alone.";
        }

        if (total.NotDeleted > 0)
        {
            message += $" {Count(total.NotDeleted, "item")} could not be deleted.";
        }

        foreach (string problem in problems)
        {
            message += " " + problem;
        }

        StepStatus status = cleaned == 0 ? StepStatus.Failed
            : problems.Count > 0 ? StepStatus.CompletedWithIssues
            : StepStatus.Succeeded;
        return new StepAnalysis(status, StepFinding.None, message);
    }

    public static string FormatSize(long bytes)
    {
        string[] units = ["KB", "MB", "GB", "TB"];
        if (bytes < 1024)
        {
            return Count(bytes, "byte");
        }

        double value = bytes;
        int unit = -1;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return string.Create(CultureInfo.InvariantCulture, $"{value:0.0} {units[unit]}");
    }

    private static string Count(long n, string noun) =>
        string.Create(CultureInfo.InvariantCulture, $"{n:N0} {noun}{(n == 1 ? "" : "s")}");

    private static string Capitalise(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
}
