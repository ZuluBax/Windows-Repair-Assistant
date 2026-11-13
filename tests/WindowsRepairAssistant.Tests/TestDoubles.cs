using WindowsRepairAssistant.Core;

namespace WindowsRepairAssistant.Tests;

/// <summary>Realistic English output captured from DISM and SFC, used to drive scenarios safely.</summary>
internal static class SampleOutput
{
    private const string DismBanner = "Deployment Image Servicing and Management tool\r\nVersion: 10.0.26100.1150\r\n\r\nImage Version: 10.0.26100.2314\r\n\r\n";
    private const string Bar100 = "[==========================100.0%==========================] ";

    public static ProcessRunResult DismClean() => Ok(DismBanner + Bar100 + "No component store corruption detected.\r\nThe operation completed successfully.\r\n");

    public static ProcessRunResult DismRepairable() => Ok(DismBanner + Bar100 + "The component store is repairable.\r\nThe operation completed successfully.\r\n");

    public static ProcessRunResult DismNotRepairable() => Ok(DismBanner + "The component store is not repairable.\r\nThe operation completed successfully.\r\n");

    public static ProcessRunResult DismLocalised() => Ok(DismBanner + Bar100 + "Es wurde keine Komponentenspeicherbeschädigung erkannt.\r\nDer Vorgang wurde erfolgreich beendet.\r\n");

    public static ProcessRunResult DismRestoreOk() => Ok(DismBanner +
        "[==                         5.0%                           ]\r[=====================      84.6%==================         ]\r" +
        Bar100 + "The restore operation completed successfully.\r\nThe operation completed successfully.\r\n");

    public static ProcessRunResult DismSourceMissing() => new(
        unchecked((int)0x800F081F),
        DismBanner + Bar100 + "\r\nError: 0x800f081f\r\n\r\nThe source files could not be found.\r\nUse the \"Source\" option to specify the location of the files that are required to restore the feature.\r\n\r\nThe DISM log file can be found at C:\\WINDOWS\\Logs\\DISM\\dism.log\r\n",
        "", TimeSpan.FromMinutes(12));

    public static ProcessRunResult DismNotElevated() => new(
        740,
        DismBanner + "Error: 740\r\n\r\nElevated permissions are required to run DISM.\r\nUse an elevated command prompt to complete these tasks.\r\n",
        "", TimeSpan.FromSeconds(1));

    public static ProcessRunResult DismUnknownError() => new(
        unchecked((int)0x800F0915), DismBanner + "Error: 0x800f0915\r\n", "", TimeSpan.FromSeconds(4));

    public static ProcessRunResult NotStarted() => new(null, "", "", TimeSpan.Zero, "The system cannot find the file specified.");

    private const string SfcIntro = "\r\nBeginning system scan.  This process will take some time.\r\n\r\nBeginning verification phase of system scan.\r\nVerification 3% complete.\rVerification 57% complete.\rVerification 100% complete.\r\n\r\n";

    public static ProcessRunResult SfcClean() => Ok(SfcIntro + "Windows Resource Protection did not find any integrity violations.\r\n");

    public static ProcessRunResult SfcRepaired() => Ok(SfcIntro +
        "Windows Resource Protection found corrupt files and successfully repaired them.\r\nFor online repairs, details are included in the CBS log file located at\r\nwindir\\Logs\\CBS\\CBS.log. For example C:\\Windows\\Logs\\CBS\\CBS.log.\r\n");

    // Deliberately wrapped mid-sentence, as SFC does when its output is redirected.
    public static ProcessRunResult SfcPartial() => Ok(SfcIntro +
        "Windows Resource Protection found corrupt files but was unable to fix some\r\nof them.\r\nFor online repairs, details are included in the CBS log file located at\r\nwindir\\Logs\\CBS\\CBS.log.\r\n");

    public static ProcessRunResult SfcPendingRestart() => Ok("There is a system repair pending which requires reboot to complete.  Restart\r\nWindows and run sfc again.\r\n");

    public static ProcessRunResult SfcCouldNotPerform() => Ok(SfcIntro + "Windows Resource Protection could not perform the requested operation.\r\n");

    public static ProcessRunResult SfcLocalised() => Ok("Der Windows-Ressourcenschutz hat keine Integritätsverletzungen gefunden.\r\n");

