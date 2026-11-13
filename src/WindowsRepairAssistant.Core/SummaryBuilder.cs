namespace WindowsRepairAssistant.Core;

public enum OverallOutcome
{
    NoProblemsFound,
    ProblemsRepaired,
    ProblemsRemain,
    Incomplete,
    Unconfirmed,
    UpdatesInstalled,
    UpdatesPartlyInstalled,
    UpdatesNotInstalled,
}

public sealed record RepairSummary(
    OverallOutcome Outcome,
    string Headline,
    IReadOnlyList<string> Details,
    bool RestartRecommended);

/// <summary>
/// Builds the plain-English verdict. Rule: only claim "no problems" or "repaired" when a tool
/// explicitly said so — never infer it from a command simply finishing.
/// </summary>
public static class SummaryBuilder
{
    public static RepairSummary Build(IReadOnlyList<StepResult> allResults)
    {
        ArgumentNullException.ThrowIfNull(allResults);

        if (allResults.Count > 0 && allResults.All(r => r.Step.Kind == RepairStepKind.InstallWindowsUpdate))
        {
            return BuildForUpdateInstall(allResults);
        }

        // The verdict comes from DISM and SFC only: a temp cleanup that skipped locked files, or an
        // update check with no internet, says nothing about whether Windows itself is healthy.
        List<StepResult> results = allResults.Where(r => r.Step.IsRepairTool).ToList();

        bool Has(StepFinding finding) => results.Any(r => r.Finding == finding);
        StepResult? Sfc() => results.FirstOrDefault(r => r.Step.Kind == RepairStepKind.SfcScanNow);

        bool dismRepaired = results.Any(r => r.Step.IsDism && r.Finding == StepFinding.Repaired);
        bool sfcRepaired = Sfc()?.Finding == StepFinding.Repaired;
        bool anyRepaired = dismRepaired || sfcRepaired;

        bool dismCorruptionUnfixed = Has(StepFinding.CorruptionRepairable) && !dismRepaired;
        bool problemsRemain = Has(StepFinding.PartiallyRepaired) || Has(StepFinding.CorruptionNotRepairable) || dismCorruptionUnfixed;
        // A repair skipped because the scan proved it unnecessary is a clean result, not a missing check.
        bool incomplete = results.Any(r => r.Status == StepStatus.Failed
                                           || (r.Status == StepStatus.Skipped && r.Finding != StepFinding.NoRepairNeeded));
        bool restartPending = Has(StepFinding.RestartPending);

        bool dismConfirmedClean = results.Any(r => r.Step.IsDism && r.Finding == StepFinding.NoCorruption)
                                  && !Has(StepFinding.CorruptionRepairable);
        bool allClean = dismConfirmedClean && Sfc()?.Finding == StepFinding.NoCorruption && !incomplete;

        (OverallOutcome outcome, string headline) = true switch
        {
            _ when problemsRemain => (OverallOutcome.ProblemsRemain,
                "Windows found problems that could not be fully repaired."),
            _ when incomplete && anyRepaired => (OverallOutcome.Incomplete,
                "Some problems were repaired, but not every check could be completed."),
            _ when incomplete || restartPending => (OverallOutcome.Incomplete,
                "The repair could not be fully completed."),
            _ when anyRepaired => (OverallOutcome.ProblemsRepaired,
                "Windows found problems and repaired them."),
            _ when allClean => (OverallOutcome.NoProblemsFound,
                "Windows system integrity checks did not identify problems requiring repair."),
            _ => (OverallOutcome.Unconfirmed,
                "The checks finished without errors, but some results could not be confirmed automatically."),
        };

        var details = new List<string>();

        if (dismRepaired)
        {
            details.Add("DISM found damage in the Windows component store and repaired it.");
        }

        if (sfcRepaired)
        {
            details.Add("System File Checker found damaged system files and repaired them.");
        }

        if (Has(StepFinding.PartiallyRepaired))
        {
            details.Add("System File Checker found damaged system files but could not repair some of them. Restart your computer and run the repair again. If the problem continues, a Windows \"repair install\" (in-place upgrade) may be needed.");
        }

        if (Has(StepFinding.CorruptionNotRepairable))
        {
            details.Add("DISM reported damage to the Windows component store that it cannot repair automatically. A Windows \"repair install\" (in-place upgrade) may be needed.");
        }

        if (Has(StepFinding.SourceFilesUnavailable))
        {
            details.Add("Windows could not download the files it needed for the repair. Check that you are connected to the internet, then run the repair again.");
        }

        if (Has(StepFinding.NotElevated))
        {
            details.Add("A repair tool did not receive administrator permission. Close the app, open it again and choose \"Yes\" when Windows asks for permission.");
        }

        if (restartPending)
        {
            details.Add("Windows is waiting for a restart from an earlier update or repair. Restart your computer, then run the repair again.");
        }

        var toolFailures = results.Where(r => r.Status == StepStatus.Failed && r.Finding is StepFinding.ToolError or StepFinding.CouldNotStart).ToList();
        foreach (StepResult failed in toolFailures)
        {
            details.Add($"{failed.Step.DisplayName} could not complete: {failed.Message}");
        }

        if (toolFailures.Count > 0)
        {
            details.Add("Restarting your computer and running the repair again often resolves this. If it keeps happening, the log file contains details a technician can use.");
        }

        if (Has(StepFinding.ResultUnrecognized))
        {
            details.Add("Some results were not in a format this app recognises (this can happen on non-English Windows). The log file contains the full output.");
        }

        StepResult? cleanup = allResults.FirstOrDefault(r => r.Step.Kind == RepairStepKind.CleanTempFiles);
        if (outcome == OverallOutcome.NoProblemsFound)
        {
            // Deleting temp files is a change, so the usual reassurance would be untrue after a cleanup;
            // a failed cleanup may still have deleted some files before it stopped.
            details.Add(cleanup is null || cleanup.Status == StepStatus.Skipped
                ? "No changes were made to your system."
                : "No repairs were made to Windows.");
        }

        if (cleanup is not null)
        {
            details.Add($"Temporary files: {cleanup.Message}");
        }

        StepResult? updateCheck = allResults.FirstOrDefault(r => r.Step.Kind == RepairStepKind.CheckWindowsUpdates);
        if (updateCheck is not null)
        {
            details.Add($"Windows Update: {updateCheck.Message}");
        }

        bool restartRecommended = anyRepaired || restartPending || Has(StepFinding.PartiallyRepaired);

        return new RepairSummary(outcome, headline, details.Distinct().ToList(), restartRecommended);
    }

