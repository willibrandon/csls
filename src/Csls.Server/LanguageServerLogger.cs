using Csls.Protocol;
using Microsoft.Extensions.Logging;

namespace Csls.Server;

/// <summary>
/// Writes source-generated language-server log messages.
/// </summary>
internal static partial class LanguageServerLogger
{
    /// <summary>
    /// Reports successful language-server initialization.
    /// </summary>
    /// <param name="logger">The language-server logger.</param>
    /// <param name="workspaceFolderCount">The initialized workspace folder count.</param>
    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Information,
        Message = "Initialized {WorkspaceFolderCount} workspace folders")]
    internal static partial void LogInitialized(
        ILogger logger,
        int workspaceFolderCount);

    /// <summary>
    /// Reports the configuration applied to the running language server.
    /// </summary>
    /// <param name="logger">The language-server logger.</param>
    /// <param name="enableAnalyzers">Whether analyzer diagnostics are enabled.</param>
    /// <param name="formatOnSave">Whether save-time formatting is enabled.</param>
    /// <param name="enableParameterHints">Whether parameter inlay hints are enabled.</param>
    /// <param name="enableTypeHints">Whether type inlay hints are enabled.</param>
    /// <param name="reportInformationAsHint">Whether information diagnostics appear as hints.</param>
    /// <param name="buildConfiguration">The active build configuration.</param>
    /// <param name="logLevel">The active log level.</param>
    /// <param name="changed">Whether the workspace configuration changed.</param>
    [LoggerMessage(
        EventId = 2,
        Level = LogLevel.Information,
        Message = "Applied configuration: analyzer diagnostics enabled={EnableAnalyzers}, format on save={FormatOnSave}, parameter hints enabled={EnableParameterHints}, type hints enabled={EnableTypeHints}, information diagnostics reported as hints={ReportInformationAsHint}, build configuration={BuildConfiguration}, log level={LogLevel}, workspace changed={Changed}")]
    internal static partial void LogConfigurationApplied(
        ILogger logger,
        bool enableAnalyzers,
        bool formatOnSave,
        bool enableParameterHints,
        bool enableTypeHints,
        bool reportInformationAsHint,
        string buildConfiguration,
        LogLevel logLevel,
        bool changed);

    /// <summary>
    /// Reports a failed client configuration request.
    /// </summary>
    /// <param name="logger">The language-server logger.</param>
    /// <param name="exception">The configuration request failure.</param>
    [LoggerMessage(
        EventId = 3,
        Level = LogLevel.Error,
        Message = "Client configuration pull failed")]
    internal static partial void LogConfigurationPullFailed(
        ILogger logger,
        Exception exception);

    /// <summary>
    /// Reports skipped push diagnostics during shutdown.
    /// </summary>
    /// <param name="logger">The language-server logger.</param>
    /// <param name="uri">The skipped document URI.</param>
    [LoggerMessage(
        EventId = 4,
        Level = LogLevel.Debug,
        Message = "Skipped push diagnostics for {Uri} during shutdown")]
    internal static partial void LogPushDiagnosticSkippedDuringShutdown(
        ILogger logger,
        DocumentUri uri);

    /// <summary>
    /// Reports a failed workspace progress transport operation.
    /// </summary>
    /// <param name="logger">The language-server logger.</param>
    /// <param name="exception">The progress transport failure.</param>
    [LoggerMessage(
        EventId = 13,
        Level = LogLevel.Warning,
        Message = "Workspace progress transport failed")]
    internal static partial void LogWorkspaceProgressFailure(
        ILogger logger,
        Exception exception);

    /// <summary>
    /// Reports a workspace load failure while progress is active.
    /// </summary>
    /// <param name="logger">The language-server logger.</param>
    /// <param name="exception">The workspace load failure.</param>
    [LoggerMessage(
        EventId = 14,
        Level = LogLevel.Error,
        Message = "Workspace loading failed while reporting progress")]
    internal static partial void LogWorkspaceLoadFailure(
        ILogger logger,
        Exception exception);

    /// <summary>
    /// Reports a failed dynamic file-watcher registration request.
    /// </summary>
    /// <param name="logger">The language-server logger.</param>
    /// <param name="exception">The registration failure.</param>
    [LoggerMessage(
        EventId = 16,
        Level = LogLevel.Warning,
        Message = "Dynamic workspace file watcher registration failed")]
    internal static partial void LogFileWatcherRegistrationFailure(
        ILogger logger,
        Exception exception);

    /// <summary>
    /// Reports the total processing time and outcome for one watched-file batch.
    /// </summary>
    /// <param name="logger">The language-server logger.</param>
    /// <param name="elapsedMilliseconds">The elapsed wall-clock milliseconds.</param>
    /// <param name="updateMode">The applied workspace update mode.</param>
    /// <param name="paths">The bounded changed-path list.</param>
    [LoggerMessage(
        EventId = 17,
        Level = LogLevel.Information,
        SkipEnabledCheck = true,
        Message = "Watched file changes completed in {ElapsedMilliseconds} ms using {UpdateMode}: {Paths}")]
    internal static partial void LogWatchedFileChangesCompleted(
        ILogger logger,
        long elapsedMilliseconds,
        string updateMode,
        string paths);
}
