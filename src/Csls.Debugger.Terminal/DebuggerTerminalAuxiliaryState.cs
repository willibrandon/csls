using Csls.Debugger.Contracts;
using Csls.Debugger.Control;
using System.Globalization;

namespace Csls.Debugger.Terminal;

/// <summary>
/// Owns bounded output, module, breakpoint, and exception terminal projections.
/// </summary>
internal sealed class DebuggerTerminalAuxiliaryState
{
    private const int MaximumItems = 200;
    private readonly DebuggerRpcClient _client;
    private IReadOnlyList<string> _breakpointLines = ["No breakpoints configured."];
    private IReadOnlyList<string> _exceptionLines = ["No current managed exception."];
    private IReadOnlyList<string> _moduleLines = ["No managed modules loaded."];
    private IReadOnlyList<string> _outputLines = ["No target output."];

    /// <summary>
    /// Creates auxiliary state backed by the private debugger RPC client.
    /// </summary>
    /// <param name="client">The connected debugger RPC client.</param>
    internal DebuggerTerminalAuxiliaryState(DebuggerRpcClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
    }

    /// <summary>
    /// Gets the currently selected auxiliary debugger pane.
    /// </summary>
    internal DebuggerTerminalAuxiliaryPane Pane { get; private set; }

    /// <summary>
    /// Gets the authoritative breakpoint state used by source rendering.
    /// </summary>
    internal DebugBreakpointSnapshot Breakpoints { get; private set; } =
        new([], [], [], []);

    /// <summary>
    /// Gets the managed-module and symbol summary shown in the header.
    /// </summary>
    internal string ModuleSummary { get; private set; } = "0 modules";

    /// <summary>
    /// Gets the current managed-exception summary shown in the header.
    /// </summary>
    internal string? ExceptionSummary { get; private set; }

    /// <summary>
    /// Gets the title for the selected auxiliary debugger pane.
    /// </summary>
    internal string Title => Pane switch
    {
        DebuggerTerminalAuxiliaryPane.Output => "Target Output",
        DebuggerTerminalAuxiliaryPane.Modules => "Modules",
        DebuggerTerminalAuxiliaryPane.Breakpoints => "Breakpoints",
        DebuggerTerminalAuxiliaryPane.Exception => "Exception",
        _ => throw new InvalidOperationException($"Unknown auxiliary pane {Pane}.")
    };

    /// <summary>
    /// Gets the bounded rows for the selected auxiliary debugger pane.
    /// </summary>
    internal IReadOnlyList<string> Lines => Pane switch
    {
        DebuggerTerminalAuxiliaryPane.Output => _outputLines,
        DebuggerTerminalAuxiliaryPane.Modules => _moduleLines,
        DebuggerTerminalAuxiliaryPane.Breakpoints => _breakpointLines,
        DebuggerTerminalAuxiliaryPane.Exception => _exceptionLines,
        _ => throw new InvalidOperationException($"Unknown auxiliary pane {Pane}.")
    };

