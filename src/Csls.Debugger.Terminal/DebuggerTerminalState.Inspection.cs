using Csls.Debugger.Contracts;
using System.Globalization;

namespace Csls.Debugger.Terminal;

/// <summary>
/// Loads navigable stopped-state source, threads, frames, variables, modules, and output.
/// </summary>
internal sealed partial class DebuggerTerminalState
{
    private IReadOnlyList<DebugStackFrameInfo> _stackFrames = [];
    private IReadOnlyList<DebugThreadInfo> _threads = [];
    private DebugStackFrameInfo? _selectedFrame;
    private long _sourceRevision;
    private long _threadRevision;
    private long _stackRevision;
    private int _sourceFrameId;
    private int _sourceFirstLine;
    private string[] _sourceTextLines = [];

    /// <summary>
    /// Gets the bounded source context around the selected frame.
    /// </summary>
    internal IReadOnlyList<string> SourceLines { get; private set; } =
        ["Waiting for a managed stop."];

    /// <summary>
    /// Gets the source row that should remain visible and focused.
    /// </summary>
    internal int SourceFocusedIndex { get; private set; }

    /// <summary>
    /// Gets the current managed-thread display rows.
    /// </summary>
    internal IReadOnlyList<string> ThreadLines { get; private set; } = [];

    /// <summary>
    /// Gets the selected managed-thread row.
    /// </summary>
    internal int SelectedThreadIndex { get; private set; }

    /// <summary>
    /// Gets the current managed-stack display rows.
    /// </summary>
    internal IReadOnlyList<string> StackLines { get; private set; } = [];

    /// <summary>
    /// Gets the selected managed-stack row.
    /// </summary>
    internal int SelectedStackFrameIndex { get; private set; }

    /// <summary>
    /// Gets the current argument and local variable display rows.
    /// </summary>
    internal IReadOnlyList<string> VariableLines { get; private set; } = [];

    /// <summary>
    /// Selects a managed thread and loads its stack and first authored frame.
    /// </summary>
    /// <param name="index">The zero-based thread row.</param>
    /// <param name="view">The published view that produced the selection.</param>
    /// <returns>A task that completes when the dependent panes are refreshed.</returns>
    internal async Task SelectThreadAsync(int index, DebuggerTerminalViewSnapshot view)
    {
        await _mutationGate.WaitAsync(_cancellationToken).ConfigureAwait(false);
        try
        {
            if (view.ThreadRevision != _threadRevision ||
                Snapshot.State != DebugSessionState.Stopped ||
                index < 0 || index >= _threads.Count || index == SelectedThreadIndex)
            {
                return;
            }

            await LoadThreadAsync(index, _cancellationToken).ConfigureAwait(false);
            PublishViewSnapshot();
        }
        finally
        {
            _ = _mutationGate.Release();
        }
    }

    /// <summary>
    /// Selects a managed stack frame and reloads its source and variables.
    /// </summary>
    /// <param name="index">The zero-based stack-frame row.</param>
    /// <param name="view">The published view that produced the selection.</param>
    /// <returns>A task that completes when the dependent panes are refreshed.</returns>
    internal async Task SelectStackFrameAsync(int index, DebuggerTerminalViewSnapshot view)
    {
        await _mutationGate.WaitAsync(_cancellationToken).ConfigureAwait(false);
        try
        {
            if (view.StackRevision != _stackRevision ||
                Snapshot.State != DebugSessionState.Stopped ||
                index < 0 || index >= _stackFrames.Count || index == SelectedStackFrameIndex)
            {
                return;
            }

            SelectedStackFrameIndex = index;
            await LoadFrameAsync(_stackFrames[index], _cancellationToken).ConfigureAwait(false);
            PublishViewSnapshot();
        }
        finally
        {
            _ = _mutationGate.Release();
        }
    }

