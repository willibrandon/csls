using Csls.Debugger.Contracts;
using Csls.Debugger.Interop;

namespace Csls.Debugger;

/// <summary>
/// Resolves logical managed function breakpoints and owns their runtime bindings.
/// </summary>
internal sealed partial class FunctionBreakpointManager : IDisposable
{
    private const int MaximumModuleCount = 4096;
    private static readonly Guid s_iUnknownInterfaceId =
        new("00000000-0000-0000-C000-000000000046");
    private readonly Func<DebugFunctionBreakpointInfo, CancellationToken, ValueTask> _notifyChanged;
    private readonly List<FunctionBreakpointDefinition> _definitions = [];
    private readonly Dictionary<nint, FunctionBreakpointBinding> _bindings = [];
    private readonly Dictionary<nint, CorDebugLoadedModule> _modules = [];
    private int _nextBreakpointId;
    private int _disposed;

    /// <summary>
    /// Creates an empty manager that publishes verified binding changes.
    /// </summary>
    /// <param name="notifyChanged">The ordered breakpoint-change notification callback.</param>
    internal FunctionBreakpointManager(
        Func<DebugFunctionBreakpointInfo, CancellationToken, ValueTask> notifyChanged)
    {
        ArgumentNullException.ThrowIfNull(notifyChanged);
        _notifyChanged = notifyChanged;
    }

    /// <summary>
    /// Replaces every logical managed function breakpoint.
    /// </summary>
    /// <param name="requests">The complete replacement request list.</param>
    /// <param name="cancellationToken">Cancels runtime rebinding.</param>
    /// <returns>The ordered current breakpoint snapshots.</returns>
    internal async ValueTask<IReadOnlyList<DebugFunctionBreakpointInfo>> SetAsync(
        IReadOnlyList<DebugFunctionBreakpointRequest> requests,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        ArgumentNullException.ThrowIfNull(requests);
        ReleaseBindings();
        _definitions.Clear();
        foreach (DebugFunctionBreakpointRequest request in requests)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(request.Name);
            bool validHitCondition = DebugHitCondition.TryParse(
                request.HitCondition,
                out DebugHitCondition? hitCondition);
            _definitions.Add(new FunctionBreakpointDefinition
            {
                Id = checked(++_nextBreakpointId),
                Name = NormalizeName(request.Name),
                HitCondition = hitCondition,
                ValidationMessage = validHitCondition
                    ? null
                    : DebugHitCondition.ValidationErrorMessage
            });
        }

        foreach (CorDebugLoadedModule module in _modules.Values)
        {
            await BindModuleAsync(module, notifyChanges: false, cancellationToken)
                .ConfigureAwait(false);
        }

        return _definitions.Select(static definition => definition.ToInfo()).ToArray();
    }

    /// <summary>
    /// Records a runtime function-breakpoint callback and evaluates its hit condition.
    /// </summary>
    /// <param name="breakpoint">The borrowed ICorDebugBreakpoint pointer.</param>
    /// <returns>Null when unowned, otherwise whether the target should stop.</returns>
    internal bool? GetBreakDecision(nint breakpoint)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        ArgumentOutOfRangeException.ThrowIfZero(breakpoint);
        nint identity = ComAbi.QueryInterface(breakpoint, s_iUnknownInterfaceId);
        try
        {
            return _bindings.TryGetValue(identity, out FunctionBreakpointBinding? binding)
                ? binding.Definition.RegisterHit()
                : null;
        }
        finally
        {
            _ = ComAbi.Release(identity);
        }
    }

    /// <summary>
    /// Releases runtime bindings after failed target activation.
    /// </summary>
    internal void ResetRuntimeBindings()
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        ReleaseBindings();
        ReleaseModules();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        ReleaseBindings();
        ReleaseModules();
        _definitions.Clear();
    }

    private static string NormalizeName(string name)
    {
        string normalized = name.Trim();
        int parameters = normalized.IndexOf('(', StringComparison.Ordinal);
        return parameters < 0 ? normalized : normalized[..parameters].TrimEnd();
    }
}
