using Microsoft.Build.Framework;
using Microsoft.CodeAnalysis;
using System.Collections.Concurrent;

namespace Csls.Workspaces;

/// <summary>
/// Forwards concurrent design-time build diagnostics to the workspace diagnostic stream.
/// </summary>
internal sealed class MSBuildWorkspaceBuildLogger : ILogger
{
    private readonly ConcurrentDictionary<int, Action<WorkspaceDiagnosticKind, string>>
        _diagnosticDestinations = new();

    /// <summary>
    /// Gets or sets the build logger verbosity.
    /// </summary>
    public LoggerVerbosity Verbosity { get; set; } = LoggerVerbosity.Minimal;

    /// <summary>
    /// Gets or sets the optional build logger parameters.
    /// </summary>
    public string? Parameters { get; set; }

    /// <summary>
    /// Registers one active workspace diagnostic destination.
    /// </summary>
    /// <param name="submissionId">The unique MSBuild submission identifier.</param>
    /// <param name="reportDiagnostic">The workspace diagnostic destination.</param>
    internal void Register(
        int submissionId,
        Action<WorkspaceDiagnosticKind, string> reportDiagnostic)
    {
        ArgumentNullException.ThrowIfNull(reportDiagnostic);
        if (!_diagnosticDestinations.TryAdd(submissionId, reportDiagnostic))
        {
            throw new InvalidOperationException(
                $"MSBuild logger registration {submissionId} already exists.");
        }
    }

    /// <summary>
    /// Removes one completed workspace diagnostic destination.
    /// </summary>
    /// <param name="submissionId">The unique MSBuild submission identifier.</param>
    internal void Unregister(int submissionId) =>
        _diagnosticDestinations.TryRemove(submissionId, out _);

    /// <summary>
    /// Subscribes to MSBuild warning and error events.
    /// </summary>
    /// <param name="eventSource">The MSBuild event source.</param>
    public void Initialize(IEventSource eventSource)
    {
        ArgumentNullException.ThrowIfNull(eventSource);
        eventSource.WarningRaised += OnWarningRaised;
        eventSource.ErrorRaised += OnErrorRaised;
    }

    /// <summary>
    /// Completes logging after the active build ends.
    /// </summary>
    public void Shutdown()
    {
    }

    private void OnWarningRaised(object sender, BuildWarningEventArgs eventArgs) =>
        Report(
            eventArgs,
            WorkspaceDiagnosticKind.Warning,
            FormatDiagnostic(eventArgs.Code, eventArgs.File, eventArgs.LineNumber, eventArgs.Message));

    private void OnErrorRaised(object sender, BuildErrorEventArgs eventArgs) =>
        Report(
            eventArgs,
            WorkspaceDiagnosticKind.Failure,
            FormatDiagnostic(eventArgs.Code, eventArgs.File, eventArgs.LineNumber, eventArgs.Message));

    private void Report(BuildEventArgs eventArgs, WorkspaceDiagnosticKind kind, string message)
    {
        if (eventArgs.BuildEventContext?.SubmissionId is int submissionId &&
            _diagnosticDestinations.TryGetValue(
                submissionId,
                out Action<WorkspaceDiagnosticKind, string>? destination))
        {
            destination(kind, message);
        }
    }

    private static string FormatDiagnostic(
        string? code,
        string? file,
        int lineNumber,
        string? message)
    {
        string location = string.IsNullOrWhiteSpace(file)
            ? string.Empty
            : lineNumber > 0
                ? $"{file}({lineNumber}): "
                : $"{file}: ";
        string identifier = string.IsNullOrWhiteSpace(code) ? string.Empty : $"{code}: ";
        return $"{location}{identifier}{message ?? "Unknown MSBuild diagnostic."}";
    }
}
