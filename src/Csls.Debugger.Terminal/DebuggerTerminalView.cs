using Csls.Debugger.Contracts;
using Hex1b;
using Hex1b.Input;
using Hex1b.Widgets;
using System.Globalization;

namespace Csls.Debugger.Terminal;

/// <summary>
/// Builds the interactive debugger's declarative Hex1b widget tree.
/// </summary>
internal static class DebuggerTerminalView
{
    /// <summary>
    /// Builds source, stack, and variable panes for the current debugger snapshot.
    /// </summary>
    /// <param name="context">The Hex1b root widget context.</param>
    /// <param name="state">The private-RPC debugger state.</param>
    /// <returns>The full-screen debugger widget.</returns>
    internal static Hex1bWidget Build(RootContext context, DebuggerTerminalState state) =>
        context.ZStack(stack =>
        [
            stack.WindowPanel()
                .Background(background => background.VStack(vertical =>
                [
                    vertical.Text(BuildHeader(state)),
                    vertical.HSplitter(
                        left =>
                        [
                            left.Border(nested =>
                                [nested.List(state.SourceLines)
                                    .FocusedIndex(state.SourceFocusedIndex)
                                    .OnFocusChanged(
                                        selection => state.SelectSourceLineAsync(
                                            selection.FocusedIndex))
                                    .Fill()])
                                .Title("Source")
                                .Fill()
                        ],
                        right =>
                        [
                            right.VStack(details =>
                            [
                                details.Border(nested =>
                                    [nested.List(state.ThreadLines)
                                        .FocusedIndex(state.SelectedThreadIndex)
                                        .OnFocusChanged(selection =>
                                            state.SelectThreadAsync(selection.FocusedIndex))
                                        .Fill()])
                                    .Title("Threads")
                                    .FixedHeight(6),
                                details.Border(nested =>
                                    [nested.List(state.StackLines)
                                        .FocusedIndex(state.SelectedStackFrameIndex)
                                        .OnFocusChanged(selection =>
                                            state.SelectStackFrameAsync(
                                                selection.FocusedIndex))
                                        .Fill()])
                                    .Title("Stack")
                                    .FixedHeight(8),
                                details.Border(nested =>
                                    [nested.List(state.VariableLines).Fill()])
                                    .Title("Arguments and Locals")
                                    .Fill(),
                                details.Border(nested =>
                                    [nested.List(state.OutputLines).Fill()])
                                    .Title("Target Output")
                                    .FixedHeight(6)
                            ]).Fill()
                        ],
                        leftWidth: 64).Fill(),
                    vertical.InfoBar(
                        "F5 Continue  F6 Pause  F9 Breakpoint  F10 Over  F11 Into  " +
                        "F12 Out  Tab Panes  Ctrl+C Exit")
                ]).InputBindings(bindings =>
                {
                    bindings.Key(Hex1bKey.F5).Action(
                        _ => state.ContinueAsync(),
                        "Continue target");
                    bindings.Key(Hex1bKey.F6).Action(
                        _ => state.PauseAsync(),
                        "Pause target");
                    bindings.Key(Hex1bKey.F9).Action(
                        _ => state.ToggleSourceBreakpointAsync(),
                        "Toggle source breakpoint");
                    bindings.Key(Hex1bKey.F10).Action(
                        _ => state.StepAsync(DebugStepKind.Over),
                        "Step over");
                    bindings.Key(Hex1bKey.F11).Action(
                        _ => state.StepAsync(DebugStepKind.Into),
                        "Step into");
                    bindings.Key(Hex1bKey.F12).Action(
                        _ => state.StepAsync(DebugStepKind.Out),
                        "Step out");
                }))
                .Fill()
        ]);

    private static string BuildHeader(DebuggerTerminalState state)
    {
        DebugSessionSnapshot snapshot = state.Snapshot;
        return $"csls debugger  {snapshot.ProcessName ?? "managed target"}  " +
            $"pid {snapshot.ProcessId?.ToString(CultureInfo.InvariantCulture) ?? "-"}  " +
            $"{snapshot.State}  {snapshot.StopReason ?? string.Empty}  " +
            $"{state.ModuleSummary}" +
            (state.ExceptionSummary is null ? string.Empty : $"  {state.ExceptionSummary}") +
            (state.StatusMessage is null ? string.Empty : $"  {state.StatusMessage}");
    }
}