    private static ProcessRunResult Ok(string stdout) => new(0, stdout, "", TimeSpan.FromMinutes(3));
}

/// <summary>Replays scripted results per step and records the order steps were invoked in.</summary>
internal sealed class FakeProcessRunner : IProcessRunner
{
    private readonly Dictionary<RepairStepKind, Func<ProcessRunResult>> _results = new();

    public List<RepairStepKind> Invocations { get; } = [];

    public int MaxConcurrent { get; private set; }

    private int _concurrent;

    public FakeProcessRunner Returns(RepairStepKind kind, Func<ProcessRunResult> result)
    {
        _results[kind] = result;
        return this;
    }

    public FakeProcessRunner Throws(RepairStepKind kind, Exception exception)
    {
        _results[kind] = () => throw exception;
        return this;
    }

    public async Task<ProcessRunResult> RunAsync(string executablePath, IReadOnlyList<string> arguments, Action<string> onOutputLine)
    {
        RepairStepKind kind = KindFor(arguments);
        Invocations.Add(kind);
        MaxConcurrent = Math.Max(MaxConcurrent, Interlocked.Increment(ref _concurrent));
        try
        {
            await Task.Yield();
            ProcessRunResult result = _results.TryGetValue(kind, out var factory) ? factory() : SampleOutput.DismClean();
            foreach (string line in result.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                onOutputLine(line);
            }

            return result;
        }
        finally
        {
            Interlocked.Decrement(ref _concurrent);
        }
    }

    private static RepairStepKind KindFor(IReadOnlyList<string> arguments) => arguments[^1] switch
    {
        "/CheckHealth" => RepairStepKind.DismCheckHealth,
        "/ScanHealth" => RepairStepKind.DismScanHealth,
        "/RestoreHealth" => RepairStepKind.DismRestoreHealth,
        "/scannow" => RepairStepKind.SfcScanNow,
        _ => throw new ArgumentException("Unexpected arguments: " + string.Join(' ', arguments)),
    };
}

internal sealed class RecordingObserver : IRepairObserver
{
    public List<string> Events { get; } = [];

    public List<double> Percents { get; } = [];

    public bool ThrowOnOutput { get; init; }

    public void StepStarted(int index, RepairStepDefinition step)
    {
        lock (Events)
        {
            Events.Add($"start:{step.Kind}");
        }
    }

    public void StepOutput(int index, string line, double? percent)
    {
        if (ThrowOnOutput)
        {
            throw new InvalidOperationException("UI exploded");
        }

        if (percent is double p)
        {
            lock (Percents)
            {
                Percents.Add(p);
            }
        }
    }

    public void StepFinished(int index, StepResult result)
    {
        lock (Events)
        {
            Events.Add($"finish:{result.Step.Kind}:{result.Status}");
        }
    }
}

/// <summary>An in-process step with a scripted result, recording whether it ran.</summary>
internal sealed class FakeInProcessStep(Func<StepAnalysis> result) : IInProcessStep
{
    public int Runs { get; private set; }

    public async Task<StepAnalysis> RunAsync(Action<string> onOutputLine)
    {
        Runs++;
        await Task.Yield();
        onOutputLine("fake step output");
        return result();
    }
}

/// <summary>Stands in for the Windows Update Agent: scripted search results and install outcomes.</summary>
internal sealed class FakeWindowsUpdateAgent : IWindowsUpdateAgent
{
    public Func<IReadOnlyList<WindowsUpdateInfo>> SearchResult { get; set; } = () => [];

    public Dictionary<string, Func<UpdateInstallOutcome>> InstallResults { get; } = new();

    public List<string> Installed { get; } = [];

    public IReadOnlyList<WindowsUpdateInfo> Search() => SearchResult();

    public UpdateInstallOutcome DownloadAndInstall(string updateId, Action<string> onProgress)
    {
        Installed.Add(updateId);
        onProgress("Installing…");
        return InstallResults.TryGetValue(updateId, out var result) ? result() : new UpdateInstallOutcome(true, false, null, null);
    }

    public static WindowsUpdateInfo Update(string id, bool optional = false, bool needsInput = false) =>
        new(id, $"Title {id}", 1024 * 1024, optional, IsDriver: optional, needsInput);
}