    /// <summary>
    /// Moves the source cursor used by interactive breakpoint operations.
    /// </summary>
    /// <param name="index">The zero-based visible source row.</param>
    /// <param name="view">The published view that produced the selection.</param>
    internal async Task SelectSourceLineAsync(int index, DebuggerTerminalViewSnapshot view)
    {
        await _mutationGate.WaitAsync(_cancellationToken).ConfigureAwait(false);
        try
        {
            if (view.SourceRevision == _sourceRevision &&
                index >= 0 && index < SourceLines.Count && index != SourceFocusedIndex)
            {
                SourceFocusedIndex = index;
                PublishViewSnapshot();
            }
        }
        finally
        {
            _ = _mutationGate.Release();
        }
    }

    /// <summary>
    /// Adds or removes a source breakpoint at the source cursor.
    /// </summary>
    /// <returns>A task that completes when runtime binding and source rendering finish.</returns>
    internal async Task ToggleSourceBreakpointAsync()
    {
        await _mutationGate.WaitAsync(_cancellationToken).ConfigureAwait(false);
        try
        {
            if (!RequireState(DebugSessionState.Stopped, "change breakpoints"))
            {
                return;
            }

            string? sourcePath = _selectedFrame?.Source?.Path;
            if (string.IsNullOrWhiteSpace(sourcePath) || _sourceFirstLine <= 0)
            {
                StatusMessage = "The selected frame has no breakpoint-addressable source path.";
                PublishViewSnapshot();
                return;
            }

            int line = checked(_sourceFirstLine + SourceFocusedIndex);
            List<DebugSourceBreakpointInfo> existing =
            [
                .. _auxiliary.Breakpoints.SourceBreakpoints.Where(
                    item => SourcePathsEqual(item.SourcePath, sourcePath))
            ];
            bool remove = existing.Any(item => item.Line == line);
            DebugSourceBreakpointRequest[] replacement =
            [
                .. existing
                    .Where(item => !remove || item.Line != line)
                    .Select(static item => new DebugSourceBreakpointRequest(
                        item.Line,
                        item.Column,
                        item.Condition,
                        item.HitCondition,
                        item.LogMessage)),
                .. remove ? [] : new[] { new DebugSourceBreakpointRequest(line, null) }
            ];
            IReadOnlyList<DebugSourceBreakpointInfo> result = await _client
                .SetSourceBreakpointsAsync(
                    new DebugSourceBreakpointSetRequest(sourcePath, replacement),
                    _cancellationToken).ConfigureAwait(false);
            DebugSourceBreakpointInfo? selected = result.FirstOrDefault(item => item.Line == line);
            StatusMessage = remove
                ? $"Removed breakpoint at {Path.GetFileName(sourcePath)}:{line}."
                : selected?.Verified == true
                    ? $"Set breakpoint at {Path.GetFileName(sourcePath)}:{selected.Line}."
                    : selected?.Message ??
                        $"Breakpoint at {Path.GetFileName(sourcePath)}:{line} is pending.";
            await _auxiliary.RefreshBreakpointsAsync(_cancellationToken).ConfigureAwait(false);
            await LoadSourceAsync(_selectedFrame!, _cancellationToken).ConfigureAwait(false);
            PublishViewSnapshot();
        }
        finally
        {
            _ = _mutationGate.Release();
        }
    }

    private async Task LoadStoppedStateAsync(CancellationToken cancellationToken)
    {
        StatusMessage = null;
        await _auxiliary.LoadAsync(Snapshot, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<DebugThreadInfo> threads = await _client.GetThreadsAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!_threads.Select(static thread => thread.Id)
            .SequenceEqual(threads.Select(static thread => thread.Id)))
        {
            _threadRevision = checked(_threadRevision + 1);
        }

        _threads = threads;
        ThreadLines = _threads.Select(static thread => string.Create(
            CultureInfo.InvariantCulture,
            $"{thread.Id,4}  {thread.Name}")).ToArray();
        if (_threads.Count == 0)
        {
            ClearInspection();
            SourceLines = ["No managed thread is available."];
            PublishViewSnapshot();
            return;
        }

        int stoppedIndex = Snapshot.StoppedThreadId is int stoppedThreadId
            ? Math.Max(0, _threads.ToList().FindIndex(thread => thread.Id == stoppedThreadId))
            : 0;
        await LoadPreferredThreadAsync(stoppedIndex, cancellationToken).ConfigureAwait(false);
        PublishViewSnapshot();
    }

