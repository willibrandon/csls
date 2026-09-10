using System.Diagnostics;
using System.Text;

namespace Csls.Debugger.Tests;

/// <summary>
/// Runs real debugger-test child processes with cancellation-safe tree ownership.
/// </summary>
internal static class DebuggerTestProcess
{
    /// <summary>
    /// Runs one redirected child process and returns its complete exit diagnostics.
    /// </summary>
    /// <param name="startInfo">The complete child-process start information.</param>
    /// <param name="cancellationToken">Cancels the process and terminates its complete tree.</param>
    /// <param name="progress">Optionally observes diagnostic lines while the child is still running.</param>
    /// <param name="diagnosticContext">Optionally captures the owned child's native stacks before cancellation cleanup.</param>
    /// <param name="observeProcess">Optionally observes the owned process until exit or capture cancellation.</param>
    /// <returns>The exit code, standard output, and standard error.</returns>
    internal static async Task<(int ExitCode, string Output, string Error)> RunAsync(
        ProcessStartInfo startInfo,
        CancellationToken cancellationToken,
        Action<string>? progress = null,
        TestContext? diagnosticContext = null,
        Func<Process, Action<string>, CancellationToken, Task>? observeProcess = null)
    {
        (int _, int exitCode, string output, string error) = await RunWithIdentityAsync(
            startInfo, cancellationToken, progress, diagnosticContext, observeProcess: observeProcess).ConfigureAwait(false);
        return (exitCode, output, error);
    }

