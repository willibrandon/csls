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
    private static readonly IReadOnlyList<DebuggerTerminalCommand> s_commands =
        Enum.GetValues<DebuggerTerminalCommand>();

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
                                    [nested.List(state.AuxiliaryLines).Fill()])
                                    .Title(state.AuxiliaryTitle)
                                    .FixedHeight(6)
                            ]).Fill()
                        ],
                        leftWidth: 64).Fill(),
                    vertical.InfoBar(
                        "F1 Commands  F2 Details  F5 Continue  Shift+F5 Stop  F6 Pause  " +
                        "F9 Breakpoint  F10 Over  F11 Into  " +
                        "F12 Out  Tab Panes  Ctrl+C Exit")
                ]).InputBindings(bindings =>
                {
                    bindings.Key(Hex1bKey.F1).Action(
                        eventArgs => OpenCommandPalette(eventArgs.Windows, state),
                        "Open debugger command palette");
                    bindings.Key(Hex1bKey.F2).Action(
                        _ => state.CycleAuxiliaryPaneAsync(),
                        "Cycle output, module, breakpoint, watch, and exception views");
                    bindings.Key(Hex1bKey.F5).Action(
                        _ => state.ContinueAsync(),
                        "Continue target");
                    bindings.Shift().Key(Hex1bKey.F5).Action(
                        _ => state.TerminateAsync(),
                        "Terminate target");
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

    private static void OpenCommandPalette(
        WindowManager windows,
        DebuggerTerminalState state)
    {
        windows.Window(window => window.SelectionPrompt(s_commands)
            .ItemText(FormatCommand)
            .FilterText(GetCommandName)
            .Prompt("Command:")
            .MaxVisibleItems(11)
            .OnSelected(async command =>
            {
                window.Window.CloseWithResult(command);
                if (command == DebuggerTerminalCommand.AddWatch)
                {
                    OpenWatchPrompt(windows, state);
                    return;
                }

                await state.ExecuteCommandAsync(command).ConfigureAwait(false);
            }))
            .Title("Debugger commands")
            .Size(72, 16)
            .Modal()
            .Open(windows);
    }

    private static void OpenWatchPrompt(
        WindowManager windows,
        DebuggerTerminalState state)
    {
        string expression = string.Empty;
        windows.Window(window => window.VStack(vertical =>
        [
            vertical.Text(""),
            vertical.Text("  Enter a side-effect-free expression for the selected frame."),
            vertical.TextBox(expression)
                .OnTextChanged(eventArgs => expression = eventArgs.NewText)
                .OnSubmit(async _ =>
                {
                    window.Window.CloseWithResult(expression);
                    await state.AddWatchAsync(expression).ConfigureAwait(false);
                }),
            vertical.Text(""),
            vertical.Text("  Enter Add  Escape Cancel")
        ]))
            .Title("Watch expression")
            .Size(72, 8)
            .Modal()
            .Open(windows);
    }

    private static string FormatCommand(DebuggerTerminalCommand command) => command switch
    {
        DebuggerTerminalCommand.AddWatch => "Add watch               Evaluate without target code",
        DebuggerTerminalCommand.ClearWatches => "Clear watches           Remove every watch",
        DebuggerTerminalCommand.Continue => "Continue                Resume the target",
        DebuggerTerminalCommand.Pause => "Pause                   Break running execution",
        DebuggerTerminalCommand.StepOver => "Step over               Run the current statement",
        DebuggerTerminalCommand.StepInto => "Step into               Enter the current call",
        DebuggerTerminalCommand.StepOut => "Step out                Leave the current frame",
        DebuggerTerminalCommand.ToggleBreakpoint =>
            "Toggle breakpoint       Change the source cursor line",
        DebuggerTerminalCommand.Restart => "Restart                 Reactivate the original target",
        DebuggerTerminalCommand.Terminate => "Terminate               End the target process",
        DebuggerTerminalCommand.Detach => "Detach                  Leave the target running",
        _ => throw new InvalidOperationException($"Unknown terminal command {command}.")
    };

    private static string GetCommandName(DebuggerTerminalCommand command) => command switch
    {
        DebuggerTerminalCommand.AddWatch => "Add watch",
        DebuggerTerminalCommand.ClearWatches => "Clear watches",
        DebuggerTerminalCommand.Continue => "Continue",
        DebuggerTerminalCommand.Pause => "Pause",
        DebuggerTerminalCommand.StepOver => "Step over",
        DebuggerTerminalCommand.StepInto => "Step into",
        DebuggerTerminalCommand.StepOut => "Step out",
        DebuggerTerminalCommand.ToggleBreakpoint => "Toggle breakpoint",
        DebuggerTerminalCommand.Restart => "Restart",
        DebuggerTerminalCommand.Terminate => "Terminate",
        DebuggerTerminalCommand.Detach => "Detach",
        _ => throw new InvalidOperationException($"Unknown terminal command {command}.")
    };

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