    private async Task LoadThreadAsync(int index, CancellationToken cancellationToken)
    {
        DebugStackTrace stack = await _client.GetStackAsync(
            new DebugStackRequest(_threads[index].Id, 0, 200),
            cancellationToken).ConfigureAwait(false);
        await ApplyThreadStackAsync(index, stack, cancellationToken).ConfigureAwait(false);
    }

    private async Task LoadPreferredThreadAsync(
        int preferredIndex,
        CancellationToken cancellationToken)
    {
        (int Index, DebugStackTrace Stack)? firstManagedStack = null;
        foreach (int index in Enumerable.Range(0, _threads.Count)
            .OrderBy(index => index == preferredIndex ? 0 : 1))
        {
            DebugStackTrace stack = await _client.GetStackAsync(
                new DebugStackRequest(_threads[index].Id, 0, 200),
                cancellationToken).ConfigureAwait(false);
            if (stack.StackFrames.Count == 0)
            {
                continue;
            }

            firstManagedStack ??= (index, stack);
            if (stack.StackFrames.Any(static frame => frame.Source is not null && frame.Line > 0))
            {
                await ApplyThreadStackAsync(index, stack, cancellationToken)
                    .ConfigureAwait(false);
                return;
            }
        }

        if (firstManagedStack is { } fallback)
        {
            await ApplyThreadStackAsync(fallback.Index, fallback.Stack, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        await ApplyThreadStackAsync(
            preferredIndex,
            new DebugStackTrace([], 0),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task ApplyThreadStackAsync(
        int index,
        DebugStackTrace stack,
        CancellationToken cancellationToken)
    {
        SelectedThreadIndex = index;
        if (!_stackFrames.Select(static frame => frame.Id)
            .SequenceEqual(stack.StackFrames.Select(static frame => frame.Id)))
        {
            _stackRevision = checked(_stackRevision + 1);
        }

        _stackFrames = stack.StackFrames;
        StackLines = _stackFrames.Select(FormatStackFrame).ToArray();
        int authoredFrameIndex = _stackFrames.ToList().FindIndex(
            static frame => frame.Source is not null && frame.Line > 0);
        SelectedStackFrameIndex = Math.Max(0, authoredFrameIndex);
        if (_stackFrames.Count == 0)
        {
            _selectedFrame = null;
            SetUnavailableSource("No managed frame is available.");
            VariableLines = [];
            _auxiliary.ClearWatchValues("No managed frame is available.");
            return;
        }

        await LoadFrameAsync(
            _stackFrames[SelectedStackFrameIndex],
            cancellationToken).ConfigureAwait(false);
    }

    private async Task LoadFrameAsync(
        DebugStackFrameInfo frame,
        CancellationToken cancellationToken)
    {
        _selectedFrame = frame;
        await LoadSourceAsync(frame, cancellationToken).ConfigureAwait(false);
        VariableLines = await LoadVariablesAsync(frame.Id, cancellationToken)
            .ConfigureAwait(false);
        await _auxiliary.LoadWatchesAsync(frame.Id, cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<string>> LoadVariablesAsync(
        int frameId,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<DebugScopeInfo> scopes = await _client.GetScopesAsync(
            new DebugScopesRequest(frameId),
            cancellationToken).ConfigureAwait(false);
        var lines = new List<string>();
        foreach (DebugScopeInfo scope in scopes)
        {
            lines.Add($"[{scope.Name}]");
            IReadOnlyList<DebugVariableInfo> variables = await _client.GetVariablesAsync(
                new DebugVariablesRequest(
                    scope.VariablesReference,
                    0,
                    200,
                    AllowTargetCodeExecution: true),
                cancellationToken).ConfigureAwait(false);
            lines.AddRange(variables.Select(static variable =>
                $"{variable.Name} = {variable.Value}  {variable.Type}"));
        }

        return lines;
    }

    private async Task LoadSourceAsync(
        DebugStackFrameInfo frame,
        CancellationToken cancellationToken)
    {
        if (frame.Source is null)
        {
            SetUnavailableSource("Source is unavailable for the selected frame.");
            return;
        }

        if (frame.Source.SourceReference > 0)
        {
            DebugSourceContent source = await _client.GetSourceContentAsync(
                new DebugSourceRequest(frame.Source.SourceReference),
                cancellationToken).ConfigureAwait(false);
            _sourceTextLines = source.Content
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n')
                .Split('\n');
        }
        else if (frame.Source.Path is not null && File.Exists(frame.Source.Path))
        {
            _sourceTextLines = await File.ReadAllLinesAsync(
                frame.Source.Path,
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            SetUnavailableSource("Source is unavailable for the selected frame.");
            return;
        }

        int firstLine = Math.Max(1, frame.Line - 50);
        int last = Math.Min(_sourceTextLines.Length, frame.Line + 50);
        bool sourceMappingChanged = _sourceFrameId != frame.Id ||
            _sourceFirstLine != firstLine || SourceLines.Count != last - firstLine + 1;
        if (sourceMappingChanged)
        {
            _sourceRevision = checked(_sourceRevision + 1);
        }

        _sourceFrameId = frame.Id;
        _sourceFirstLine = firstLine;
        SourceFocusedIndex = Math.Clamp(
            sourceMappingChanged ? frame.Line - _sourceFirstLine : SourceFocusedIndex,
            0,
            Math.Max(0, last - _sourceFirstLine));
        HashSet<int> breakpointLines =
        [
            .. _auxiliary.Breakpoints.SourceBreakpoints
                .Where(item => frame.Source.Path is not null &&
                    SourcePathsEqual(item.SourcePath, frame.Source.Path))
                .Select(static item => item.Line)
        ];
        SourceLines = Enumerable.Range(_sourceFirstLine, last - _sourceFirstLine + 1)
            .Select(line => string.Create(
                CultureInfo.InvariantCulture,
                $"{(breakpointLines.Contains(line) ? '●' : ' ')} {line,5}  " +
                $"{_sourceTextLines[line - 1]}"))
            .ToArray();
    }

    private void SetUnavailableSource(string message)
    {
        _sourceRevision = checked(_sourceRevision + 1);
        _sourceFrameId = 0;
        _sourceTextLines = [];
        _sourceFirstLine = 0;
        SourceFocusedIndex = 0;
        SourceLines = [message];
    }

    private void ClearInspection(string? message = null)
    {
        message ??= Snapshot.State switch
        {
            DebugSessionState.Running => "Target is running.",
            DebugSessionState.Stopped => "Loading stopped-state details.",
            DebugSessionState.Terminated => "Target has terminated.",
            _ => "Debugger inspection is unavailable."
        };
        _threadRevision = checked(_threadRevision + 1);
        _stackRevision = checked(_stackRevision + 1);
        _sourceRevision = checked(_sourceRevision + 1);
        _sourceFrameId = 0;
        _threads = [];
        _stackFrames = [];
        _selectedFrame = null;
        _sourceTextLines = [];
        _sourceFirstLine = 0;
        SourceLines = [message];
        SourceFocusedIndex = 0;
        ThreadLines = [];
        SelectedThreadIndex = 0;
        StackLines = [];
        SelectedStackFrameIndex = 0;
        VariableLines = [];
        _auxiliary.ClearStoppedState(message);
    }

    private static string FormatStackFrame(DebugStackFrameInfo frame) =>
        frame.Source is null || frame.Line <= 0
            ? frame.Name
            : string.Create(
                CultureInfo.InvariantCulture,
                $"{frame.Name}  {frame.Source.Name}:{frame.Line}");

    private static bool SourcePathsEqual(string left, string right) =>
        string.Equals(
            left,
            right,
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);
}
