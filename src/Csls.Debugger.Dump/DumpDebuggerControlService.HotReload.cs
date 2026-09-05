using Csls.Debugger.Contracts;

namespace Csls.Debugger.Dump;

/// <summary>
/// Rejects Hot Reload for immutable process-dump sessions.
/// </summary>
public sealed partial class DumpDebuggerControlService
{
    /// <inheritdoc />
    public Task<DebugHotReloadResult> ApplyHotReloadAsync(
        DebugHotReloadRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromException<DebugHotReloadResult>(
            CreateReadOnlyException("Hot Reload"));
    }
}
