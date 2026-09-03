using System.Diagnostics;

namespace Csls.Debugger;

/// <summary>
/// Owns one launched or attached CoreCLR process and its COM debugger resources.
/// </summary>
internal sealed partial class CorDebugDebuggee : IDebuggeeProcess
{
    private readonly DebuggerSessionActor _actor;
    private readonly SourceBreakpointManager _sourceBreakpoints;
    private readonly CorDebugManagedCallback _managedCallback;
    private readonly CorDebugRuntimeStartupRegistration _registration;
    private readonly DbgShimStandardStreams? _standardStreams;
    private readonly TextReader _standardOutput;
    private readonly TextReader _standardError;
    private readonly Process _process;
    private readonly UnixChildExitMonitor? _unixExitMonitor;
    private readonly bool _ownsProcess;
    private readonly Dictionary<(int ThreadId, int FrameIndex), ManagedFrameHandle> _frames = [];
    private readonly Dictionary<string, ManagedInstructionReferenceHandle> _instructionFrames =
        new(StringComparer.Ordinal);
    private readonly Dictionary<int, ManagedStepTargetHandle> _stepTargets = [];
    private readonly Dictionary<int, ManagedGotoTargetHandle> _gotoTargets = [];
    private readonly Dictionary<(int FrameId, ManagedScopeKind Kind), ManagedScopeHandle> _scopes = [];
    private readonly Dictionary<int, ManagedValueHandle> _values = [];
    private readonly Dictionary<nint, ManagedValueHandle> _valueIdentities = [];
    private readonly Dictionary<string, ManagedValueHandle> _memoryValues =
        new(StringComparer.Ordinal);
    private nint _corDebug;
    private nint _debugProcess;
    private nint _activeStepper;
    private nint _activeStepperIdentity;
    private ManagedAsyncStep? _asyncStep;
    private ManagedTargetBreakpoint? _targetBreakpoint;
    private int _nextFrameId;
    private int _nextStepTargetId;
    private int _nextGotoTargetId;
    private int _nextVariablesReference;
    private int _ownsRuntimeLease;
    private int _detached;
    private int _disposed;

    private CorDebugDebuggee(
        DebuggerSessionActor actor,
        SourceBreakpointManager sourceBreakpoints,
        CorDebugManagedCallback managedCallback,
        CorDebugRuntimeStartupRegistration registration,
        DbgShimStandardStreams? standardStreams,
        Process process,
        UnixChildExitMonitor? unixExitMonitor,
        bool ownsProcess,
        bool ownsRuntimeLease,
        CorDebugActivationResult activation)
    {
        _actor = actor;
        _sourceBreakpoints = sourceBreakpoints;
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
    }

    /// <inheritdoc />
    public int Id => _process.Id;

    /// <inheritdoc />
    public string Name => _process.ProcessName;

    /// <inheritdoc />
    public bool OwnsProcess => _ownsProcess;

}
