using Csls.Debugger.Control;
using System.Diagnostics;

namespace Csls.Mcp.Worker;

/// <summary>
/// Starts and releases the supervised debugger worker process.
/// </summary>
internal sealed partial class McpDebuggerSession
{
    /// <summary>
    /// Starts one debugger worker connected only through inherited standard-stream handles.
    /// </summary>
    /// <param name="workerPath">The absolute debugger worker path.</param>
    /// <param name="id">The stable MCP session identifier.</param>
    /// <param name="kind">How the target will be acquired.</param>
    /// <param name="agentControl">Whether target control is explicitly allowed.</param>
    /// <param name="cancellationToken">The startup cancellation token.</param>
    /// <returns>An ownership lease for the connected supervised session.</returns>
    internal static async Task<McpDebuggerSessionLease> StartAsync(
        string workerPath,
        string id,
        McpDebuggerSessionKind kind,
        bool agentControl,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workerPath);
        bool managedWorker = string.Equals(
            Path.GetExtension(workerPath),
            ".dll",
            StringComparison.OrdinalIgnoreCase);
        var startInfo = new ProcessStartInfo
        {
            FileName = managedWorker ? ResolveDotNetHost() : workerPath,
            WorkingDirectory = Path.GetDirectoryName(workerPath)
                ?? throw new InvalidOperationException(
                    $"Debugger worker {workerPath} has no containing directory."),
            RedirectStandardError = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        };
        if (managedWorker)
        {
            startInfo.ArgumentList.Add(workerPath);
        }

        startInfo.ArgumentList.Add("control");
        Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The debugger worker did not start.");
        ValueTask<string> diagnostics = new(
            McpDebuggerWorkerDiagnostics.ReadAsync(process.StandardError));
        var client = new DebuggerRpcClient(
            process.StandardInput.BaseStream,
            process.StandardOutput.BaseStream,
            leaveOpen: true);
        try
        {
            await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
            return McpDebuggerSessionLease.Create(
                id,
                kind,
                agentControl,
                process,
                diagnostics,
                client);
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            await StopWorkerAsync(process, diagnostics).ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
        {
            return;
        }

        await _operationGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            Client.ResourceChanged -= OnResourceChanged;
            await Client.DisposeAsync().ConfigureAwait(false);
            await _worker.StandardInput.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _ = _operationGate.Release();
        }

        await StopWorkerAsync(_worker, _diagnostics).ConfigureAwait(false);
        _operationGate.Dispose();
    }

    private static async Task StopWorkerAsync(
        Process process,
        ValueTask<string> diagnostics)
    {
        try
        {
            if (!process.HasExited)
            {
                try
                {
                    await process.WaitForExitAsync(CancellationToken.None)
                        .WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                }
            }

            string diagnosticText = await diagnostics.ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                throw new InvalidDataException(
                    $"Debugger worker exited with code {process.ExitCode}: {diagnosticText}");
            }
        }
        finally
        {
            process.Dispose();
        }
    }

    private static string ResolveDotNetHost()
    {
        string? hostPath = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        return string.IsNullOrWhiteSpace(hostPath) ? "dotnet" : hostPath;
    }
}
