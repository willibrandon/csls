using Microsoft.Extensions.Logging;

namespace Csls.Workspaces;

/// <summary>
/// Writes source-generated workspace manager log messages.
/// </summary>
internal static partial class WorkspaceManagerLogger
{
    /// <summary>
    /// Reports a transaction artifact that could not be removed.
    /// </summary>
    /// <param name="logger">The workspace logger.</param>
    /// <param name="path">The artifact path.</param>
    /// <param name="exception">The cleanup failure.</param>
    [LoggerMessage(
        EventId = 3,
        Level = LogLevel.Warning,
        Message = "Could not remove edit transaction artifact {Path}")]
    internal static partial void LogEditArtifactCleanupFailure(
        ILogger logger,
        string path,
        Exception exception);
}
