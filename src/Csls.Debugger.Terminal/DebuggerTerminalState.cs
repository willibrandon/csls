using Csls.Debugger.Contracts;
using Csls.Debugger.Control;
using Hex1b;
using System.Globalization;

namespace Csls.Debugger.Terminal;

/// <summary>
/// Holds immutable debugger snapshots loaded exclusively through private control RPC.
/// </summary>
internal sealed class DebuggerTerminalState
{
    private readonly DebuggerRpcClient _client;
    private readonly CancellationToken _cancellationToken;
    private Hex1bApp? _app;

    private DebuggerTerminalState(
        DebuggerRpcClient client,
        CancellationToken cancellationToken)
    {
        _client = client;
        _cancellationToken = cancellationToken;
    }

    /// <summary>
    /// Gets the current debugger lifecycle snapshot.
    /// </summary>
    internal DebugSessionSnapshot Snapshot { get; private set; } =
        new() { State = DebugSessionState.Created };

    /// <summary>
    /// Gets the bounded source context around the selected frame.
    /// </summary>
    internal IReadOnlyList<string> SourceLines { get; private set; } = [];

    /// <summary>
    /// Gets the source row that should remain visible and focused.
    /// </summary>
    internal int SourceFocusedIndex { get; private set; }

    /// <summary>
    /// Gets the current managed stack display rows.
    /// </summary>
    internal IReadOnlyList<string> StackLines { get; private set; } = [];

    /// <summary>
    /// Gets the current argument and local variable display rows.
    /// </summary>
    internal IReadOnlyList<string> VariableLines { get; private set; } = [];

    /// <summary>
    /// Creates state and waits until the target reaches its initial stop.
    /// </summary>
    /// <param name="client">The connected debugger RPC client.</param>
    /// <param name="cancellationToken">The interactive session cancellation token.</param>
    /// <returns>The fully loaded stopped-state snapshot.</returns>
    internal static async Task<DebuggerTerminalState> CreateAsync(
        DebuggerRpcClient client,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        var state = new DebuggerTerminalState(client, cancellationToken);
        await state.WaitForStopAndLoadAsync().ConfigureAwait(false);
        return state;
    }

    /// <summary>
    /// Attaches the running Hex1b application used to redraw state changes.
    /// </summary>
    /// <param name="app">The running application.</param>
    internal void AttachApp(Hex1bApp app)
    {
        ArgumentNullException.ThrowIfNull(app);
        _app = app;
    }

