#if DEBUG
using WindowsRepairAssistant.Core;

namespace WindowsRepairAssistant.Services;

/// <summary>
/// DEBUG BUILDS ONLY. Replays scripted DISM/SFC output so every screen, the close guard and the
/// restart dialogs can be tested in minutes without touching the real system.
/// Launch with: WindowsRepairAssistant.exe --simulate=clean|repaired|sfc-partial|dism-fail|no-internet
/// Restart is also simulated, so "Restart computer now" never reboots a development machine.
/// </summary>
internal sealed class SimulatedProcessRunner(string scenario) : IProcessRunner
{
    private static readonly TimeSpan LineDelay = TimeSpan.FromMilliseconds(120);

    public static readonly string[] Scenarios = ["clean", "repaired", "sfc-partial", "dism-fail", "no-internet"];

    public async Task<ProcessRunResult> RunAsync(string executablePath, IReadOnlyList<string> arguments, Action<string> onOutputLine)
    {
        string step = arguments[^1];
        (int exitCode, List<string> lines) = Script(step);

        var all = new System.Text.StringBuilder();
        var started = DateTime.UtcNow;
        foreach (string line in lines)
        {
            await Task.Delay(LineDelay).ConfigureAwait(false);
            all.AppendLine(line);
            onOutputLine(line);
        }

        return new ProcessRunResult(exitCode, all.ToString(), "", DateTime.UtcNow - started);
    }

    private (int ExitCode, List<string> Lines) Script(string step)
    {
        var banner = new List<string> { "Deployment Image Servicing and Management tool", "Version: 10.0.26100.1150", "Image Version: 10.0.26100.2314" };
        bool corrupt = scenario is "repaired" or "no-internet";

        switch (step)
        {
            case "/CheckHealth" when scenario == "dism-fail":
                return (unchecked((int)0x800F0915), [.. banner, "Error: 0x800f0915", "The DISM log file can be found at C:\\WINDOWS\\Logs\\DISM\\dism.log"]);
            case "/CheckHealth":
                return (0, [.. banner, corrupt ? "The component store is repairable." : "No component store corruption detected.", "The operation completed successfully."]);
            case "/ScanHealth":
                return (0, [.. banner, .. Bar(), corrupt ? "The component store is repairable." : "No component store corruption detected.", "The operation completed successfully."]);
            case "/RestoreHealth" when scenario == "no-internet":
                return (unchecked((int)0x800F081F), [.. banner, .. Bar(), "Error: 0x800f081f", "The source files could not be found."]);
            case "/RestoreHealth":
                return (0, [.. banner, .. Bar(), "The restore operation completed successfully.", "The operation completed successfully."]);
            default:
                string verdict = scenario switch
                {
                    "repaired" => "Windows Resource Protection found corrupt files and successfully repaired them.",
                    "sfc-partial" => "Windows Resource Protection found corrupt files but was unable to fix some of them.",
                    _ => "Windows Resource Protection did not find any integrity violations.",
                };
                return (0, ["Beginning system scan.  This process will take some time.", "Beginning verification phase of system scan.",
                    .. Enumerable.Range(1, 20).Select(i => $"Verification {i * 5}% complete."), verdict]);
        }
    }

    private static IEnumerable<string> Bar() =>
        Enumerable.Range(1, 20).Select(i => $"[{new string('=', i)}{new string(' ', 20 - i)} {i * 5}.0% ]");

    public static Task<RestartRequestResult> SimulatedRestartAsync()
    {
        System.Windows.MessageBox.Show("SIMULATION: Windows would restart now. Nothing was restarted.", "Simulation");
        return Task.FromResult(new RestartRequestResult(true, null));
    }
}

/// <summary>
/// DEBUG BUILDS ONLY. Launch with --simulate-update=9.9.9 to see the update notice without a real
/// release existing. The first-run prompt still appears and saves a real choice.
/// </summary>
internal sealed class SimulatedUpdateChecker(string latestVersion) : IUpdateChecker
{
    public Task<UpdateCheckResult> CheckAsync(string currentVersion) =>
        Task.FromResult(new UpdateCheckResult(UpdateCheckStatus.UpdateAvailable, latestVersion));
}

/// <summary>DEBUG BUILDS ONLY. Used with --simulate so a simulated run never deletes real files.</summary>
internal sealed class SimulatedTempCleanup : IInProcessStep
{
    public async Task<StepAnalysis> RunAsync(Action<string> onOutputLine)
    {
        onOutputLine(@"Cleaning your Temp folder: C:\Users\Example\AppData\Local\Temp [SIMULATED]");
        await Task.Delay(800).ConfigureAwait(false);
        onOutputLine("  500 files deleted so far…");
        await Task.Delay(800).ConfigureAwait(false);
        onOutputLine(@"Cleaning the Windows Temp folder: C:\Windows\Temp [SIMULATED]");
        await Task.Delay(800).ConfigureAwait(false);
        return new StepAnalysis(StepStatus.Succeeded, StepFinding.None,
            "Deleted 1,234 files and freed 812.4 MB. 7 files in use or waiting for a restart were left alone. [SIMULATED - nothing was deleted]");
    }
}

/// <summary>
/// DEBUG BUILDS ONLY. Used with --simulate: offers made-up updates and pretends to install them, so a
/// simulated run never contacts Windows Update. "Example driver" fails to install, to show that path.
/// </summary>
internal sealed class SimulatedWindowsUpdateAgent(string scenario) : IWindowsUpdateAgent
{
    public IReadOnlyList<WindowsUpdateInfo> Search()
    {
        Thread.Sleep(1500);
        if (scenario == "no-internet")
        {
            throw new WindowsUpdateException("Simulated: no internet", unchecked((int)0x8024402C));
        }

        return
        [
            new("sim-1", "[SIMULATED] Security update for Windows (KB0000001)", 650L * 1024 * 1024, false, false, false),
            new("sim-2", "[SIMULATED] Microsoft Defender Antivirus definition update", 80L * 1024 * 1024, false, false, false),
            new("sim-3", "[SIMULATED] Example driver - Display", 300L * 1024 * 1024, true, true, false),
            new("sim-4", "[SIMULATED] Example interactive update", 5L * 1024 * 1024, true, false, true),
        ];
    }

    public UpdateInstallOutcome DownloadAndInstall(string updateId, Action<string> onProgress)
    {
        onProgress("Downloading… [SIMULATED]");
        Thread.Sleep(1200);
        onProgress("Installing… [SIMULATED]");
        Thread.Sleep(1200);
        return updateId switch
        {
            "sim-1" => new UpdateInstallOutcome(true, true, null, null),
            "sim-3" => new UpdateInstallOutcome(false, false, unchecked((int)0x80240022), "Simulated failure."),
            _ => new UpdateInstallOutcome(true, false, null, null),
        };
    }
}
#endif
