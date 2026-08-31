using StreamJsonRpc;
using System.Collections.Concurrent;

namespace Csls.Workspaces;

/// <summary>
/// Executes isolated design-time builds requested through the worker standard streams.
/// </summary>
internal sealed class MSBuildBuildHostRpcTarget
{
    private int _requestState;

    /// <summary>
    /// Loads every requested project through one in-process MSBuild session.
    /// </summary>
    /// <param name="request">The projects and global properties to evaluate.</param>
    /// <param name="cancellationToken">The RPC cancellation token.</param>
    /// <returns>The completed project states and diagnostics.</returns>
    [JsonRpcMethod("msbuild/load", UseSingleObjectParameterDeserialization = true)]
    public async Task<MSBuildBuildHostResponse> LoadAsync(
        MSBuildBuildHostRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Interlocked.Exchange(ref _requestState, 1) != 0)
        {
            throw new InvalidOperationException(
                "The MSBuild build host accepts one project load request.");
        }

        if (request.ProjectPaths.Length == 0)
        {
            return new MSBuildBuildHostResponse([], []);
        }

        _ = MSBuildRegistration.EnsureRegistered(request.ProjectPaths[0]);
        var diagnostics = new ConcurrentQueue<MSBuildBuildHostDiagnostic>();
        var buildManager = new MSBuildProjectBuildManager(
            request.GlobalProperties,
            (kind, message) => diagnostics.Enqueue(
                new MSBuildBuildHostDiagnostic(kind, message)));
        IReadOnlyList<MSBuildProjectSnapshot> snapshots = await buildManager
            .LoadAsync(request.ProjectPaths, cancellationToken)
            .ConfigureAwait(false);
        return new MSBuildBuildHostResponse([.. snapshots], [.. diagnostics]);
    }
}
