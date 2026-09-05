using System.Diagnostics;

namespace Csls.Debugger;

/// <summary>
/// Owns one launched or attached CoreCLR process and its COM debugger resources.
/// </summary>
internal sealed partial class CorDebugDebuggee :
    IDebuggeeProcess,
    IManagedObjectExpansionServices,
    IManagedDebuggerDisplayServices
{
    private readonly DebuggerSessionActor _actor;
    private readonly SourceBreakpointManager _sourceBreakpoints;
    private readonly FunctionBreakpointManager _functionBreakpoints;
    private readonly InstructionBreakpointManager _instructionBreakpoints;
    private readonly ManagedTupleTypeShape _tupleTypeShape;
    private readonly ManagedTuplePresenter _tuplePresenter;
    private readonly ManagedObjectExpander _objectExpander;
    private readonly ManagedDebuggerDisplayFormatter _debuggerDisplayFormatter;
    private readonly ManagedDebuggerTypeProxyResolver _debuggerTypeProxyResolver;
    private readonly ManagedDebuggerTypeProxyPropertyResolver _debuggerTypeProxyPropertyResolver;
    private readonly ManagedResultsViewResolver _resultsViewResolver;
    private readonly ManagedBoundTypeSystem _boundTypes;
    private readonly ManagedLoadedTypeNameResolver _typeNames;
    private readonly ManagedFrameTypeResolver _frameTypes;
    private readonly ManagedReferenceConversion _referenceConversions;
    private readonly ManagedReferenceExpressionEvaluator _referenceExpressions;
    private readonly CorDebugManagedCallback _managedCallback;
    private readonly CorDebugRuntimeStartupRegistration _registration;
    private readonly DbgShimStandardStreams? _standardStreams;
    private readonly TextReader _standardOutput;
    private readonly TextReader _standardError;
    private readonly Process _process;
    private readonly UnixChildExitMonitor? _unixExitMonitor;
    private readonly bool _ownsProcess;
    private readonly ManagedStoppedFrameRegistry _frames = new();
    private readonly Dictionary<int, ManagedStepTargetHandle> _stepTargets = [];
    private readonly Dictionary<int, ManagedGotoTargetHandle> _gotoTargets = [];
    private readonly Dictionary<(int FrameId, ManagedScopeKind Kind), ManagedScopeHandle> _scopes = [];
    private readonly Dictionary<int, ManagedValueHandle> _values = [];
    private readonly Dictionary<(
        nint Identity,
        int? FrameId,
        string? EvaluateName,
        ManagedValueView View,
        ManagedValueOrigin? Origin,
        ManagedResultsViewLifetime? Lifetime),
        ManagedValueHandle> _valueIdentities = [];
    private readonly Dictionary<(
        ulong Address,
        ManagedResultsViewLifetime? Lifetime),
        ManagedHeapValueOrigin> _heapValueOrigins = [];
    private readonly Dictionary<string, ManagedValueHandle> _memoryValues =
        new(StringComparer.Ordinal);
    private ManagedFunctionEvaluation? _activeFunctionEvaluation;
    private ManagedResultsViewSnapshot? _resultsViewSnapshot;
    private string? _functionEvaluationDisabledReason;
    private nint _corDebug;
    private nint _debugProcess;
    private nint _activeStepper;
    private nint _activeStepperIdentity;
    private ManagedAsyncStep? _asyncStep;
    private ManagedTargetBreakpoint? _targetBreakpoint;
    private int _nextStepTargetId;
    private int _nextGotoTargetId;
    private int _nextVariablesReference;
    private int _ownsRuntimeLease;
    private int _detached;
    private int _disposed;

    private CorDebugDebuggee(
        DebuggerSessionActor actor,
        SourceBreakpointManager sourceBreakpoints,
        FunctionBreakpointManager functionBreakpoints,
        InstructionBreakpointManager instructionBreakpoints,
        DisposableOwner<CorDebugManagedCallback> managedCallbackOwner,
        DisposableOwner<CorDebugRuntimeStartupRegistration> registrationOwner,
        DbgShimStandardStreamsOwner? standardStreamsOwner,
        DisposableOwner<Process> processOwner,
        UnixChildExitMonitor? unixExitMonitor,
        bool ownsProcess,
        bool ownsRuntimeLease,
        CorDebugActivationResult activation)
    {
        CorDebugManagedCallback managedCallback = managedCallbackOwner.Value
            ?? throw new InvalidOperationException("No managed callback is owned.");
        CorDebugRuntimeStartupRegistration registration = registrationOwner.Value
            ?? throw new InvalidOperationException("No runtime registration is owned.");
        DbgShimStandardStreams? standardStreams = standardStreamsOwner?.Value;
        Process process = processOwner.Value
            ?? throw new InvalidOperationException("No debuggee process is owned.");
        _actor = actor;
        _sourceBreakpoints = sourceBreakpoints;
        _functionBreakpoints = functionBreakpoints;
        _instructionBreakpoints = instructionBreakpoints;
        _tupleTypeShape = new ManagedTupleTypeShape(this);
        _tuplePresenter = new ManagedTuplePresenter(
            this,
            _tupleTypeShape,
            FormatTupleElementType);
        _objectExpander = new ManagedObjectExpander(this, _tuplePresenter, _tupleTypeShape);
        _debuggerDisplayFormatter = new ManagedDebuggerDisplayFormatter(this);
        _debuggerTypeProxyResolver = new ManagedDebuggerTypeProxyResolver(sourceBreakpoints);
        _debuggerTypeProxyPropertyResolver =
            new ManagedDebuggerTypeProxyPropertyResolver(sourceBreakpoints);
        _resultsViewResolver = new ManagedResultsViewResolver(sourceBreakpoints);
        _typeNames = new ManagedLoadedTypeNameResolver(sourceBreakpoints);
        _boundTypes = new ManagedBoundTypeSystem(sourceBreakpoints, _typeNames);
        _frameTypes = new ManagedFrameTypeResolver(sourceBreakpoints, _boundTypes);
        _referenceConversions = new ManagedReferenceConversion(_boundTypes);
        _referenceExpressions = new ManagedReferenceExpressionEvaluator(_referenceConversions);
        _managedCallback = managedCallback;
        _registration = registration;
        _standardStreams = standardStreams;
        _standardOutput = standardStreams is null
            ? TextReader.Null
            : CreateReader(standardStreams.StandardOutput);
        _standardError = standardStreams is null
            ? TextReader.Null
            : CreateReader(standardStreams.StandardError);
        _process = process;
        _unixExitMonitor = unixExitMonitor;
        _ownsProcess = ownsProcess;
        _ownsRuntimeLease = ownsRuntimeLease ? 1 : 0;
        _corDebug = activation.CorDebug;
        _debugProcess = activation.Process;
        _ = managedCallbackOwner.Detach();
        _ = registrationOwner.Detach();
        if (standardStreamsOwner is not null)
        {
            _ = standardStreamsOwner.Detach();
        }

        _ = processOwner.Detach();
    }

    /// <inheritdoc />
    public int Id => _process.Id;

    /// <inheritdoc />
    public string Name => _process.ProcessName;

    /// <inheritdoc />
    public bool OwnsProcess => _ownsProcess;

}
