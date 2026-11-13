using WindowsRepairAssistant.Core;
using Xunit.Abstractions;

namespace WindowsRepairAssistant.Tests;

/// <summary>
/// Runs the real Windows tools through the production process runner, using only read-only commands.
/// Opt-in because they take minutes and need an elevated shell:
///   set WRA_RUN_REAL_TOOLS=1 &amp;&amp; dotnet test --filter Category=RealTools
/// </summary>
[Trait("Category", "RealTools")]
public class RealToolIntegrationTests(ITestOutputHelper output)
{
    private static bool Enabled => Environment.GetEnvironmentVariable("WRA_RUN_REAL_TOOLS") == "1";

    [Fact]
    public async Task Dism_check_health_produces_a_recognised_result()
    {
        if (!Enabled)
        {
            return;
        }

        RepairStepDefinition step = RepairPlan.CreateDefault()[0];
        var lines = new List<string>();
        ProcessRunResult run = await new SystemProcessRunner().RunAsync(step.ExecutablePath, step.Arguments, lines.Add);
        StepAnalysis analysis = OutputAnalyzer.Analyze(step.Kind, run);

        output.WriteLine($"Exit {run.ExitCode} in {run.Duration}; {analysis.Status}/{analysis.Finding}: {analysis.Message}");
        lines.ForEach(output.WriteLine);

        Assert.Null(run.StartError);
        Assert.Contains(lines, l => l.Contains("Deployment Image Servicing and Management", StringComparison.Ordinal));
        Assert.NotEqual(StepFinding.ResultUnrecognized, analysis.Finding);
    }

    [Fact]
    public async Task Sfc_verify_only_output_is_decoded_and_recognised()
    {
        if (!Enabled)
        {
            return;
        }

        // /verifyonly scans exactly like /scannow but never repairs, so it is safe to run on a dev machine.
        string sfc = Path.Combine(Environment.SystemDirectory, "sfc.exe");
        var lines = new List<string>();
        var percents = new List<double>();
        ProcessRunResult run = await new SystemProcessRunner().RunAsync(sfc, ["/verifyonly"], line =>
        {
            lines.Add(line);
            if (ProgressParser.TryParsePercent(line) is double p)
            {
                percents.Add(p);
            }
        });
        StepAnalysis analysis = OutputAnalyzer.Analyze(RepairStepKind.SfcScanNow, run);

        output.WriteLine($"Exit {run.ExitCode} in {run.Duration}; {analysis.Status}/{analysis.Finding}: {analysis.Message}");
        output.WriteLine($"{percents.Count} progress updates, last {percents.LastOrDefault()}");
        lines.Where(l => ProgressParser.TryParsePercent(l) is null).ToList().ForEach(output.WriteLine);

        Assert.Null(run.StartError);
        Assert.DoesNotContain(lines, l => l.Contains('\0'));
        Assert.NotEmpty(percents);
        Assert.NotEqual(StepFinding.ResultUnrecognized, analysis.Finding);
    }

    [Fact] // Read-only: only works out where the folders are, deletes nothing.
    public void Temp_folders_resolve_to_this_users_temp_and_windows_temp()
    {
        if (!Enabled)
        {
            return;
        }

        IReadOnlyList<TempFolder> folders = TempFolders.Locate();
        folders.ToList().ForEach(f => output.WriteLine($"{f.Description}: {f.Path ?? "(none)"} {f.Problem}"));

        Assert.Equal(Path.TrimEndingDirectorySeparator(Path.GetTempPath()), folders[0].Path, ignoreCase: true);
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp"), folders[1].Path, ignoreCase: true);
    }

    [Fact] // Read-only: searches like Settings > Windows Update does, installs nothing. Needs the internet.
    public void Windows_update_search_returns_a_readable_list()
    {
        if (!Enabled)
        {
            return;
        }

        var agent = new WuaWindowsUpdateAgent();
        var timer = System.Diagnostics.Stopwatch.StartNew();
        IReadOnlyList<WindowsUpdateInfo> updates = agent.Search();

        output.WriteLine($"{updates.Count} updates in {timer.Elapsed}");
        updates.ToList().ForEach(u => output.WriteLine($"{(u.IsOptional ? "optional" : "important")} driver={u.IsDriver} input={u.NeedsUserInput} {u.DownloadSizeBytes:N0} B  {u.Title}"));

        Assert.All(updates, u =>
        {
            Assert.False(string.IsNullOrWhiteSpace(u.Id));
            Assert.False(string.IsNullOrWhiteSpace(u.Title));
            Assert.True(u.DownloadSizeBytes >= 0);
        });
    }
}
