using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Drives the production DAP session through real anonymous operating-system pipes.
/// </summary>
internal sealed partial class DapTestClient
{
    /// <summary>
    /// Waits for the production DAP process to finish.
    /// </summary>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>The process exit code.</returns>
    internal async Task<int> WaitForExitAsync(CancellationToken cancellationToken)
    {
        Process process = _process ?? throw new InvalidOperationException(
            "The DAP test client has not been initialized.");
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        await _diagnostics.ConfigureAwait(false);
        return process.ExitCode;
    }

    /// <summary>
    /// Closes standard input so the adapter observes end of stream.
    /// </summary>
    /// <returns>A completed task after the input stream closes.</returns>
    internal ValueTask CloseProtocolAsync()
    {
        Process process = _process ?? throw new InvalidOperationException(
            "The DAP test client has not been initialized.");
        process.StandardInput.Close();
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Process? process = _process;
        if (process is not null)
        {
            process.StandardInput.Close();
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            await _diagnostics.ConfigureAwait(false);
            if (_pendingMessage is Task<JsonDocument> pendingMessage)
            {
                try
                {
                    using JsonDocument message = await pendingMessage
                        .WaitAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception exception) when (
                    exception is OperationCanceledException or IOException or
                    JsonException or ObjectDisposedException)
                {
                    Debug.Assert(process.HasExited);
                }

                _pendingMessage = null;
            }

            process.Dispose();
        }

        while (_bufferedMessages.TryDequeue(out JsonDocument? message))
        {
            message.Dispose();
        }

        Diagnostics.Dispose();
    }

    private Task InitializeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string repositoryRoot = FindRepositoryRoot();
        string applicationPath = Path.Join(
            repositoryRoot,
            "artifacts",
            "bin",
            "Csls.App",
            "debug",
            "csls.dll");
        var startInfo = new ProcessStartInfo
        {
            FileName = ResolveDotNetHost(),
            WorkingDirectory = repositoryRoot,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add(applicationPath);
        startInfo.ArgumentList.Add("debugger");
        startInfo.ArgumentList.Add("dap");
        string? configuredWorkerPath = Environment.GetEnvironmentVariable(
            "CSLS_DEBUGGER_WORKER_TEST_PATH");
        startInfo.Environment["CSLS_DEBUGGER_WORKER_PATH"] =
            string.IsNullOrWhiteSpace(configuredWorkerPath)
                ? Path.Join(
                    repositoryRoot,
                    "artifacts",
                    "bin",
                    "Csls.Debugger.Worker",
                    "debug",
                    "csls-debugger-worker.dll")
                : Path.GetFullPath(configuredWorkerPath);
        _process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The csls debugger command did not start.");
        _diagnostics = new ValueTask(CaptureDiagnosticsAsync(_process.StandardError));
        return Task.CompletedTask;
    }

    private async Task CaptureDiagnosticsAsync(StreamReader reader)
    {
        char[] buffer = new char[1024];
        while (true)
        {
            int count = await reader.ReadAsync(buffer).ConfigureAwait(false);
            if (count == 0)
            {
                return;
            }

            await Diagnostics.WriteAsync(buffer.AsMemory(0, count)).ConfigureAwait(false);
        }
    }

    private static string ResolveDotNetHost()
    {
        string? configured = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        return string.IsNullOrWhiteSpace(configured) ? "dotnet" : configured;
    }

    private static string FindRepositoryRoot([CallerFilePath] string sourcePath = "")
        => DebuggerTestEnvironment.FindRepositoryRoot(sourcePath);
}
