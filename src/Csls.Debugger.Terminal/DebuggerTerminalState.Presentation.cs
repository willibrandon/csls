using Csls.Debugger.Contracts;
using System.Globalization;

namespace Csls.Debugger.Terminal;

/// <summary>
/// Publishes complete immutable view data independently of deferred widget rendering.
/// </summary>
internal sealed partial class DebuggerTerminalState
{
    private DebuggerTerminalViewSnapshot _viewSnapshot = new();

    /// <summary>
    /// Acknowledges pending refresh work and captures one published frame for every widget callback.
    /// </summary>
    /// <returns>The immutable view data used throughout one widget-tree construction.</returns>
    internal DebuggerTerminalViewSnapshot CaptureViewSnapshot()
    {
        AcknowledgeViewRefresh();
        return Volatile.Read(ref _viewSnapshot);
    }

    /// <summary>
    /// Copies a completed mutation into one immutable frame and requests its presentation.
    /// </summary>
    /// <remarks>The caller must hold the state mutation gate.</remarks>
    private void PublishViewSnapshot()
    {
        DebugSessionSnapshot session = Snapshot;
        string? status = StatusMessage;
        string moduleSummary = ModuleSummary;
        string? exceptionSummary = ExceptionSummary;
        var snapshot = new DebuggerTerminalViewSnapshot
        {
            Header = BuildViewHeader(session, status, moduleSummary, exceptionSummary),
            SourceRevision = _sourceRevision,
            SourceLines = [.. SourceLines],
            SourceFocusedIndex = SourceFocusedIndex,
            ThreadRevision = _threadRevision,
            ThreadLines = [.. ThreadLines],
            SelectedThreadIndex = SelectedThreadIndex,
            StackRevision = _stackRevision,
            StackLines = [.. StackLines],
            SelectedStackFrameIndex = SelectedStackFrameIndex,
            VariableLines = [.. VariableLines],
            AuxiliaryTitle = AuxiliaryTitle,
            AuxiliaryLines = [.. AuxiliaryLines]
        };
        Volatile.Write(ref _viewSnapshot, snapshot);
        RequestViewRefresh();
    }

    private void PublishViewError(string message)
    {
        StatusMessage = message;
        ClearInspection("Debugger inspection is unavailable.");
        PublishViewSnapshot();
    }

    private static string BuildViewHeader(
        DebugSessionSnapshot session,
        string? status,
        string moduleSummary,
        string? exceptionSummary) =>
        $"csls debugger  {session.ProcessName ?? "managed target"}  " +
        $"pid {session.ProcessId?.ToString(CultureInfo.InvariantCulture) ?? "-"}  " +
        $"{session.State}  {session.StopReason ?? string.Empty}  " +
        moduleSummary +
        (exceptionSummary is null ? string.Empty : $"  {exceptionSummary}") +
        (status is null ? string.Empty : $"  {status}");
}
