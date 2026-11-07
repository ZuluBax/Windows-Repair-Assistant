using System.Text.RegularExpressions;

namespace WindowsRepairAssistant.Core;

/// <summary>
/// Turns raw DISM/SFC output into a status the user can trust.
/// </summary>
/// <remarks>
/// Message matching is English-only: DISM and SFC localise their output and expose no
/// machine-readable result. On non-English Windows we fall back to exit codes and say plainly
/// when a result could not be confirmed, rather than guessing that everything is fine.
/// </remarks>
public static partial class OutputAnalyzer
{
    // DISM returns HRESULTs as its exit code; these are the ones worth a specific explanation.
    private const uint ErrorElevationRequired = 740;
    private const uint HResultElevationRequired = 0x800702E4;
    private const uint HResultAccessDenied = 0x80070005;
    private const uint CbsPendingRestart = 0x800F082F;
    private static readonly uint[] SourceFilesNotFoundCodes = [0x800F081F, 0x800F0906, 0x800F0907, 0x800F0950];

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"error:?\s*(0x[0-9a-f]{8})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ErrorCodeInText();

    public static StepAnalysis Analyze(RepairStepKind kind, ProcessRunResult run)
    {
        ArgumentNullException.ThrowIfNull(run);

        if (run.StartError is not null)
        {
            return new StepAnalysis(StepStatus.Failed, StepFinding.CouldNotStart,
                $"The Windows tool could not be started ({run.StartError}).");
        }

        // SFC and DISM wrap long sentences across lines when redirected, so match on collapsed text.
        string text = Normalize(run.StandardOutput + " " + run.StandardError);

        return kind == RepairStepKind.SfcScanNow
            ? AnalyzeSfc(text, run.ExitCode)
            : AnalyzeDism(kind, text, run.ExitCode);
    }

    internal static string Normalize(string value) => Whitespace().Replace(value, " ").Trim().ToLowerInvariant();

    private static StepAnalysis AnalyzeDism(RepairStepKind kind, string text, int? exitCode)
    {
        if (kind != RepairStepKind.DismRestoreHealth)
        {
            // Order matters: "not repairable" contains "repairable".
            if (ContainsAny(text, "component store is not repairable", "component store cannot be repaired"))
            {
                return new StepAnalysis(StepStatus.CompletedWithIssues, StepFinding.CorruptionNotRepairable,
                    "Damage was found in the Windows component store that DISM cannot repair.");
            }

            if (text.Contains("no component store corruption detected", StringComparison.Ordinal))
            {
                return new StepAnalysis(StepStatus.Succeeded, StepFinding.NoCorruption,
                    "No damage was found in the Windows component store.");
            }

            if (text.Contains("component store is repairable", StringComparison.Ordinal))
            {
                return new StepAnalysis(StepStatus.CompletedWithIssues, StepFinding.CorruptionRepairable,
                    "Damage was found in the Windows component store. The repair step will try to fix it.");
            }
        }

        if (exitCode == 0)
        {
            return kind == RepairStepKind.DismRestoreHealth
                ? new StepAnalysis(StepStatus.Succeeded, StepFinding.None, "The Windows image repair finished successfully.")
                : new StepAnalysis(StepStatus.Succeeded, StepFinding.ResultUnrecognized, "The check finished without reporting an error.");
        }

        return DescribeDismFailure(text, exitCode);
    }

