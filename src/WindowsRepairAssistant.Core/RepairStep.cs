namespace WindowsRepairAssistant.Core;

public enum RepairStepKind
{
    DismCheckHealth,
    DismScanHealth,
    DismRestoreHealth,
    SfcScanNow,
    CleanTempFiles,
    CheckWindowsUpdates,
    InstallWindowsUpdate,
}

/// <summary>
/// A step carried out by this app's own code rather than by launching a Windows tool.
/// Implementations report progress through <c>onOutputLine</c> and return their verdict; an
/// unexpected exception is caught by the orchestrator and reported as the step failing.
/// </summary>
public interface IInProcessStep
{
    Task<StepAnalysis> RunAsync(Action<string> onOutputLine);
}

/// <summary>One step in the sequence, with the wording shown to the user.</summary>
public sealed record RepairStepDefinition(
    RepairStepKind Kind,
    string DisplayName,
    string Title,
    string Description,
    string ExecutablePath,
    IReadOnlyList<string> Arguments)
{
    /// <summary>Set for steps run by this app's own code; null for steps that launch a Windows tool.</summary>
    public IInProcessStep? InProcess { get; init; }

    public bool IsDism => Kind is RepairStepKind.DismCheckHealth or RepairStepKind.DismScanHealth or RepairStepKind.DismRestoreHealth;

    /// <summary>DISM and SFC: the steps whose results decide the repair verdict. The others are optional extras.</summary>
    public bool IsRepairTool => IsDism || Kind == RepairStepKind.SfcScanNow;

    /// <summary>Human-readable command line, used only for logging (never executed as a string).</summary>
    public string CommandLine => InProcess is null
        ? $"{ExecutablePath} {string.Join(' ', Arguments)}"
        : $"(built into Windows Repair Assistant) {DisplayName}";

    public static RepairStepDefinition ForInProcess(RepairStepKind kind, string displayName, string title, string description, IInProcessStep step) =>
        new(kind, displayName, title, description, "", []) { InProcess = step };
}

public static class RepairPlan
{
    /// <summary>The four DISM/SFC steps only.</summary>
    public static IReadOnlyList<RepairStepDefinition> CreateDefault() => Create(DefaultSystemDirectory);

    /// <param name="tempCleanup">When set, temporary files are cleaned first, freeing space before DISM may download files.</param>
    /// <param name="updateCheck">When set, Windows Update is checked last, once DISM and SFC have finished with the servicing stack.</param>
    public static IReadOnlyList<RepairStepDefinition> Create(string systemDirectory, IInProcessStep? tempCleanup = null, IInProcessStep? updateCheck = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(systemDirectory);

        // Absolute paths only. We run elevated, so resolving "dism"/"sfc" through PATH or the
        // working directory would let a planted lookalike executable run as administrator.
        string dism = Path.Combine(systemDirectory, "dism.exe");
        string sfc = Path.Combine(systemDirectory, "sfc.exe");

        var plan = new List<RepairStepDefinition>();

        if (tempCleanup is not null)
        {
            plan.Add(RepairStepDefinition.ForInProcess(RepairStepKind.CleanTempFiles,
                "Temp file cleanup",
                "Cleaning up temporary files",
                "Deletes temporary files from your Temp folder and the Windows Temp folder. Files that are in use are left alone.",
                tempCleanup));
        }

        plan.AddRange(
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
        ]);

        if (updateCheck is not null)
        {
            plan.Add(RepairStepDefinition.ForInProcess(RepairStepKind.CheckWindowsUpdates,
                "Windows Update check",
                "Checking for Windows updates",
                "Asks Windows Update which updates are available for this computer. Nothing is installed yet: you choose afterwards.",
                updateCheck));
        }

        return plan;
    }

    public static string DefaultSystemDirectory
    {
        get
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
}
