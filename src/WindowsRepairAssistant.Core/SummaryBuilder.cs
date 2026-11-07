namespace WindowsRepairAssistant.Core;

public enum OverallOutcome
{
    NoProblemsFound,
    ProblemsRepaired,
    ProblemsRemain,
    Incomplete,
    Unconfirmed,
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
    public static RepairSummary Build(IReadOnlyList<StepResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);

        bool Has(StepFinding finding) => results.Any(r => r.Finding == finding);
        StepResult? Sfc() => results.FirstOrDefault(r => r.Step.Kind == RepairStepKind.SfcScanNow);

        bool dismRepaired = results.Any(r => r.Step.IsDism && r.Finding == StepFinding.Repaired);
        bool sfcRepaired = Sfc()?.Finding == StepFinding.Repaired;
        bool anyRepaired = dismRepaired || sfcRepaired;

        bool dismCorruptionUnfixed = Has(StepFinding.CorruptionRepairable) && !dismRepaired;
        bool problemsRemain = Has(StepFinding.PartiallyRepaired) || Has(StepFinding.CorruptionNotRepairable) || dismCorruptionUnfixed;
        bool incomplete = results.Any(r => r.Status is StepStatus.Failed or StepStatus.Skipped);
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

        if (outcome == OverallOutcome.NoProblemsFound)
        {
            details.Add("No changes were made to your system.");
        }

        bool restartRecommended = anyRepaired || restartPending || Has(StepFinding.PartiallyRepaired);

        return new RepairSummary(outcome, headline, details.Distinct().ToList(), restartRecommended);
    }
}