    /// <summary>
    /// Continues execution until the next debugger stop or process exit.
    /// </summary>
    /// <returns>A task that completes after refreshed state is available.</returns>
    internal async Task ContinueAsync()
    {
        Snapshot = await _client.ContinueAsync(_cancellationToken).ConfigureAwait(false);
        ClearInspection();
        _app?.Invalidate();
        await WaitForStopAndLoadAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Pauses a running target and reloads its managed state.
    /// </summary>
    /// <returns>A task that completes after refreshed state is available.</returns>
    internal async Task PauseAsync()
    {
        Snapshot = await _client.PauseAsync(_cancellationToken).ConfigureAwait(false);
        await LoadStoppedStateAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Performs one source-level step and reloads the next stopped state.
    /// </summary>
    /// <param name="kind">The source-level step kind.</param>
    /// <returns>A task that completes after refreshed state is available.</returns>
    internal async Task StepAsync(DebugStepKind kind)
    {
        int threadId = Snapshot.StoppedThreadId
            ?? throw new InvalidOperationException("The stopped thread is unavailable.");
        Snapshot = await _client.StepAsync(
            new DebugStepRequest(threadId, kind),
            _cancellationToken).ConfigureAwait(false);
        ClearInspection();
        _app?.Invalidate();
        await WaitForStopAndLoadAsync().ConfigureAwait(false);
    }

    private async Task WaitForStopAndLoadAsync()
    {
        while (true)
        {
            Snapshot = await _client.GetSessionAsync(_cancellationToken).ConfigureAwait(false);
            if (Snapshot.State == DebugSessionState.Stopped)
            {
                await LoadStoppedStateAsync().ConfigureAwait(false);
                return;
            }

            if (Snapshot.State is DebugSessionState.Terminated or DebugSessionState.Faulted)
            {
                ClearInspection();
                _app?.Invalidate();
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25), _cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task LoadStoppedStateAsync()
    {
        IReadOnlyList<DebugThreadInfo> threads = await _client
            .GetThreadsAsync(_cancellationToken)
            .ConfigureAwait(false);
        int threadId = Snapshot.StoppedThreadId ?? threads[0].Id;
        DebugStackTrace stack = await _client.GetStackAsync(
            new DebugStackRequest(threadId, 0, 200),
            _cancellationToken).ConfigureAwait(false);
        StackLines = stack.StackFrames
            .Select(static frame => string.Create(
                CultureInfo.InvariantCulture,
                $"{frame.Name}  {frame.Source?.Name}:{frame.Line}"))
            .ToArray();
        DebugStackFrameInfo? selectedFrame = stack.StackFrames.FirstOrDefault(
            static frame => frame.Source is not null && frame.Line > 0)
            ?? (stack.StackFrames.Count == 0 ? null : stack.StackFrames[0]);
        if (selectedFrame is null)
        {
            SourceLines = ["No managed frame is available."];
            VariableLines = [];
            _app?.Invalidate();
            return;
        }

        SourceLines = await LoadSourceContextAsync(selectedFrame, _cancellationToken)
            .ConfigureAwait(false);
        SourceFocusedIndex = Math.Min(50, selectedFrame.Line - 1);
        VariableLines = await LoadVariablesAsync(selectedFrame.Id).ConfigureAwait(false);
        _app?.Invalidate();
    }

    private async Task<IReadOnlyList<string>> LoadVariablesAsync(int frameId)
    {
        IReadOnlyList<DebugScopeInfo> scopes = await _client.GetScopesAsync(
            new DebugScopesRequest(frameId),
            _cancellationToken).ConfigureAwait(false);
        var lines = new List<string>();
        foreach (DebugScopeInfo scope in scopes)
        {
            lines.Add($"[{scope.Name}]");
            IReadOnlyList<DebugVariableInfo> variables = await _client.GetVariablesAsync(
                new DebugVariablesRequest(scope.VariablesReference, 0, 200),
                _cancellationToken).ConfigureAwait(false);
            lines.AddRange(variables.Select(static variable =>
                $"{variable.Name} = {variable.Value}  {variable.Type}"));
        }

        return lines;
    }

    private async Task<IReadOnlyList<string>> LoadSourceContextAsync(
        DebugStackFrameInfo frame,
        CancellationToken cancellationToken)
    {
        if (frame.Source is null)
        {
            return ["Source is unavailable for the selected frame."];
        }

        string[] lines;
        if (frame.Source.SourceReference > 0)
        {
            DebugSourceContent source = await _client.GetSourceContentAsync(
                new DebugSourceRequest(frame.Source.SourceReference),
                cancellationToken).ConfigureAwait(false);
            lines = source.Content
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n')
                .Split('\n');
        }
        else if (frame.Source.Path is not null && File.Exists(frame.Source.Path))
        {
            lines = await File.ReadAllLinesAsync(frame.Source.Path, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            return ["Source is unavailable for the selected frame."];
        }

        int first = Math.Max(1, frame.Line - 50);
        int last = Math.Min(lines.Length, frame.Line + 50);
        return Enumerable.Range(first, last - first + 1)
            .Select(line => string.Create(
                CultureInfo.InvariantCulture,
                $"{(line == frame.Line ? '>' : ' ')} {line,5}  {lines[line - 1]}"))
            .ToArray();
    }

    private void ClearInspection()
    {
        SourceLines = ["Target is running."];
        SourceFocusedIndex = 0;
        StackLines = [];
        VariableLines = [];
    }
}
