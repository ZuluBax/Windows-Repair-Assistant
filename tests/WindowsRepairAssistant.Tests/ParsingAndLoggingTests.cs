using System.Text;
using WindowsRepairAssistant.Core;

namespace WindowsRepairAssistant.Tests;

public class ProgressParserTests
{
    [Theory]
    [InlineData("[==========================100.0%==========================]", 100.0)]
    [InlineData("[=====                      14.3%                          ]", 14.3)]
    [InlineData("Verification 45% complete.", 45.0)]
    [InlineData("Vérification 45 % terminée.", 45.0)]
    [InlineData("[===   7,5%   ]", 7.5)]
    public void Parses_real_percentages(string line, double expected) =>
        Assert.Equal(expected, ProgressParser.TryParsePercent(line));

    [Theory]
    [InlineData("Beginning verification phase of system scan.")]
    [InlineData("The operation completed successfully.")]
    [InlineData("")]
    [InlineData("Value 250% is not a progress value")]
    public void Returns_null_without_progress(string line) => Assert.Null(ProgressParser.TryParsePercent(line));
}

public class ConsoleOutputReaderTests
{
    [Fact]
    public async Task Decodes_utf16_sfc_output_and_splits_on_carriage_returns()
    {
        byte[] bytes = Encoding.Unicode.GetBytes("Beginning verification phase of system scan.\r\nVerification 3% complete.\rVerification 57% complete.\r\nWindows Resource Protection did not find any integrity violations.\r\n");
        var lines = new List<string>();

        string all = await ConsoleOutputReader.ReadAllAsync(new MemoryStream(bytes), lines.Add);

        Assert.Equal(
            ["Beginning verification phase of system scan.", "Verification 3% complete.", "Verification 57% complete.", "Windows Resource Protection did not find any integrity violations."],
            lines);
        Assert.Contains("integrity violations", all, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Decodes_single_byte_dism_output()
    {
        byte[] bytes = Encoding.ASCII.GetBytes("Deployment Image Servicing and Management tool\r\n[====  20.0%   ]\r[=======  40.0%  ]\r\nNo component store corruption detected.\r\n");
        var lines = new List<string>();

        await ConsoleOutputReader.ReadAllAsync(new MemoryStream(bytes), lines.Add);

        Assert.Equal(4, lines.Count);
        Assert.Equal("No component store corruption detected.", lines[^1]);
    }

    [Fact]
    public async Task Handles_short_output_below_detection_threshold()
    {
        var lines = new List<string>();
        await ConsoleOutputReader.ReadAllAsync(new MemoryStream(Encoding.ASCII.GetBytes("OK")), lines.Add);
        Assert.Equal(["OK"], lines);
    }

    [Fact]
    public async Task Handles_empty_output()
    {
        var lines = new List<string>();
        string all = await ConsoleOutputReader.ReadAllAsync(new MemoryStream(), lines.Add);
        Assert.Empty(lines);
        Assert.Equal("", all);
    }

    [Fact]
    public void Detects_utf16_by_zero_high_bytes() =>
        Assert.Equal(Encoding.Unicode, ConsoleOutputReader.DetectEncoding(Encoding.Unicode.GetBytes("Windows Resource Protection")));
}

public sealed class LogStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wra-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task Completed_session_log_contains_required_sections_and_end_marker()
    {
        var store = new LogStore(_root, hardenPermissions: false);
        string path;
        using (FileSessionLog log = store.CreateSessionLog(DateTimeOffset.Now, TimeProvider.System))
        {
            path = log.FilePath!;
            var runner = new FakeProcessRunner()
                .Returns(RepairStepKind.DismRestoreHealth, SampleOutput.DismRestoreOk)
                .Returns(RepairStepKind.SfcScanNow, SampleOutput.SfcRepaired);
            await new RepairOrchestrator(runner, RepairPlan.Create(@"C:\Windows\System32"), log,
                new SessionContext("1.0.0", "Windows 11 Pro 24H2"), TimeProvider.System).RunAsync(new RecordingObserver());
        }

        string text = await File.ReadAllTextAsync(path);
        Assert.Contains("Windows version:  Windows 11 Pro 24H2", text, StringComparison.Ordinal);
        Assert.Contains(@"C:\Windows\System32\dism.exe /Online /Cleanup-Image /CheckHealth", text, StringComparison.Ordinal);
        Assert.Contains("Exit code: 0", text, StringComparison.Ordinal);
        Assert.Contains("Total duration:", text, StringComparison.Ordinal);
        Assert.Contains("found corrupt files and successfully repaired them", text, StringComparison.Ordinal);
        Assert.EndsWith(FileSessionLog.EndMarker + Environment.NewLine, text, StringComparison.Ordinal);
        Assert.Null(store.FindInterruptedSessionLog());
    }

    [Fact]
    public void Session_without_end_marker_is_detected_as_interrupted()
    {
        var store = new LogStore(_root, hardenPermissions: false);
        string path;
        using (FileSessionLog log = store.CreateSessionLog(DateTimeOffset.Now, TimeProvider.System))
        {
            path = log.FilePath!;
            log.SessionStarted(new SessionContext("1.0.0", "Windows"), DateTimeOffset.Now, 4);
        }

        Assert.Equal(path, store.FindInterruptedSessionLog());
    }

    [Fact]
    public void Log_files_are_never_overwritten()
    {
        var store = new LogStore(_root, hardenPermissions: false);
        var now = DateTimeOffset.Now;
        using FileSessionLog first = store.CreateSessionLog(now, TimeProvider.System);
        using FileSessionLog second = store.CreateSessionLog(now, TimeProvider.System);
        Assert.NotEqual(first.FilePath, second.FilePath);
    }

    [Fact]
    public void Prune_keeps_newest_logs()
    {
        var store = new LogStore(_root, hardenPermissions: false);
        CreateLogs(store, LogStore.FilePrefix, count: 5);

        store.PruneOldLogs(keep: 2);

        Assert.Equal(["repair-20260103.log", "repair-20260104.log"], RemainingFiles(store));
    }

    [Fact]
    public void Default_retention_keeps_five_repair_and_five_crash_logs_independently()
    {
        var store = new LogStore(_root, hardenPermissions: false);
        CreateLogs(store, LogStore.FilePrefix, count: 7);
        CreateLogs(store, LogStore.CrashFilePrefix, count: 9, newerThanRepairLogs: true);

        store.PruneOldLogs();

        string[] remaining = RemainingFiles(store);
        Assert.Equal(5, LogStore.LogsToKeepPerKind);
        Assert.Equal(["repair-20260102.log", "repair-20260103.log", "repair-20260104.log", "repair-20260105.log", "repair-20260106.log"],
            remaining.Where(f => f.StartsWith(LogStore.FilePrefix, StringComparison.Ordinal)));
        Assert.Equal(["crash-20260104.log", "crash-20260105.log", "crash-20260106.log", "crash-20260107.log", "crash-20260108.log"],
            remaining.Where(f => f.StartsWith(LogStore.CrashFilePrefix, StringComparison.Ordinal)));
    }

    [Fact]
    public void Prune_ignores_unrelated_files()
    {
        var store = new LogStore(_root, hardenPermissions: false);
        CreateLogs(store, LogStore.FilePrefix, count: 1);
        File.WriteAllText(Path.Combine(store.LogDirectory, "notes.txt"), "keep me");

        store.PruneOldLogs(keep: 0);

        Assert.Equal(["notes.txt"], RemainingFiles(store));
    }

    private static void CreateLogs(LogStore store, string prefix, int count, bool newerThanRepairLogs = false)
    {
        store.EnsureDirectory();
        for (int i = 0; i < count; i++)
        {
            string file = Path.Combine(store.LogDirectory, $"{prefix}2026010{i}{LogStore.FileExtension}");
            File.WriteAllText(file, "x");
            File.SetCreationTimeUtc(file, DateTime.UtcNow.AddDays(i + (newerThanRepairLogs ? 100 : 0)));
        }
    }

    private static string[] RemainingFiles(LogStore store) =>
        Directory.GetFiles(store.LogDirectory).Select(f => Path.GetFileName(f)!).Order(StringComparer.Ordinal).ToArray();
}
