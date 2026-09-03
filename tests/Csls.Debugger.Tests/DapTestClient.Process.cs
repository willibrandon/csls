using System.Diagnostics;
using System.Runtime.CompilerServices;

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
        await CaptureDiagnosticsAsync(process.StandardError).ConfigureAwait(false);
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
            await CaptureDiagnosticsAsync(process.StandardError).ConfigureAwait(false);
            process.Dispose();
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
        startInfo.Environment["CSLS_DEBUGGER_WORKER_PATH"] = Path.Join(
            repositoryRoot,
            "artifacts",
            "bin",
            "Csls.Debugger.Worker",
            "debug",
            "csls-debugger-worker.dll");
        _process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The csls debugger command did not start.");
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
