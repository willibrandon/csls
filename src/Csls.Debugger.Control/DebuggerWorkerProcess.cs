using System.Diagnostics;

namespace Csls.Debugger.Control;

/// <summary>
/// Owns a debugger worker and its private inherited-stream RPC connection.
/// </summary>
public sealed class DebuggerWorkerProcess : IAsyncDisposable
{
    private readonly Process _process;
    private readonly ValueTask<string> _diagnostics;
    private int _disposed;

    private DebuggerWorkerProcess(Process process, ValueTask<string> diagnostics, DebuggerRpcClient client)
    {
        _process = process;
        _diagnostics = diagnostics;
        Client = client;
    }

    /// <summary>
    /// Gets the private RPC client connected to this worker.
    /// </summary>
    public DebuggerRpcClient Client { get; }

    /// <summary>
    /// Gets the operating-system identifier of the owned worker process.
    /// </summary>
    public int ProcessId => _process.Id;

    /// <summary>
    /// Starts an explicitly selected worker and verifies its private control protocol.
    /// </summary>
    /// <param name="workerPath">The absolute existing worker executable or assembly path.</param>
    /// <param name="configureNativeEnvironment">Whether the worker hosts the live CoreCLR debugger.</param>
    /// <param name="cancellationToken">Cancels worker connection establishment.</param>
    /// <returns>The connected worker owner.</returns>
    public static async Task<DebuggerWorkerProcess> StartAsync(
        string workerPath,
        bool configureNativeEnvironment,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workerPath);
        if (!Path.IsPathFullyQualified(workerPath))
        {
            throw new ArgumentException("The debugger worker path must be absolute.", nameof(workerPath));
        }

        if (!File.Exists(workerPath))
        {
            throw new FileNotFoundException("The debugger worker does not exist.", workerPath);
        }

        cancellationToken.ThrowIfCancellationRequested();
        ProcessStartInfo startInfo = CreateStartInfo(workerPath, configureNativeEnvironment);
        Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The debugger worker did not start.");
        ValueTask<string> diagnostics = new(DebuggerWorkerDiagnostics.ReadAsync(process.StandardError));
        var client = new DebuggerRpcClient(
            process.StandardInput.BaseStream,
            process.StandardOutput.BaseStream,
            leaveOpen: true);
        try
        {
            await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
            return new DebuggerWorkerProcess(process, diagnostics, client);
        }
        catch
        {
            await ReleaseWorkerAsync(client, process, diagnostics).ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await ReleaseWorkerAsync(Client, _process, _diagnostics).ConfigureAwait(false);
    }

    private static ProcessStartInfo CreateStartInfo(string workerPath, bool configureNativeEnvironment)
    {
        bool managedWorker = string.Equals(Path.GetExtension(workerPath), ".dll", StringComparison.OrdinalIgnoreCase);
        var startInfo = new ProcessStartInfo
        {
            FileName = managedWorker ? ResolveDotNetHost() : workerPath,
            WorkingDirectory = Path.GetDirectoryName(workerPath)
                ?? throw new ArgumentException("The debugger worker has no containing directory.", nameof(workerPath)),
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
        if (configureNativeEnvironment)
        {
            DebuggerWorkerEnvironment.Configure(startInfo, workerPath);
        }

        return startInfo;
    }

    private static async Task ReleaseWorkerAsync(
        DebuggerRpcClient client,
        Process process,
        ValueTask<string> diagnostics)
    {
        try
        {
            await client.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            try
            {
                await process.StandardInput.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                await StopWorkerAsync(process, diagnostics).ConfigureAwait(false);
            }
        }
    }

    private static async Task StopWorkerAsync(Process process, ValueTask<string> diagnostics)
    {
        using (process)
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
    }

    private static string ResolveDotNetHost()
    {
        string? hostPath = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        return string.IsNullOrWhiteSpace(hostPath) ? "dotnet" : hostPath;
    }
}
