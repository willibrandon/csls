using Csls.Debugger.Contracts;
using Csls.Debugger.Interop;

namespace Csls.Debugger;

/// <summary>
/// Resolves logical managed-IL breakpoints and owns their runtime bindings.
/// </summary>
internal sealed partial class InstructionBreakpointManager : IDisposable
{
    private const int MaximumModuleCount = 4096;
    private static readonly Guid s_iUnknownInterfaceId =
        new("00000000-0000-0000-C000-000000000046");
    private readonly Func<DebugInstructionBreakpointInfo, CancellationToken, ValueTask>
        _notifyChanged;
    private readonly List<InstructionBreakpointDefinition> _definitions = [];
    private readonly Dictionary<nint, InstructionBreakpointBinding> _bindings = [];
    private readonly Dictionary<nint, InstructionBreakpointModule> _modules = [];
    private int _nextBreakpointId;
    private int _disposed;

    /// <summary>
    /// Creates an empty manager that publishes runtime binding changes.
    /// </summary>
    /// <param name="notifyChanged">The ordered breakpoint-change callback.</param>
    internal InstructionBreakpointManager(
        Func<DebugInstructionBreakpointInfo, CancellationToken, ValueTask> notifyChanged)
    {
        ArgumentNullException.ThrowIfNull(notifyChanged);
        _notifyChanged = notifyChanged;
    }

    /// <summary>
    /// Replaces every logical managed-IL breakpoint.
    /// </summary>
    /// <param name="requests">The resolved complete replacement set.</param>
    /// <param name="cancellationToken">Cancels runtime binding.</param>
    /// <returns>The ordered current breakpoint states.</returns>
    internal async ValueTask<IReadOnlyList<DebugInstructionBreakpointInfo>> SetAsync(
        IReadOnlyList<ManagedInstructionBreakpointRequest> requests,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        ArgumentNullException.ThrowIfNull(requests);
        ReleaseBindings();
        _definitions.Clear();
        foreach (ManagedInstructionBreakpointRequest request in requests)
        {
            bool validHitCondition = DebugHitCondition.TryParse(
                request.HitCondition,
                out DebugHitCondition? hitCondition);
            _definitions.Add(new InstructionBreakpointDefinition
            {
                Id = checked(++_nextBreakpointId),
                InstructionReference = request.InstructionReference,
                Offset = request.Offset,
                ModulePath = request.ModulePath,
                MethodToken = request.MethodToken,
                IlOffset = request.IlOffset,
                HitCondition = hitCondition,
                ValidationMessage = request.ValidationMessage ?? (validHitCondition
                    ? null
                    : DebugHitCondition.ValidationErrorMessage)
            });
        }

        foreach (InstructionBreakpointModule module in _modules.Values)
        {
            await BindModuleAsync(module, notifyChanges: false, cancellationToken)
                .ConfigureAwait(false);
        }

        return _definitions.Select(static definition => definition.ToInfo()).ToArray();
    }

    /// <summary>
    /// Records a runtime callback and evaluates its hit-count predicate.
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
            return _bindings.TryGetValue(identity, out InstructionBreakpointBinding? binding)
                ? binding.Definition.RegisterHit()
                : null;
        }
        finally
        {
            _ = ComAbi.Release(identity);
        }
    }

    /// <summary>
    /// Releases runtime objects after failed target activation.
    /// </summary>
    internal void ResetRuntimeBindings()
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        ReleaseBindings();
        ReleaseModules();
        foreach (InstructionBreakpointDefinition definition in _definitions)
        {
            definition.BindingMessage = null;
            definition.HitCondition?.Reset();
        }
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
}
