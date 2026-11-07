using System.ComponentModel;
using System.Diagnostics;

namespace WindowsRepairAssistant.Core;

/// <param name="ExitCode">Null only when the process never started.</param>
/// <param name="StartError">Set when the process could not be launched at all.</param>
public sealed record ProcessRunResult(
    int? ExitCode,
    string StandardOutput,
    string StandardError,
    TimeSpan Duration,
    string? StartError = null);

public interface IProcessRunner
{
    /// <summary>
    /// Runs a process to completion. Deliberately has no cancellation: interrupting DISM or SFC
    /// mid-repair can leave the component store worse off than before, so once started a step
    /// always runs to the end.
    /// </summary>
    /// <param name="onOutputLine">Called from a background thread for every stdout/stderr line.</param>
    Task<ProcessRunResult> RunAsync(string executablePath, IReadOnlyList<string> arguments, Action<string> onOutputLine);
}

public sealed class SystemProcessRunner : IProcessRunner
{
    public async Task<ProcessRunResult> RunAsync(string executablePath, IReadOnlyList<string> arguments, Action<string> onOutputLine)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(onOutputLine);

        var startInfo = new ProcessStartInfo(executablePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Environment.SystemDirectory,
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        var stopwatch = Stopwatch.StartNew();
        using var process = new Process { StartInfo = startInfo };

        try
        {
            if (!process.Start())
            {
                return new ProcessRunResult(null, "", "", stopwatch.Elapsed, "Windows did not start the process.");
            }
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            return new ProcessRunResult(null, "", "", stopwatch.Elapsed, ex.Message);
        }

        // Both pipes must be drained continuously, or the child blocks once a pipe buffer fills.
        // We read raw bytes rather than StandardOutput text because sfc.exe writes UTF-16 when
        // redirected while dism.exe writes in the OEM code page; ConsoleOutputReader detects which.
        Task<string> stdoutTask = ConsoleOutputReader.ReadAllAsync(process.StandardOutput.BaseStream, onOutputLine);
        Task<string> stderrTask = ConsoleOutputReader.ReadAllAsync(process.StandardError.BaseStream, onOutputLine);

        await process.WaitForExitAsync().ConfigureAwait(false);
        string stdout = await ReadSafelyAsync(stdoutTask).ConfigureAwait(false);
        string stderr = await ReadSafelyAsync(stderrTask).ConfigureAwait(false);

        return new ProcessRunResult(process.ExitCode, stdout, stderr, stopwatch.Elapsed);
    }

    private static async Task<string> ReadSafelyAsync(Task<string> readTask)
    {
        try
        {
            return await readTask.ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            // A broken pipe loses some output text but not the exit code, which is still authoritative.
            return $"[Windows Repair Assistant: output could not be fully read: {ex.Message}]";
        }
    }
}