    /// <summary>
    /// Loads every bounded auxiliary projection for one stopped generation.
    /// </summary>
    /// <param name="snapshot">The exact stopped debugger-session snapshot.</param>
    /// <param name="cancellationToken">Cancels private RPC inspection.</param>
    internal async Task LoadAsync(
        DebugSessionSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        DebugOutputPage output = await _client.GetOutputAsync(
            new DebugOutputRequest(0, MaximumItems),
            cancellationToken).ConfigureAwait(false);
        DebugModulePage modules = await _client.GetModulesAsync(
            new DebugModulesRequest(0, MaximumItems),
            cancellationToken).ConfigureAwait(false);
        await RefreshBreakpointsAsync(cancellationToken).ConfigureAwait(false);

        List<string> outputLines =
        [
            .. output.Entries.SelectMany(static entry => entry.Output
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n')
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => $"{FormatOutputCategory(entry.Category)} {line}"))
        ];
        if (output.DroppedBeforeStart > 0)
        {
            outputLines.Insert(
                0,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"… {output.DroppedBeforeStart} older output segments were dropped"));
        }

        if (output.HasMore)
        {
            outputLines.Add("… more retained output is available");
        }

        _outputLines = outputLines.Count == 0 ? ["No target output."] : outputLines;

        List<string> moduleLines = [.. modules.Modules.Select(FormatModule)];
        if (modules.TotalModules > modules.Modules.Count)
        {
            moduleLines.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"… {modules.TotalModules - modules.Modules.Count} more modules"));
        }

        _moduleLines = moduleLines.Count == 0 ? ["No managed modules loaded."] : moduleLines;
        int symbolCount = modules.Modules.Count(static module =>
            module.SymbolKind != DebugModuleSymbolKind.None);
        ModuleSummary = string.Create(
            CultureInfo.InvariantCulture,
            $"{modules.TotalModules} modules, {symbolCount} with symbols");

        ExceptionSummary = null;
        _exceptionLines = ["No current managed exception."];
        if (string.Equals(
                snapshot.StopReason,
                "exception",
                StringComparison.OrdinalIgnoreCase) &&
            snapshot.StoppedThreadId is int threadId)
        {
            DebugExceptionInfo exception = await _client.GetExceptionInfoAsync(
                new DebugExceptionInfoRequest(threadId),
                cancellationToken).ConfigureAwait(false);
            ExceptionSummary = $"{exception.ExceptionId}: {exception.Description}";
            _exceptionLines =
            [
                $"Type: {exception.ExceptionId}",
                $"Stage: {exception.BreakMode}",
                $"Description: {exception.Description}"
            ];
        }
    }

    /// <summary>
    /// Reloads authoritative breakpoint state after an interactive mutation.
    /// </summary>
    /// <param name="cancellationToken">Cancels private RPC inspection.</param>
    internal async Task RefreshBreakpointsAsync(CancellationToken cancellationToken)
    {
        Breakpoints = await _client.GetBreakpointsAsync(cancellationToken)
            .ConfigureAwait(false);
        List<string> lines =
        [
            .. Breakpoints.SourceBreakpoints.Select(FormatSourceBreakpoint),
            .. Breakpoints.FunctionBreakpoints.Select(FormatFunctionBreakpoint),
            .. Breakpoints.InstructionBreakpoints.Select(FormatInstructionBreakpoint),
            .. Breakpoints.ExceptionBreakpoints.Select(FormatExceptionBreakpoint)
        ];
        _breakpointLines = lines.Count == 0 ? ["No breakpoints configured."] : lines;
    }

    /// <summary>
    /// Advances to the next auxiliary debugger pane.
    /// </summary>
    internal void Cycle()
    {
        Pane = Pane switch
        {
            DebuggerTerminalAuxiliaryPane.Output => DebuggerTerminalAuxiliaryPane.Modules,
            DebuggerTerminalAuxiliaryPane.Modules => DebuggerTerminalAuxiliaryPane.Breakpoints,
            DebuggerTerminalAuxiliaryPane.Breakpoints => DebuggerTerminalAuxiliaryPane.Exception,
            DebuggerTerminalAuxiliaryPane.Exception => DebuggerTerminalAuxiliaryPane.Output,
            _ => throw new InvalidOperationException($"Unknown auxiliary pane {Pane}.")
        };
    }

    /// <summary>
    /// Clears only generation-bound auxiliary state when the target resumes.
    /// </summary>
    internal void ClearStoppedState()
    {
        _exceptionLines = ["No current managed exception."];
        ExceptionSummary = null;
    }

    private static string FormatModule(DebugModuleInfo module) => string.Create(
        CultureInfo.InvariantCulture,
        $"{module.Id,4}  {module.Name}  {module.SymbolKind}  " +
        $"{FormatUserCode(module.IsUserCode)}  {FormatOptimization(module.IsOptimized)}");

    private static string FormatSourceBreakpoint(DebugSourceBreakpointInfo breakpoint) =>
        $"{FormatBreakpointState(breakpoint.Verified)} #{breakpoint.Id} source " +
        $"{Path.GetFileName(breakpoint.SourcePath)}:{breakpoint.Line}" +
        FormatBreakpointOptions(breakpoint.Condition, breakpoint.HitCondition);

    private static string FormatFunctionBreakpoint(DebugFunctionBreakpointInfo breakpoint) =>
        $"{FormatBreakpointState(breakpoint.Verified)} #{breakpoint.Id} function " +
        breakpoint.Name + FormatBreakpointOptions(
            breakpoint.Condition,
            breakpoint.HitCondition);

    private static string FormatInstructionBreakpoint(
        DebugInstructionBreakpointInfo breakpoint) =>
        $"{FormatBreakpointState(breakpoint.Verified)} #{breakpoint.Id} instruction " +
        breakpoint.InstructionReference +
        (breakpoint.Offset == 0
            ? string.Empty
            : string.Create(CultureInfo.InvariantCulture, $" {breakpoint.Offset:+#;-#}")) +
        FormatBreakpointOptions(breakpoint.Condition, breakpoint.HitCondition);

    private static string FormatExceptionBreakpoint(
        DebugExceptionBreakpointRequest breakpoint) =>
        $"● exception {breakpoint.BreakMode}: " +
        (breakpoint.ExceptionTypeNames.Count == 0
            ? "all managed exceptions"
            : string.Join(", ", breakpoint.ExceptionTypeNames));

    private static string FormatBreakpointState(bool verified) => verified ? "●" : "○";

    private static string FormatUserCode(bool? isUserCode) => !isUserCode.HasValue
        ? "classification unknown"
        : isUserCode.GetValueOrDefault() ? "user" : "framework";

    private static string FormatOptimization(bool? isOptimized) => !isOptimized.HasValue
        ? "optimization unknown"
        : isOptimized.GetValueOrDefault() ? "optimized" : "unoptimized";

    private static string FormatBreakpointOptions(string? condition, string? hitCondition) =>
        (condition is null ? string.Empty : $"  when {condition}") +
        (hitCondition is null ? string.Empty : $"  hit {hitCondition}");

    private static string FormatOutputCategory(DebugOutputCategory category) => category switch
    {
        DebugOutputCategory.StandardOutput => "out>",
        DebugOutputCategory.StandardError => "err>",
        _ => "dbg>"
    };
}
