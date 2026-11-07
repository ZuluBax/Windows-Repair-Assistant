using System.ComponentModel;
using System.Diagnostics;

namespace WindowsRepairAssistant.Core;

public sealed record RestartRequestResult(bool Succeeded, string? ErrorMessage);

public static class WindowsRestart
{
    private const int ShutdownAlreadyInProgress = 1190;
    private static readonly TimeSpan ShutdownExeTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Asks Windows to restart via the built-in shutdown.exe. Callers must have obtained explicit
    /// user confirmation first; this method never prompts.
    /// </summary>
    public static async Task<RestartRequestResult> RequestRestartAsync()
    {
        var startInfo = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "shutdown.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        // /r restart, /t 0 no delay (the user already confirmed), /d p:0:0 records a planned restart.
        foreach (string argument in new[] { "/r", "/t", "0", "/d", "p:0:0" })
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            using Process? process = Process.Start(startInfo);
            if (process is null)
            {
                return new RestartRequestResult(false, "Windows did not start the restart command.");
            }

            using var timeout = new CancellationTokenSource(ShutdownExeTimeout);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);

            return process.ExitCode switch
            {
                0 => new RestartRequestResult(true, null),
                ShutdownAlreadyInProgress => new RestartRequestResult(true, null),
                int code => new RestartRequestResult(false, $"Windows refused the restart request (code {code})."),
            };
        }
        catch (OperationCanceledException)
        {
            return new RestartRequestResult(false, "Windows did not respond to the restart request in time.");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return new RestartRequestResult(false, ex.Message);
        }
    }
}
