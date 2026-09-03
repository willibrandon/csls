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
        await _session.LaunchManagedAsync(
            new DebuggeeLaunchOptions
            {
                Program = request.Program,
                WorkingDirectory = request.WorkingDirectory,
                Arguments = request.Arguments,
                Environment = request.Environment,
                RuntimeHostPath = request.RuntimeHostPath,
                SourceFileMap = request.SourceFileMap,
                SourceLinkOptions = request.SourceLinkOptions,
                SuppressJitOptimizations = request.SuppressJitOptimizations,
                JustMyCode = request.JustMyCode,
                EnableStepFiltering = request.EnableStepFiltering
            },
            cancellationToken).ConfigureAwait(false);
        return GetSnapshot();
    }

    /// <inheritdoc />
    public async Task<DebugSessionSnapshot> AttachAsync(
        DebugAttachRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await _session.ConfigureRuntimeOptionsAsync(
            request.JustMyCode,
            request.EnableStepFiltering,
            cancellationToken).ConfigureAwait(false);
        await _session.ConfigureSourceOptionsAsync(
            request.SourceFileMap,
            request.SourceLinkOptions,
            cancellationToken).ConfigureAwait(false);
        await _session.AttachManagedAsync(request.ProcessId, cancellationToken)
            .ConfigureAwait(false);
        return GetSnapshot();
    }
}
