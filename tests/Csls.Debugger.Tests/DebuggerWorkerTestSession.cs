using Csls.Debugger.Control;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Csls.Debugger.Tests;

/// <summary>
/// Owns a production debugger worker connected through real operating-system pipes.
/// </summary>
internal sealed class DebuggerWorkerTestSession : IAsyncDisposable
{
    private readonly ValueTask<string> _diagnostics;
    private readonly Process _process;
    private int _disposed;

    private DebuggerWorkerTestSession(
        Process process,
        ValueTask<string> diagnostics,
        DebuggerRpcClient client)
    {
        _process = process;
        _diagnostics = diagnostics;
        Client = client;
    }

    /// <summary>
    /// Gets the connected production debugger-control client.
    /// </summary>
    internal DebuggerRpcClient Client { get; }

    /// <summary>
    /// Starts and connects a packaged debugger worker from the current repository build.
    /// </summary>
    /// <param name="cancellationToken">Cancels worker connection establishment.</param>
    /// <returns>The owning worker session.</returns>
    internal static async Task<DebuggerWorkerTestSession> StartAsync(
        CancellationToken cancellationToken)
    {
        string repositoryRoot = FindRepositoryRoot();
        string workerPath = Path.Join(
            repositoryRoot,
            "artifacts",
            "bin",
            "Csls.Debugger.Worker",
            "debug",
            "csls-debugger-worker.dll");
        var startInfo = new ProcessStartInfo
        {
            FileName = ResolveDotNetHost(),
            WorkingDirectory = repositoryRoot,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add(workerPath);
        startInfo.ArgumentList.Add("control");
        Csls.Debugger.DebuggerWorkerEnvironment.Configure(startInfo, workerPath);
        Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The debugger worker did not start.");
        ValueTask<string> diagnostics = new(
            process.StandardError.ReadToEndAsync(CancellationToken.None));
        var client = new DebuggerRpcClient(
            process.StandardInput.BaseStream,
            process.StandardOutput.BaseStream,
            leaveOpen: true);
        try
        {
            await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
            return new DebuggerWorkerTestSession(process, diagnostics, client);
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            process.Dispose();
            _ = await diagnostics.ConfigureAwait(false);
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

        await Client.DisposeAsync().ConfigureAwait(false);
        await _process.StandardInput.DisposeAsync().ConfigureAwait(false);
        if (!_process.HasExited)
        {
            try
            {
                await _process.WaitForExitAsync(CancellationToken.None)
                    .WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }

        string diagnostics = await _diagnostics.ConfigureAwait(false);
        int exitCode = _process.ExitCode;
        _process.Dispose();
        if (exitCode != 0)
        {
            throw new InvalidDataException(
                $"Debugger worker exited with code {exitCode}: {diagnostics}");
        }
    }

    private static string ResolveDotNetHost()
    {
        string? configured = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        return string.IsNullOrWhiteSpace(configured) ? "dotnet" : configured;
    }

    private static string FindRepositoryRoot([CallerFilePath] string sourcePath = "")
    {
        DirectoryInfo? directory = new FileInfo(sourcePath).Directory;
        while (directory is not null && !File.Exists(Path.Join(directory.FullName, "Csls.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate the csls repository root.");
    }
}
