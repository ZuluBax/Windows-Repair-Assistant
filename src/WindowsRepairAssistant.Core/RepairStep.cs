namespace WindowsRepairAssistant.Core;

public enum RepairStepKind
{
    DismCheckHealth,
    DismScanHealth,
    DismRestoreHealth,
    SfcScanNow,
}

/// <summary>One Windows command in the repair sequence, with the wording shown to the user.</summary>
public sealed record RepairStepDefinition(
    RepairStepKind Kind,
    string DisplayName,
    string Title,
    string Description,
    string ExecutablePath,
    IReadOnlyList<string> Arguments)
{
    public bool IsDism => Kind is not RepairStepKind.SfcScanNow;

    /// <summary>Human-readable command line, used only for logging (never executed as a string).</summary>
    public string CommandLine => $"{ExecutablePath} {string.Join(' ', Arguments)}";
}

public static class RepairPlan
{
    public static IReadOnlyList<RepairStepDefinition> CreateDefault() => Create(ResolveSystemDirectory());

    public static IReadOnlyList<RepairStepDefinition> Create(string systemDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(systemDirectory);

        // Absolute paths only. We run elevated, so resolving "dism"/"sfc" through PATH or the
        // working directory would let a planted lookalike executable run as administrator.
        string dism = Path.Combine(systemDirectory, "dism.exe");
        string sfc = Path.Combine(systemDirectory, "sfc.exe");

        return
        [
            new(RepairStepKind.DismCheckHealth,
                "DISM CheckHealth",
                "Checking Windows image health",
                "Quickly checks whether Windows has already recorded any damage to its component store — the files Windows uses to repair itself.",
                dism, ["/Online", "/Cleanup-Image", "/CheckHealth"]),

            new(RepairStepKind.DismScanHealth,
                "DISM ScanHealth",
                "Scanning Windows image",
                "Performs a thorough scan of the Windows component store for damage. This often takes 5 to 20 minutes.",
                dism, ["/Online", "/Cleanup-Image", "/ScanHealth"]),

            new(RepairStepKind.DismRestoreHealth,
                "DISM RestoreHealth",
                "Repairing Windows image",
                "Repairs any damage in the component store, downloading replacement files from Windows Update if needed. This can take 10 to 30 minutes or longer.",
                dism, ["/Online", "/Cleanup-Image", "/RestoreHealth"]),

            new(RepairStepKind.SfcScanNow,
                "SFC /scannow",
                "Scanning and repairing system files",
                "Checks every protected Windows system file and replaces any that are damaged or missing. This often takes 10 to 20 minutes.",
                sfc, ["/scannow"]),
        ];
    }

    private static string ResolveSystemDirectory()
    {
        // A 32-bit process on 64-bit Windows is silently redirected from System32 to SysWOW64,
        // where sfc.exe fails with "could not start the repair service". Sysnative bypasses that.
        // The shipped build is x64 so this is defensive only.
        if (Environment.Is64BitOperatingSystem && !Environment.Is64BitProcess)
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Sysnative");
        }

        return Environment.SystemDirectory;
    }
}