    private static StepAnalysis DescribeDismFailure(string text, int? exitCode)
    {
        uint? code = exitCode is int value ? unchecked((uint)value) : null;
        Match textCode = ErrorCodeInText().Match(text);
        if (textCode.Success)
        {
            code ??= Convert.ToUInt32(textCode.Groups[1].Value, 16);
        }

        string codeText = FormatCode(code);

        if (code is ErrorElevationRequired or HResultElevationRequired || text.Contains("elevated permissions are required", StringComparison.Ordinal))
        {
            return new StepAnalysis(StepStatus.Failed, StepFinding.NotElevated,
                "DISM needs administrator permission and did not receive it.");
        }

        if ((code is uint c && SourceFilesNotFoundCodes.Contains(c))
            || text.Contains("source files could not be found", StringComparison.Ordinal)
            || ContainsAnyCode(text, SourceFilesNotFoundCodes))
        {
            return new StepAnalysis(StepStatus.Failed, StepFinding.SourceFilesUnavailable,
                $"Windows could not get the replacement files it needs. This usually means there is no internet connection or Windows Update is blocked ({codeText}).");
        }

        if (code == CbsPendingRestart || ContainsAnyCode(text, [CbsPendingRestart]))
        {
            return new StepAnalysis(StepStatus.Failed, StepFinding.RestartPending,
                "Windows has a restart pending from an earlier update or repair, so DISM could not continue.");
        }

        if (code == HResultAccessDenied)
        {
            return new StepAnalysis(StepStatus.Failed, StepFinding.ToolError,
                $"Windows denied access to a file DISM needed. Security software can sometimes cause this ({codeText}).");
        }

        return new StepAnalysis(StepStatus.Failed, StepFinding.ToolError,
            $"DISM reported an error and could not finish ({codeText}).");
    }

    private static StepAnalysis AnalyzeSfc(string text, int? exitCode)
    {
        if (text.Contains("did not find any integrity violations", StringComparison.Ordinal))
        {
            return new StepAnalysis(StepStatus.Succeeded, StepFinding.NoCorruption,
                "No damaged system files were found.");
        }

        if (text.Contains("found corrupt files and successfully repaired them", StringComparison.Ordinal))
        {
            return new StepAnalysis(StepStatus.Succeeded, StepFinding.Repaired,
                "Damaged system files were found and successfully repaired.");
        }

        if (text.Contains("found corrupt files but was unable to fix some of them", StringComparison.Ordinal))
        {
            return new StepAnalysis(StepStatus.CompletedWithIssues, StepFinding.PartiallyRepaired,
                "Damaged system files were found, but some of them could not be repaired.");
        }

        if (text.Contains("system repair pending", StringComparison.Ordinal))
        {
            return new StepAnalysis(StepStatus.CompletedWithIssues, StepFinding.RestartPending,
                "A previous system repair is waiting for a restart, so System File Checker could not scan.");
        }

        if (text.Contains("must be an administrator", StringComparison.Ordinal))
        {
            return new StepAnalysis(StepStatus.Failed, StepFinding.NotElevated,
                "System File Checker needs administrator permission and did not receive it.");
        }

        if (text.Contains("could not start the repair service", StringComparison.Ordinal))
        {
            return new StepAnalysis(StepStatus.Failed, StepFinding.ToolError,
                "The Windows Modules Installer service could not be started, so System File Checker could not run.");
        }

        if (text.Contains("could not perform the requested operation", StringComparison.Ordinal))
        {
            return new StepAnalysis(StepStatus.Failed, StepFinding.ToolError,
                "System File Checker could not complete the scan.");
        }

        if (exitCode is int code && code != 0)
        {
            return new StepAnalysis(StepStatus.Failed, StepFinding.ToolError,
                $"System File Checker stopped with an error ({FormatCode(unchecked((uint)code))}).");
        }

        // SFC's exit code does not distinguish "clean" from "found problems", so without a
        // recognised message we must not claim success.
        return new StepAnalysis(StepStatus.CompletedWithIssues, StepFinding.ResultUnrecognized,
            "System File Checker finished, but its result could not be confirmed automatically. See the log for details.");
    }

    internal static string FormatCode(uint? code) => code is uint c ? $"error code 0x{c:X8}" : "unknown error code";

    private static bool ContainsAny(string text, params string[] phrases) =>
        phrases.Any(p => text.Contains(p, StringComparison.Ordinal));

    private static bool ContainsAnyCode(string text, uint[] codes) =>
        codes.Any(c => text.Contains($"0x{c:x8}", StringComparison.Ordinal));
}
