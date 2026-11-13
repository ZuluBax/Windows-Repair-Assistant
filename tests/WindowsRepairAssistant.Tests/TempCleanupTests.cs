using System.Diagnostics;
using WindowsRepairAssistant.Core;

namespace WindowsRepairAssistant.Tests;

/// <summary>
/// Runs against the real file system in a private scratch folder, because the safety guarantees
/// (links, locked files) depend on real Windows behaviour that a fake could not prove.
/// </summary>
public sealed class TempCleanupTests : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "WRA-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _root;
    private readonly string _outside;

    public TempCleanupTests()
    {
        _root = Path.Combine(_scratch, "Temp");
        _outside = Path.Combine(_scratch, "Precious");
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_outside);
    }

    // rmdir rather than Directory.Delete(recursive): .NET's recursive delete fails with "access denied"
    // on a folder that contains a junction, and rmdir /s removes junctions without following them.
    public void Dispose() => RunCmd($"rmdir /s /q \"{_scratch}\"");

    [Fact]
    public void Deletes_files_and_nested_folders_but_keeps_the_root()
    {
        Write(Path.Combine(_root, "a.tmp"), 100);
        Write(Path.Combine(_root, "sub", "b.tmp"), 250);
        Write(Path.Combine(_root, "sub", "deeper", "c.tmp"), 50);
        Directory.CreateDirectory(Path.Combine(_root, "empty"));

        CleanupStats stats = SafeTreeCleaner.DeleteContents(_root);

        Assert.True(Directory.Exists(_root));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_root));
        Assert.Equal(3, stats.FilesDeleted);
        Assert.Equal(3, stats.FoldersDeleted);
        Assert.Equal(400, stats.BytesFreed);
        Assert.Equal(0, stats.InUse);
        Assert.Equal(0, stats.NotDeleted);
    }

    [Fact]
    public void Files_in_use_are_left_alone_along_with_their_folder()
    {
        string locked = Path.Combine(_root, "busy", "locked.tmp");
        Write(locked, 10);
        Write(Path.Combine(_root, "busy", "free.tmp"), 20);

        CleanupStats stats;
        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            stats = SafeTreeCleaner.DeleteContents(_root);
        }

        Assert.True(File.Exists(locked));
        Assert.False(File.Exists(Path.Combine(_root, "busy", "free.tmp")));
        Assert.Equal(1, stats.FilesDeleted);
        Assert.Equal(20, stats.BytesFreed);
        Assert.Equal(1, stats.InUse);
        Assert.Equal(0, stats.FoldersDeleted);
    }

    [Fact] // An installer may have staged this file to be moved into place at the next restart
    public void Files_waiting_for_a_restart_are_left_alone()
    {
        string staged = Path.Combine(_root, "staged", "new-driver.sys");
        Write(staged, 30);
        Write(Path.Combine(_root, "junk.tmp"), 5);

        CleanupStats stats = SafeTreeCleaner.DeleteContents(_root, protectedPaths: [staged.ToUpperInvariant()]);

        Assert.True(File.Exists(staged));
        Assert.False(File.Exists(Path.Combine(_root, "junk.tmp")));
        Assert.Equal(1, stats.FilesDeleted);
        Assert.Equal(1, stats.InUse);
    }

    [Fact] // Pairs of source, destination; an empty destination means "delete at restart"
    public void Only_files_waiting_to_be_moved_at_restart_are_protected()
    {
        string[] raw =
        [
            @"\??\C:\Windows\Temp\a.tmp", @"!\??\C:\Windows\System32\drivers\a.sys", // moved into place: keep
            @"\??\C:\Users\Bob\AppData\Local\Temp\b.tmp", "",                      // deleted anyway: no need to keep
        ];

        Assert.Equal([@"C:\Windows\Temp\a.tmp", @"C:\Windows\System32\drivers\a.sys"], TempFolders.ParsePendingRenamePaths(raw));
    }

    [Fact] // Seen on a real PC: thousands of delete-at-restart entries elsewhere must not protect anything in Temp
    public void Delete_at_restart_entries_protect_nothing()
    {
        string[] raw = [.. Enumerable.Range(0, 5726).SelectMany(i => new[] { $@"\??\C:\ProgramData\App\cache{i}.bin", "" })];

        Assert.Empty(TempFolders.ParsePendingRenamePaths(raw));
    }

    [Fact] // If the list is ever malformed (odd length), the pairs can't be trusted: protect every path
    public void A_malformed_list_protects_every_path()
    {
        string[] raw = [@"\??\C:\Windows\Temp\a.tmp", "", @"\??\C:\Windows\Temp\b.tmp"];

        Assert.Equal([@"C:\Windows\Temp\a.tmp", @"C:\Windows\Temp\b.tmp"], TempFolders.ParsePendingRenamePaths(raw));
    }

    [Fact]
    public void Read_only_files_are_deleted()
    {
        string file = Path.Combine(_root, "ro.tmp");
        Write(file, 5);
        File.SetAttributes(file, FileAttributes.ReadOnly);

        CleanupStats stats = SafeTreeCleaner.DeleteContents(_root);

        Assert.False(File.Exists(file));
        Assert.Equal(1, stats.FilesDeleted);
    }

    [Fact] // The privilege-escalation pattern: a junction in Temp pointing at files we must never touch
    public void Junction_to_another_folder_is_removed_without_touching_its_target()
    {
        string precious = Path.Combine(_outside, "important.doc");
        Write(precious, 42);
        string junction = Path.Combine(_root, "innocent-looking");
        RunCmd($"mklink /J \"{junction}\" \"{_outside}\"");
        Assert.True(File.Exists(Path.Combine(junction, "important.doc")), "test setup: junction should expose the target");

        CleanupStats stats = SafeTreeCleaner.DeleteContents(_root);

        Assert.True(File.Exists(precious));
        Assert.Equal(42, new FileInfo(precious).Length);
        Assert.False(Directory.Exists(junction));
        Assert.Equal(0, stats.BytesFreed);
    }

    [Fact]
    public void Hard_link_to_an_outside_file_never_deletes_the_outside_file()
    {
        string precious = Path.Combine(_outside, "important.doc");
        Write(precious, 42);
        RunCmd($"mklink /H \"{Path.Combine(_root, "link.tmp")}\" \"{precious}\"");

        SafeTreeCleaner.DeleteContents(_root);

        Assert.True(File.Exists(precious));
        Assert.Equal(42, new FileInfo(precious).Length);
    }

    [Fact]
    public void A_root_that_is_itself_a_link_is_refused()
    {
        string linkedRoot = Path.Combine(_scratch, "LinkedTemp");
        RunCmd($"mklink /J \"{linkedRoot}\" \"{_outside}\"");
        Write(Path.Combine(_outside, "important.doc"), 1);

        Assert.Throws<CleanupRefusedException>(() => SafeTreeCleaner.DeleteContents(linkedRoot));
        Assert.True(File.Exists(Path.Combine(_outside, "important.doc")));
    }

    [Fact]
    public void A_missing_root_is_reported()
    {
        Assert.Throws<DirectoryNotFoundException>(() => SafeTreeCleaner.DeleteContents(Path.Combine(_scratch, "nope")));
    }

    [Fact]
    public async Task Step_cleans_every_folder_and_reports_totals_in_plain_english()
    {
        string second = Path.Combine(_scratch, "Second", "Temp");
        Write(Path.Combine(_root, "a.tmp"), 1024 * 1024);
        Write(Path.Combine(second, "b.tmp"), 512 * 1024);
        var lines = new List<string>();

        var step = new TempCleanupStep(() => [new TempFolder("your Temp folder", _root, null), new TempFolder("the Windows Temp folder", second, null)]);
        StepAnalysis result = await step.RunAsync(lines.Add);

        Assert.Equal(StepStatus.Succeeded, result.Status);
        Assert.Equal("Deleted 2 files and freed 1.5 MB.", result.Message);
        Assert.Contains(lines, l => l.Contains(_root, StringComparison.OrdinalIgnoreCase));
        Assert.Empty(Directory.EnumerateFileSystemEntries(second));
    }

    [Fact]
    public async Task Step_mentions_files_in_use_and_folders_it_could_not_clean()
    {
        string locked = Path.Combine(_root, "locked.tmp");
        Write(locked, 10);

        var step = new TempCleanupStep(() =>
        [
            new TempFolder("your Temp folder", _root, null),
            new TempFolder("the Windows Temp folder", null, "it could not be found"),
        ]);

        StepAnalysis result;
        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            result = await step.RunAsync(_ => { });
        }

        Assert.Equal(StepStatus.CompletedWithIssues, result.Status);
        Assert.Equal("Deleted 0 files and freed 0 bytes. 1 file in use or waiting for a restart was left alone. The Windows Temp folder was not cleaned because it could not be found.", result.Message);
    }

    [Theory] // The check made on every item just before it is deleted, against links swapped in mid-run
    [InlineData(@"\\?\C:\Windows\Temp\a.tmp", true)]
    [InlineData(@"\\?\c:\windows\temp\Sub\a.tmp", true)]
    [InlineData(@"\\?\C:\Windows\Temp", false)]
    [InlineData(@"\\?\C:\Windows\Temp\", false)]
    [InlineData(@"\\?\C:\Windows\Temp2\a.tmp", false)]
    [InlineData(@"\\?\C:\Windows\System32\config\SAM", false)]
    public void Only_items_strictly_inside_the_folder_may_be_deleted(string finalPath, bool expected)
    {
        Assert.Equal(expected, SafeTreeCleaner.IsStrictlyInside(finalPath, @"\\?\C:\Windows\Temp"));
    }

    [Theory]
    [InlineData(@"C:\Users\Bob\AppData\Local\Temp", true)]
    [InlineData(@"C:\Users\Bob\AppData\Local\Temp\", true)]
    [InlineData(@"C:\Windows\Temp", true)]
    [InlineData(@"D:\tmp", true)]
    [InlineData(@"C:\", false)]
    [InlineData(@"C:\Users\Bob", false)]
    [InlineData(@"C:\Windows", false)]
    [InlineData(@"Temp", false)]
    [InlineData(@"\\server\share\Temp", false)]
    [InlineData(@"C:\Users\Bob\AppData\Local\Temp\..\..", false)]
    public void Only_local_folders_named_Temp_are_accepted(string path, bool expected)
    {
        Assert.Equal(expected, TempFolders.IsSafeToClean(path, out string? reason));
        Assert.Equal(expected, reason is null);
    }

    [Theory]
    [InlineData(@"%USERPROFILE%\AppData\Local\Temp", @"C:\Users\Bob\AppData\Local\Temp")]
    [InlineData(@"%userprofile%\AppData\Local\Temp", @"C:\Users\Bob\AppData\Local\Temp")]
    [InlineData(@"C:\Users\Bob\Scratch\Temp", @"C:\Users\Bob\Scratch\Temp")]
    // Set by the user but deleted from as administrator: never outside their own profile.
    [InlineData(@"D:\Temp", @"C:\Users\Bob\AppData\Local\Temp")]
    [InlineData(@"C:\Users\Admin\AppData\Local\Temp", @"C:\Users\Bob\AppData\Local\Temp")]
    [InlineData(@"C:\Users\Bobby\Temp", @"C:\Users\Bob\AppData\Local\Temp")]
    [InlineData(@"%USERPROFILE%\..\Admin\Temp", @"C:\Users\Bob\AppData\Local\Temp")]
    [InlineData(null, @"C:\Users\Bob\AppData\Local\Temp")]
    [InlineData("", @"C:\Users\Bob\AppData\Local\Temp")]
    // A variable we cannot expand for another user falls back to the standard location.
    [InlineData(@"%ONEDRIVE%\Temp", @"C:\Users\Bob\AppData\Local\Temp")]
    public void Another_users_temp_setting_is_expanded_against_their_profile(string? raw, string expected)
    {
        Assert.Equal(expected, TempFolders.ExpandUserTempSetting(raw, @"C:\Users\Bob"));
    }

    [Theory]
    [InlineData(0, "0 bytes")]
    [InlineData(1, "1 byte")]
    [InlineData(1023, "1,023 bytes")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(1024L * 1024 * 1024 * 3, "3.0 GB")]
    public void Sizes_are_formatted_for_people(long bytes, string expected)
    {
        Assert.Equal(expected, TempCleanupStep.FormatSize(bytes));
    }

    private static void Write(string path, int bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
    }

    private static void RunCmd(string command)
    {
        // A plain argument string: cmd.exe does not understand the backslash-escaped quotes ArgumentList would produce.
        using Process process = Process.Start(new ProcessStartInfo("cmd.exe", "/c " + command)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"'{command}' failed: {process.StandardError.ReadToEnd()}");
    }
}
