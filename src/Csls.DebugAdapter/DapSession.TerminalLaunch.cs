using Csls.Debugger;
using Csls.Debugger.Control;
using System.Diagnostics;

namespace Csls.DebugAdapter;

/// <summary>
/// Starts client-terminal targets through the authenticated worker launcher.
/// </summary>
internal sealed partial class DapSession
{
    private DebuggerTerminalLaunchServer? _terminalLaunchServer;

    private async Task LaunchManagedInTerminalAsync(
        DapLaunchConfiguration launch,
        CancellationToken cancellationToken)
    {
        int processId = await StartTerminalTargetAsync(launch, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            await _engineSession.LaunchManagedInTerminalAsync(
                launch.Options,
                processId,
                TerminalLaunchServer.ReadExitCodeAsync,
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await DisposeTerminalLaunchServerAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task LaunchWithoutDebuggingInTerminalAsync(
        DapLaunchConfiguration launch,
        CancellationToken cancellationToken)
    {
        int processId = await StartTerminalTargetAsync(launch, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            await _engineSession.LaunchWithoutDebuggingInTerminalAsync(
                launch.Options,
                processId,
                TerminalLaunchServer.ReadExitCodeAsync,
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await DisposeTerminalLaunchServerAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task<int> StartTerminalTargetAsync(
        DapLaunchConfiguration launch,
        CancellationToken cancellationToken)
    {
        if (!_clientSupportsRunInTerminal)
        {
            throw new ArgumentException(
                "The DAP client does not support the requested terminal console.");
        }

        await DisposeTerminalLaunchServerAsync().ConfigureAwait(false);

        string workerPath = DebuggerTerminalWorkerLocator.ResolveCurrent();
        IReadOnlyList<string> arguments = string.Equals(
            Path.GetExtension(workerPath), ".dll", StringComparison.OrdinalIgnoreCase)
            ? ["dotnet", workerPath, "terminal-launch"]
            : [workerPath, "terminal-launch"];
        Dictionary<string, string> targetEnvironment = await DebuggeeLaunchEnvironment
            .CreateAsync(launch.Options, cancellationToken).ConfigureAwait(false);
        var instruction = new DebuggerTerminalLaunchInstruction
        {
            Program = launch.Options.Program,
            WorkingDirectory = launch.Options.WorkingDirectory,
            Arguments = launch.Options.Arguments,
            Environment = targetEnvironment,
            RuntimeHostPath = launch.Options.RuntimeHostPath,
            SuspendForDebugging = !launch.NoDebug
        };
        _terminalLaunchServer = new DebuggerTerminalLaunchServer();
        using var handshakeCancellation = CancellationTokenSource
            .CreateLinkedTokenSource(cancellationToken);
        handshakeCancellation.CancelAfter(TimeSpan.FromSeconds(30));
        Task<int> accepted = TerminalLaunchServer.AcceptAsync(instruction, handshakeCancellation.Token);
        bool started = false;
        try
        {
            Dictionary<string, string> launcherEnvironment = new(StringComparer.Ordinal)
            {
                [DebuggerTerminalLauncher.PipeEnvironmentVariable] = TerminalLaunchServer.PipeName,
                [DebuggerTerminalLauncher.SecretEnvironmentVariable] = TerminalLaunchServer.LaunchSecret
            };
            _ = await RunInTerminalAsync(
                launch.Console == DapConsoleKind.Integrated ? "integrated" : "external",
                launch.Options.WorkingDirectory,
                Path.GetFileNameWithoutExtension(launch.Options.Program),
                arguments,
                launcherEnvironment,
                cancellationToken).ConfigureAwait(false);
            int processId;
            try
            {
                processId = await accepted.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (
                handshakeCancellation.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException("The terminal launcher did not report its target in time.");
            }
            started = true;
            return processId;
        }
        finally
        {
            if (!started)
            {
                await handshakeCancellation.CancelAsync().ConfigureAwait(false);
                try
                {
                    _ = await accepted.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (handshakeCancellation.IsCancellationRequested)
                {
                    Debug.Assert(accepted.IsCanceled);
                }
                finally
                {
                    await DisposeTerminalLaunchServerAsync().ConfigureAwait(false);
                }
            }
        }
    }

    private async Task DisposeTerminalLaunchServerAsync()
    {
        if (_terminalLaunchServer is not null)
        {
            await _terminalLaunchServer.DisposeAsync().ConfigureAwait(false);
            _terminalLaunchServer = null;
        }
    }

    private DebuggerTerminalLaunchServer TerminalLaunchServer => _terminalLaunchServer
        ?? throw new InvalidOperationException("The terminal launch endpoint is unavailable.");
}
