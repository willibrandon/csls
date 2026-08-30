using Microsoft.Extensions.Logging;

namespace Csls.Workspaces;

/// <summary>
/// Writes source-generated workspace manager log messages.
/// </summary>
internal static partial class WorkspaceManagerLogger
{
    /// <summary>
    /// Reports the start of initial workspace loading.
    /// </summary>
    /// <param name="logger">The workspace logger.</param>
    [LoggerMessage(
        EventId = 4,
        Level = LogLevel.Information,
        Message = "Loading C# workspace")]
    internal static partial void LogWorkspaceLoadStarted(ILogger logger);

    /// <summary>
    /// Reports completion of initial workspace loading.
    /// </summary>
    /// <param name="logger">The workspace logger.</param>
    /// <param name="elapsedMilliseconds">The elapsed wall-clock milliseconds.</param>
    /// <param name="projectCount">The loaded project count.</param>
    [LoggerMessage(
        EventId = 5,
        Level = LogLevel.Information,
        Message = "Completed initial C# workspace load in {ElapsedMilliseconds} ms with {ProjectCount} projects")]
    internal static partial void LogWorkspaceLoadCompleted(
        ILogger logger,
        long elapsedMilliseconds,
        int projectCount);

    /// <summary>
    /// Reports the start of a full workspace reload.
    /// </summary>
    /// <param name="logger">The workspace logger.</param>
    [LoggerMessage(
        EventId = 6,
        Level = LogLevel.Information,
        Message = "Reloading C# workspace")]
    internal static partial void LogWorkspaceReloadStarted(ILogger logger);

    /// <summary>
    /// Reports completion of a full workspace reload.
    /// </summary>
    /// <param name="logger">The workspace logger.</param>
    /// <param name="elapsedMilliseconds">The elapsed wall-clock milliseconds.</param>
    /// <param name="projectCount">The loaded project count.</param>
    [LoggerMessage(
        EventId = 7,
        Level = LogLevel.Information,
        Message = "Completed C# workspace reload in {ElapsedMilliseconds} ms with {ProjectCount} projects")]
    internal static partial void LogWorkspaceReloadCompleted(
        ILogger logger,
        long elapsedMilliseconds,
        int projectCount);

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
