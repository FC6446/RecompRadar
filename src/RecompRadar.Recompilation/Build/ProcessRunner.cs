using System.Diagnostics;
using System.Text;

namespace RecompRadar.Recompilation.Build;

public sealed record ProcessResult(int ExitCode, bool TimedOut, bool Cancelled, TimeSpan Duration);

/// <summary>Runs a child process without a console window and streams its output lines. Used for the .NET SDK build and for generated programs.</summary>
public static class ProcessRunner
{
    public static async Task<ProcessResult> RunAsync(
        string fileName,
        IEnumerable<string> arguments,
        string workingDirectory,
        IReadOnlyDictionary<string, string>? environment,
        Action<string>? onStandardOutput,
        Action<string>? onStandardError,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        if (environment is not null)
        {
            foreach (var (key, value) in environment)
            {
                info.Environment[key] = value;
            }
        }

        var stopwatch = Stopwatch.StartNew();
        using var process = new Process { StartInfo = info };
        var stdoutDone = new TaskCompletionSource();
        var stderrDone = new TaskCompletionSource();
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                stdoutDone.TrySetResult();
            }
            else
            {
                onStandardOutput?.Invoke(e.Data);
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                stderrDone.TrySetResult();
            }
            else
            {
                onStandardError?.Invoke(e.Data);
            }
        };

        process.Start();
        process.StandardInput.Close(); // programs never wait for input
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        var timedOut = false;
        var cancelled = false;
        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            timedOut = timeoutSource.IsCancellationRequested && !cancellationToken.IsCancellationRequested;
            cancelled = cancellationToken.IsCancellationRequested;
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // The process ended between the cancellation and the kill.
            }

            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }

        // Wait until the output pipes are drained so that no trailing lines are lost.
        try
        {
            await Task.WhenAll(stdoutDone.Task, stderrDone.Task).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // A grandchild process still holds the pipes; the lines received so far are all there is.
        }

        stopwatch.Stop();
        return new ProcessResult(process.ExitCode, timedOut, cancelled, stopwatch.Elapsed);
    }
}
