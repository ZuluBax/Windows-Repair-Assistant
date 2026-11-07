using System.ComponentModel;
using System.Diagnostics;
using System.Security;
using System.Security.Principal;
using Microsoft.Win32;

namespace WindowsRepairAssistant.Core;

public static class SystemInfo
{
    private const int FirstWindows11Build = 22000;

    public static string DescribeWindows()
    {
        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            if (key is null)
            {
                return Environment.OSVersion.VersionString;
            }

            string product = key.GetValue("ProductName") as string ?? "Windows";
            string? release = key.GetValue("DisplayVersion") as string ?? key.GetValue("ReleaseId") as string;
            string? build = key.GetValue("CurrentBuildNumber") as string;
            string revision = key.GetValue("UBR") is int ubr ? $".{ubr}" : "";

            // Windows 11 still reports "Windows 10" in ProductName for compatibility.
            if (int.TryParse(build, out int buildNumber) && buildNumber >= FirstWindows11Build
                && product.StartsWith("Windows 10", StringComparison.OrdinalIgnoreCase))
            {
                product = "Windows 11" + product["Windows 10".Length..];
            }

            string arch = Environment.Is64BitOperatingSystem ? "64-bit" : "32-bit";
            return $"{product} {release} (build {build}{revision}, {arch})".Replace("  ", " ", StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is SecurityException or IOException or UnauthorizedAccessException)
        {
            return Environment.OSVersion.VersionString;
        }
    }

    public static bool IsRunningAsAdministrator()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>
    /// Detects a DISM or SFC already running (e.g. left over from a previous session, or started by
    /// the user in a terminal). Running two at once against the same Windows image is unreliable.
    /// </summary>
    public static string? FindRunningRepairTool()
    {
        foreach (string name in new[] { "dism", "sfc" })
        {
            Process[] processes = [];
            try
            {
                processes = Process.GetProcessesByName(name);
                if (processes.Length > 0)
                {
                    return name.Equals("sfc", StringComparison.Ordinal) ? "System File Checker (sfc.exe)" : "DISM (dism.exe)";
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
            {
                // If we cannot enumerate processes we let the tools themselves report any conflict.
            }
            finally
            {
                foreach (Process process in processes)
                {
                    process.Dispose();
                }
            }
        }

        return null;
    }
}
