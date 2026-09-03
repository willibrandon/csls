using Csls.Debugger.Contracts;

namespace Csls.Debugger.Control;

/// <summary>
/// Activates managed debugger targets through the private control service.
/// </summary>
public sealed partial class DebuggerControlService
{
    /// <inheritdoc />
    public async Task<DebugSessionSnapshot> LaunchAsync(
        DebugLaunchRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await _session.LaunchManagedAsync(CreateLaunchOptions(request), cancellationToken)
            .ConfigureAwait(false);
        _launchRequest = request;
        _attachRequest = null;
        return GetSnapshot();
    }

    /// <inheritdoc />
    public async Task<DebugSessionSnapshot> AttachAsync(
        DebugAttachRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await _session.AttachManagedAsync(CreateAttachOptions(request), cancellationToken)
            .ConfigureAwait(false);
        _attachRequest = request;
        _launchRequest = null;
        return GetSnapshot();
    }

    /// <inheritdoc />
    public async Task<DebugSessionSnapshot> RestartAsync(
        CancellationToken cancellationToken)
    {
        if (_launchRequest is not null)
        {
            await _session.RestartManagedAsync(
                CreateLaunchOptions(_launchRequest),
                cancellationToken).ConfigureAwait(false);
        }
        else if (_attachRequest is not null)
        {
            await _session.RestartManagedAttachAsync(
                CreateAttachOptions(_attachRequest),
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            throw new InvalidOperationException(
                "A debugger target must be activated before it can be restarted.");
        }

        return GetSnapshot();
    }

    private static DebuggeeLaunchOptions CreateLaunchOptions(DebugLaunchRequest request) => new()
    {
        Program = request.Program,
        WorkingDirectory = request.WorkingDirectory,
        Arguments = request.Arguments,
        Environment = request.Environment,
        RuntimeHostPath = request.RuntimeHostPath,
        SourceFileMap = request.SourceFileMap,
        SourceLinkOptions = request.SourceLinkOptions,
        SymbolOptions = request.SymbolOptions,
        SuppressJitOptimizations = request.SuppressJitOptimizations,
        JustMyCode = request.JustMyCode,
        EnableStepFiltering = request.EnableStepFiltering
    };

    private static DebuggeeAttachOptions CreateAttachOptions(DebugAttachRequest request) => new()
    {
        ProcessId = request.ProcessId,
        SourceFileMap = request.SourceFileMap,
        SourceLinkOptions = request.SourceLinkOptions,
        SymbolOptions = request.SymbolOptions,
        JustMyCode = request.JustMyCode,
        EnableStepFiltering = request.EnableStepFiltering
    };
}
