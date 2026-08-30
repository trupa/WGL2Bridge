using System.Diagnostics;

namespace WGL2Bridge.Platform;

/// <summary>
/// Small helper for invoking command-line tools (netsh, netbird) without leaking console windows.
/// Both output streams are drained concurrently so large output cannot deadlock the process.
/// </summary>
internal static class ProcessRunner
{
    /// <summary>Runs a process to completion and returns its exit code and combined output.</summary>
    public static (int ExitCode, string Output) Run(string fileName, string arguments, int timeoutMs = 20000)
    {
        var startInfo = new ProcessStartInfo(fileName, arguments)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            return (-1, string.Empty);
        }

        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();

        // A hung CLI (e.g. netbird status while the daemon is in a half-down state) must never block
        // the caller indefinitely; kill it and report a failure.
        if (!process.WaitForExit(timeoutMs))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // The process may have exited between the timeout and the kill.
            }

            process.WaitForExit();
            Drain(stdout);
            Drain(stderr);
            return (-1, $"Command '{fileName}' timed out after {timeoutMs} ms.");
        }

        string outText = Drain(stdout);
        string errText = Drain(stderr);

        return (process.ExitCode, string.IsNullOrEmpty(errText) ? outText : outText + Environment.NewLine + errText);
    }

    private static string Drain(Task<string> reader)
    {
        try
        {
            return reader.GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }
}
