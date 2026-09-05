using Csls.Debugger.Contracts;

namespace Csls.Debugger;

/// <summary>
/// Exposes generation-independent source and symbol inspection operations.
/// </summary>
public sealed partial class DebuggerSession
{
    /// <summary>
    /// Gets source documents represented by symbols for the loaded managed modules.
    /// </summary>
    /// <param name="cancellationToken">Cancels queueing source enumeration.</param>
    /// <returns>The distinct normalized source document snapshot.</returns>
    public async Task<IReadOnlyList<DebugSourceInfo>> GetLoadedSourcesAsync(
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        IReadOnlyList<DebugSourceInfo>? result = null;
        await _actor.InvokeAsync(
            token =>
            {
                _ = token;
                EnsureSymbolsAvailable();
                result = _sourceBreakpoints.GetLoadedSources();
                return ValueTask.CompletedTask;
            },
            cancellationToken).ConfigureAwait(false);
        return result!;
    }

    /// <summary>
    /// Gets executable breakpoint locations in one inclusive source range.
    /// </summary>
    /// <param name="sourcePath">The absolute source document path.</param>
    /// <param name="startLine">The one-based inclusive start line.</param>
    /// <param name="startColumn">The one-based inclusive start column.</param>
    /// <param name="endLine">The one-based inclusive end line.</param>
    /// <param name="endColumn">The one-based inclusive end column.</param>
    /// <param name="cancellationToken">Cancels queueing location enumeration.</param>
    /// <returns>The distinct ordered executable locations.</returns>
    public async Task<IReadOnlyList<DebugBreakpointLocation>> GetBreakpointLocationsAsync(
        string sourcePath,
        int startLine,
        int startColumn,
        int endLine,
        int endColumn,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        IReadOnlyList<DebugBreakpointLocation>? result = null;
        await _actor.InvokeAsync(
            token =>
            {
                _ = token;
                EnsureSymbolsAvailable();
                result = _sourceBreakpoints.GetBreakpointLocations(
                    sourcePath,
                    startLine,
                    startColumn,
                    endLine,
                    endColumn);
                return ValueTask.CompletedTask;
            },
            cancellationToken).ConfigureAwait(false);
        return result!;
    }

    private void EnsureSymbolsAvailable()
    {
        if (_state is not DebugSessionState.Running and not DebugSessionState.Stopped)
        {
            throw new InvalidOperationException(
                $"Managed symbols are unavailable while the debugger session is {_state}.");
        }
    }
}
