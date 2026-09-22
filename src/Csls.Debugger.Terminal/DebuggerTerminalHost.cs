using Hex1b;
using System.Runtime.CompilerServices;

namespace Csls.Debugger.Terminal;

/// <summary>
/// Runs interactive Hex1b clients against private debugger control sessions.
/// </summary>
public static class DebuggerTerminalHost
{
    /// <summary>
    /// Launches a managed target and opens the debugger at its entry or initial source breakpoint.
    /// </summary>
    /// <param name="options">The validated launch and breakpoint options.</param>
    /// <param name="cancellationToken">The interactive session cancellation token.</param>
    /// <returns>The debugger process exit code.</returns>
    public static async Task<int> RunLaunchAsync(
        DebuggerTerminalLaunchOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ValidateLaunch(options);
        DebuggerTerminalOwnedSession session = await DebuggerTerminalOwnedSession
            .LaunchAsync(options, cancellationToken).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable cleanup = session.ConfigureAwait(false);
        return await RunTerminalAsync(session.State, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Attaches to and pauses a running managed target before opening the debugger.
    /// </summary>
    /// <param name="options">The validated target process selection.</param>
    /// <param name="cancellationToken">The interactive session cancellation token.</param>
    /// <returns>The debugger process exit code.</returns>
    public static async Task<int> RunAttachAsync(
        DebuggerTerminalAttachOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.ProcessId);
        DebuggerTerminalOwnedSession session = await DebuggerTerminalOwnedSession
            .AttachAsync(options, cancellationToken).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable cleanup = session.ConfigureAwait(false);
        return await RunTerminalAsync(session.State, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<int> RunTerminalAsync(
        DebuggerTerminalState state,
        CancellationToken cancellationToken)
    {
        Hex1bTerminal terminal = Hex1bTerminal.CreateBuilder()
            .WithHex1bApp(
                state.AttachWorkload,
                context => DebuggerTerminalView.Build(context, state))
            .WithMouse()
            .Build();
        await using (terminal.ConfigureAwait(false))
        {
            await terminal.RunAsync(cancellationToken).ConfigureAwait(false);
        }

        return 0;
    }

    private static void ValidateLaunch(DebuggerTerminalLaunchOptions options)
    {
        if (!Path.IsPathFullyQualified(options.Program) || !File.Exists(options.Program))
        {
            throw new FileNotFoundException("The managed program does not exist.", options.Program);
        }

        if (!Path.IsPathFullyQualified(options.WorkingDirectory) ||
            !Directory.Exists(options.WorkingDirectory))
        {
            throw new DirectoryNotFoundException(
                $"The target working directory does not exist: {options.WorkingDirectory}");
        }

        if ((options.SourcePath is null) != (options.Line is null))
        {
            throw new ArgumentException("An initial source breakpoint requires both a source path and line.", nameof(options));
        }

        if (options.SourcePath is null && !options.StopAtEntry)
        {
            throw new ArgumentException("Choose an entry stop or an initial source breakpoint.", nameof(options));
        }

        if (options.SourcePath is string sourcePath &&
            (!Path.IsPathFullyQualified(sourcePath) || !File.Exists(sourcePath)))
        {
            throw new FileNotFoundException(
                "The initial breakpoint source does not exist.",
                options.SourcePath);
        }

        if (options.Line is int line)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(line);
        }
    }
}
