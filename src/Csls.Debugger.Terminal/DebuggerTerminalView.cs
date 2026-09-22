using Csls.Debugger.Contracts;
using Hex1b;
using Hex1b.Input;
using Hex1b.Widgets;

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
    /// <param name="sessions">The terminal-owned debugger sessions.</param>
    /// <param name="cancellationToken">The terminal lifetime token.</param>
    /// <returns>The full-screen debugger widget.</returns>
    internal static Hex1bWidget Build(
        RootContext context,
        DebuggerTerminalSessions sessions,
        CancellationToken cancellationToken)
    {
        sessions.AcknowledgeRefresh();
        DebuggerTerminalOwnedSession selected = sessions.Selected;
        DebuggerTerminalState state = selected.State;
        Guid sessionId = selected.Id;
        DebuggerTerminalViewSnapshot snapshot = state.CaptureViewSnapshot();
        return context.ZStack(stack =>
        [
            stack.WindowPanel()
                .Background(background => background.VStack(vertical =>
                [
                    vertical.Text(snapshot.Header +
                        (sessions.Message is string message ? $"  {message}" : string.Empty)),
                    vertical.HSplitter(
                        left =>
                        [
                            left.Border(nested =>
                                [nested.List(snapshot.SourceLines)
                                    .FocusedIndex(snapshot.SourceFocusedIndex)
                                    .OnFocusChanged(
                                        selection => sessions.InvokeSelectedAsync(sessionId,
                                            current => current.SelectSourceLineAsync(
                                                selection.FocusedIndex, snapshot), cancellationToken))
                                    .Fill()])
                                .Title(snapshot.SourceTitle)
                                .Fill()
                        ],
                        right =>
                        [
                            right.VStack(details =>
                            [
                                details.Border(nested =>
                                    [nested.List(snapshot.ThreadLines)
                                        .FocusedIndex(snapshot.SelectedThreadIndex)
                                        .OnFocusChanged(selection => sessions.InvokeSelectedAsync(sessionId,
                                            current => current.SelectThreadAsync(
                                                selection.FocusedIndex, snapshot), cancellationToken))
                                        .Fill()])
                                    .Title("Threads")
                                    .FixedHeight(6),
                                details.Border(nested =>
                                    [nested.List(snapshot.StackLines)
                                        .FocusedIndex(snapshot.SelectedStackFrameIndex)
                                        .OnFocusChanged(selection => sessions.InvokeSelectedAsync(sessionId,
                                            current => current.SelectStackFrameAsync(
                                                selection.FocusedIndex, snapshot), cancellationToken))
                                        .Fill()])
                                    .Title("Stack")
                                    .FixedHeight(8),
                                details.Border(nested =>
                                    [nested.List(snapshot.VariableLines).Fill()])
                                    .Title("Arguments and Locals")
                                    .Fill(),
                                details.Border(nested =>
                                    [nested.List(snapshot.AuxiliaryLines).Fill()])
                                    .Title(snapshot.AuxiliaryTitle)
                                    .FixedHeight(6)
                            ]).Fill()
                        ],
                        leftWidth: 64).Fill(),
                    vertical.InfoBar(
                        "F1 Commands  F2 Details  F3 Sessions  F5 Continue  Shift+F5 Stop  F6 Pause  " +
                        "F9 Breakpoint  F10 Over  F11 Into  " +
                        "F12 Out  Tab Panes  Ctrl+C Exit")
                ]).InputBindings(bindings =>
                {
                    bindings.Key(Hex1bKey.F1).Action(
                        eventArgs => OpenCommandPalette(eventArgs.Windows, sessions,
                            sessionId, cancellationToken),
                        "Open debugger command palette");
                    bindings.Key(Hex1bKey.F2).Action(
                        _ => sessions.InvokeSelectedAsync(sessionId,
                            static current => current.CycleAuxiliaryPaneAsync(), cancellationToken),
                        "Cycle output, module, breakpoint, watch, and exception views");
                    bindings.Key(Hex1bKey.F3).Action(
                        eventArgs => DebuggerTerminalSessionPrompts.OpenBrowser(
                            eventArgs.Windows, sessions, cancellationToken),
                        "Browse terminal-owned debugger sessions");
                    bindings.Key(Hex1bKey.F5).Action(
                        _ => sessions.InvokeSelectedAsync(sessionId,
                            static current => current.ContinueAsync(), cancellationToken),
                        "Continue target");
                    bindings.Shift().Key(Hex1bKey.F5).Action(
                        _ => sessions.InvokeSelectedAsync(sessionId,
                            static current => current.TerminateAsync(), cancellationToken),
                        "Terminate target");
                    bindings.Key(Hex1bKey.F6).Action(
                        _ => sessions.InvokeSelectedAsync(sessionId,
                            static current => current.PauseAsync(), cancellationToken),
                        "Pause target");
                    bindings.Key(Hex1bKey.F9).Action(
                        _ => sessions.InvokeSelectedAsync(sessionId,
                            static current => current.ToggleSourceBreakpointAsync(), cancellationToken),
                        "Toggle source breakpoint");
                    bindings.Key(Hex1bKey.F10).Action(
                        _ => sessions.InvokeSelectedAsync(sessionId,
                            static current => current.StepAsync(DebugStepKind.Over), cancellationToken),
                        "Step over");
                    bindings.Key(Hex1bKey.F11).Action(
                        _ => sessions.InvokeSelectedAsync(sessionId,
                            static current => current.StepAsync(DebugStepKind.Into), cancellationToken),
                        "Step into");
                    bindings.Key(Hex1bKey.F12).Action(
                        _ => sessions.InvokeSelectedAsync(sessionId,
                            static current => current.StepAsync(DebugStepKind.Out), cancellationToken),
                        "Step out");
                }))
                .Fill()
        ]);
    }

    private static void OpenCommandPalette(
        WindowManager windows,
        DebuggerTerminalSessions sessions,
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        windows.Window(window => window.SelectionPrompt(s_commands)
            .ItemText(FormatCommand)
            .FilterText(GetCommandName)
            .Prompt("Command:")
            .MaxVisibleItems(11)
            .OnSelected(async command =>
            {
                window.Window.CloseWithResult(command);
                if (command == DebuggerTerminalCommand.BrowseSessions)
                {
                    DebuggerTerminalSessionPrompts.OpenBrowser(windows, sessions, cancellationToken);
                    return;
                }

                if (command == DebuggerTerminalCommand.LaunchSession)
                {
                    DebuggerTerminalSessionPrompts.OpenLaunch(windows, sessions, cancellationToken);
                    return;
                }

                if (command == DebuggerTerminalCommand.AttachSession)
                {
                    DebuggerTerminalSessionPrompts.OpenAttach(windows, sessions, cancellationToken);
                    return;
                }

                if (command == DebuggerTerminalCommand.AddWatch)
                {
                    OpenWatchPrompt(windows, sessions, sessionId, cancellationToken);
                    return;
                }

                await sessions.InvokeSelectedAsync(sessionId,
                    current => current.ExecuteCommandAsync(command), cancellationToken).ConfigureAwait(false);
            }))
            .Title("Debugger commands")
            .Size(72, 16)
            .Modal()
            .Open(windows);
    }

    private static void OpenWatchPrompt(
        WindowManager windows,
        DebuggerTerminalSessions sessions,
        Guid sessionId,
        CancellationToken cancellationToken)
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
                    await sessions.InvokeSelectedAsync(sessionId,
                        current => current.AddWatchAsync(expression), cancellationToken).ConfigureAwait(false);
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
        DebuggerTerminalCommand.BrowseSessions => "Sessions                Browse and switch owned targets",
        DebuggerTerminalCommand.LaunchSession => "Launch session          Start another managed target",
        DebuggerTerminalCommand.AttachSession => "Attach session          Attach another managed process",
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
        DebuggerTerminalCommand.BrowseSessions => "Sessions",
        DebuggerTerminalCommand.LaunchSession => "Launch session",
        DebuggerTerminalCommand.AttachSession => "Attach session",
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
}