    /// <summary>
    /// Runs one redirected child and preserves its process identity for operating-system crash diagnostics.
    /// </summary>
    /// <param name="startInfo">The complete child-process start information.</param>
    /// <param name="cancellationToken">Cancels the process and terminates its complete tree.</param>
    /// <param name="progress">Optionally observes diagnostic lines while the child is still running.</param>
    /// <param name="diagnosticContext">Optionally captures the owned child's native stacks before cancellation cleanup.</param>
    /// <param name="observeNativeExceptions">Whether to observe a Windows collector waiting for its capture input.</param>
    /// <param name="observeProcess">Optionally observes the owned process until exit or capture cancellation.</param>
    /// <param name="observeOutputDrain">Observes exited processes until their output drains; capture cancels and awaits the observer.</param>
    /// <returns>The process identifier, exit code, standard output, and standard error.</returns>
    internal static async Task<(int ProcessId, int ExitCode, string Output, string Error)> RunWithIdentityAsync(
        ProcessStartInfo startInfo,
        CancellationToken cancellationToken,
        Action<string>? progress = null,
        TestContext? diagnosticContext = null,
        bool observeNativeExceptions = false,
        Func<Process, Action<string>, CancellationToken, Task>? observeProcess = null,
        Func<Process, CancellationToken, Task>? observeOutputDrain = null)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        if (observeNativeExceptions && !OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Native collector observation requires Windows.");
        }
        cancellationToken.ThrowIfCancellationRequested();
        startInfo.RedirectStandardInput |= observeNativeExceptions;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        startInfo.UseShellExecute = false;
        using DebuggerCaptureTrace? trace = CreateTrace(diagnosticContext);
        long started = Stopwatch.GetTimestamp();
        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException(
                $"The debugger test process did not start: {startInfo.FileName}");
        return await CaptureAsync(process, progress, diagnosticContext, trace,
            observeNativeExceptions, observeProcess, observeOutputDrain, started, cancellationToken)
            .ConfigureAwait(false);
    }

    private static DebuggerCaptureTrace? CreateTrace(TestContext? context)
    {
        if (context is null)
        {
            return null;
        }
        string directory = Path.Join(DebuggerTestEnvironment.FindRepositoryRoot(), "artifacts", "test-results");
        Directory.CreateDirectory(directory);
        string path = Path.Join(directory, $"process-capture-{Guid.NewGuid():N}.log");
        var trace = new DebuggerCaptureTrace(path);
        try
        {
            context.AddResultFile(trace.FilePath);
            return trace;
        }
        catch
        {
            trace.Dispose();
            throw;
        }
    }

    private static async Task<(int ProcessId, int ExitCode, string Output, string Error)> CaptureAsync(
        Process process, Action<string>? progress, TestContext? diagnosticContext, DebuggerCaptureTrace? trace,
        bool observeNativeExceptions, Func<Process, Action<string>, CancellationToken, Task>? observeProcess,
        Func<Process, CancellationToken, Task>? observeOutputDrain,
        long started, CancellationToken cancellationToken)
    {
        using var observation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var drainObservation = CancellationTokenSource.CreateLinkedTokenSource(observation.Token);
        Action<string>? captureProgress = progress is null && trace is null ? null : ReportProgress;
        Task<string> output = ReadOutputAsync(process.StandardOutput, captureProgress, observation.Token);
        Task<string> error = ReadOutputAsync(process.StandardError, captureProgress, observation.Token);
        var nativeDiagnostics = new StringBuilder();
        Task nativeEvents = Task.CompletedTask;
        Task exit = Task.CompletedTask;
        Task processObservation = Task.CompletedTask;
        Task outputDrainObservation = Task.CompletedTask;
        try
        {
            ReportPhase("Started output capture");
            if (observeNativeExceptions && OperatingSystem.IsWindows())
            {
                nativeEvents = WindowsNativeDebugObserver.ObserveAsync(process, record =>
                {
                    nativeDiagnostics.AppendLine(record);
                    ReportProgress(record);
                }, observation.Token, ReportPhase, diagnosticContext);
            }
            // Native observation already waits for the kernel signal after continuing EXIT_PROCESS_DEBUG_EVENT.
            exit = observeNativeExceptions ? nativeEvents : DebuggerProcessExit.WaitAsync(process, observation.Token);
            processObservation = observeProcess?.Invoke(process, ReportProgress, observation.Token) ?? Task.CompletedTask;
            // Observe each operation as it completes: a failed sink must end capture while the child is still alive.
            List<Task> pending = [output, error, exit];
            if (observeProcess is not null)
            {
                pending.Add(processObservation);
            }
            while (pending.Count != 0)
            {
                Task completed = await Task.WhenAny(pending).ConfigureAwait(false);
                _ = pending.Remove(completed);
                await completed.ConfigureAwait(false);
                if (ReferenceEquals(completed, exit) && (!output.IsCompleted || !error.IsCompleted) &&
                    observeOutputDrain is not null)
                {
                    outputDrainObservation = observeOutputDrain(process, drainObservation.Token);
                    pending.Add(outputDrainObservation);
                }
                if (output.IsCompleted && error.IsCompleted)
                {
                    await drainObservation.CancelAsync().ConfigureAwait(false);
                }
                ReportPhase(ReferenceEquals(completed, output) ? "stdout completed"
                    : ReferenceEquals(completed, error) ? "stderr completed"
                    : ReferenceEquals(completed, exit)
                        ? observeNativeExceptions ? "Native observation completed" : "Process handle signaled"
                    : ReferenceEquals(completed, outputDrainObservation)
                        ? "Output-drain observation completed" : "Process observation completed");
            }
            await nativeEvents.ConfigureAwait(false);
            await exit.ConfigureAwait(false);
            return (
                process.Id,
                process.ExitCode,
                await output.ConfigureAwait(false),
                await error.ConfigureAwait(false) + nativeDiagnostics);
        }
        catch (Exception failure)
        {
            try
            {
                // Detach on the observer's owning thread before any diagnostic collector or process termination.
                await observation.CancelAsync().ConfigureAwait(false);
                await nativeEvents.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                if (cancellationToken.IsCancellationRequested && diagnosticContext is not null && !process.HasExited)
                {
                    await DebuggerProcessDiagnostics.CaptureAsync(process.Id, diagnosticContext).ConfigureAwait(false);
                }
            }
            finally
            {
                if (!process.HasExited)
                {
                    try
                    {
                        process.Kill(entireProcessTree: true);
                    }
                    catch (InvalidOperationException) when (process.HasExited)
                    {
                        await DebuggerProcessExit.WaitAsync(process, CancellationToken.None)
                            .ConfigureAwait(false);
                    }
                }
                await DebuggerProcessExit.WaitAsync(process, CancellationToken.None).ConfigureAwait(false);

                var operations = Task.WhenAll(output, error, nativeEvents, exit, processObservation, outputDrainObservation);
                await operations.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
            if (failure is OperationCanceledException && cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(failure.Message, failure, cancellationToken);
            }
            throw;
        }

        void ReportPhase(string phase)
        {
            if (diagnosticContext is not null)
            {
                ThreadPool.GetAvailableThreads(out int available, out _);
                string record = FormattableString.Invariant(
                    $"Process capture {process.Id}: {phase} at {Stopwatch.GetElapsedTime(started).TotalMilliseconds:F1} ms; pool threads={ThreadPool.ThreadCount}, available={available}, queued={ThreadPool.PendingWorkItemCount}.");
                trace?.WriteLine(record);
                diagnosticContext.WriteLine(record);
            }
        }

        void ReportProgress(string record)
        {
            trace?.WriteLine(record);
            progress?.Invoke(record);
        }
    }

    private static async Task<string> ReadOutputAsync(StreamReader reader, Action<string>? progress,
        CancellationToken cancellationToken)
    {
        if (progress is null)
        {
            return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        }

        var result = new StringBuilder();
        var line = new StringBuilder();
        char[] buffer = new char[4096];
        bool skipLineFeed = false;
        try
        {
            // Own partial lines so cancelling a read cannot discard an already captured diagnostic fragment.
            while (await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false) is int count && count > 0)
            {
                for (int index = 0; index < count; index++)
                {
                    char character = buffer[index];
                    if (skipLineFeed)
                    {
                        skipLineFeed = false;
                        if (character == '\n')
                        {
                            continue;
                        }
                    }
                    if (character is '\r' or '\n')
                    {
                        PublishLine(result, line, progress);
                        skipLineFeed = character == '\r';
                    }
                    else
                    {
                        line.Append(character);
                    }
                }
            }
        }
        finally
        {
            if (line.Length != 0)
            {
                PublishLine(result, line, progress);
            }
        }

        return result.ToString();
    }

    private static void PublishLine(StringBuilder result, StringBuilder line, Action<string> progress)
    {
        string record = line.ToString();
        line.Clear();
        result.AppendLine(record);
        progress(record);
    }
}