    private static bool WasInstalled(StepResult r) =>
        r.Finding is StepFinding.UpdateInstalled or StepFinding.UpdateInstalledRestartRequired;

    private static RepairSummary BuildForUpdateInstall(IReadOnlyList<StepResult> results)
    {
        int installed = results.Count(WasInstalled);
        bool restartRequired = results.Any(r => r.Finding == StepFinding.UpdateInstalledRestartRequired);

        (OverallOutcome outcome, string headline) = true switch
        {
            _ when installed == results.Count => (OverallOutcome.UpdatesInstalled,
                results.Count == 1 ? "The update was installed." : "All the selected updates were installed."),
            _ when installed > 0 => (OverallOutcome.UpdatesPartlyInstalled,
                $"{installed} of {results.Count} updates were installed. The others could not be installed."),
            _ => (OverallOutcome.UpdatesNotInstalled,
                results.Count == 1 ? "The update could not be installed." : "The selected updates could not be installed."),
        };

        var details = new List<string>();
        foreach (StepResult failed in results.Where(r => !WasInstalled(r)))
        {
            details.Add($"{failed.Step.Title}: {failed.Message}");
        }

        if (installed < results.Count)
        {
            details.Add("You can try again later, or install them from Settings > Windows Update. The log file contains the error details.");
        }

        if (restartRequired)
        {
            details.Add("Windows needs a restart to finish installing the updates.");
        }

        return new RepairSummary(outcome, headline, details.Distinct().ToList(), restartRequired);
    }
}
