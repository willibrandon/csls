using Csls.Debugger.Contracts;
using Microsoft.Diagnostics.Runtime;

namespace Csls.Debugger.Dump;

/// <summary>
/// Activates and closes managed process-dump inspection sessions.
/// </summary>
public sealed partial class DumpDebuggerControlService
{
    /// <inheritdoc />
    public Task<DebugSessionSnapshot> OpenDumpAsync(
        DebugDumpOpenRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return InvokeAsync(() => OpenDump(request), cancellationToken);
    }

    /// <inheritdoc />
    public Task<DebugSessionSnapshot> LaunchAsync(
        DebugLaunchRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        throw CreateReadOnlyException("target launch");
    }

    /// <inheritdoc />
    public Task<DebugSessionSnapshot> AttachAsync(
        DebugAttachRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        throw CreateReadOnlyException("live-process attachment");
    }

    /// <inheritdoc />
    public Task<DebugSessionSnapshot> RestartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        throw CreateReadOnlyException("restart");
    }

    /// <inheritdoc />
    public Task<DebugSessionSnapshot> TerminateAsync(CancellationToken cancellationToken) =>
        CloseAsync(cancellationToken);

    /// <inheritdoc />
    public Task<DebugSessionSnapshot> DetachAsync(CancellationToken cancellationToken) =>
        CloseAsync(cancellationToken);

    private DebugSessionSnapshot OpenDump(DebugDumpOpenRequest request)
    {
        if (_snapshot.State != DebugSessionState.Created)
        {
            throw new InvalidOperationException(
                "This process-dump worker already owns a session.");
        }

        if (string.IsNullOrWhiteSpace(request.DumpPath) ||
            !Path.IsPathFullyQualified(request.DumpPath))
        {
            throw new ArgumentException(
                "DumpPath must be an absolute path.",
                nameof(request));
        }

        string dumpPath = Path.GetFullPath(request.DumpPath);
        if (!File.Exists(dumpPath))
        {
            throw new FileNotFoundException("The selected process dump does not exist.", dumpPath);
        }

        if (request.RuntimeIndex < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                "RuntimeIndex must not be negative.");
        }

        string? dacPath = ValidateDacPath(request.DacPath);
        _dataTarget = DataTarget.LoadDump(
            dumpPath,
            new DataTargetOptions
            {
                SymbolPaths = [],
                TraceSymbolRequests = false,
                VerifyDacOnWindows = true,
                UseLockFreeMemoryMapReader = Environment.Is64BitProcess
            });
        try
        {
            if (request.RuntimeIndex >= _dataTarget.ClrVersions.Length)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(request),
                    $"The dump contains {_dataTarget.ClrVersions.Length} managed runtime(s).");
            }

            ClrInfo runtimeInfo = _dataTarget.ClrVersions[request.RuntimeIndex];
            _runtime = dacPath is null
                ? runtimeInfo.CreateRuntime()
                : runtimeInfo.CreateRuntime(dacPath, ignoreMismatch: false);
            IReadOnlyList<DumpThread> threads = CreateThreads(_runtime);
            IReadOnlyList<DebugModuleInfo> modules = CreateModules(_runtime);
            _threads = threads;
            _modules = modules;
            int? stoppedThreadId = threads.Count == 0 ? null : threads[0].Id;
            _snapshot = new DebugSessionSnapshot
            {
                State = DebugSessionState.Stopped,
                ProcessName = Path.GetFileName(dumpPath),
                ProcessId = _dataTarget.DataReader.ProcessId > 0
                    ? _dataTarget.DataReader.ProcessId
                    : null,
                StopReason = "dump",
                StoppedThreadId = stoppedThreadId,
                StopGeneration = 1
            };
            return _snapshot;
        }
        catch
        {
            DisposeTarget();
            throw;
        }
    }

    private Task<DebugSessionSnapshot> CloseAsync(CancellationToken cancellationToken) =>
        InvokeAsync(
            () =>
            {
                DisposeTarget();
                _snapshot = new DebugSessionSnapshot
                {
                    State = DebugSessionState.Terminated,
                    ProcessName = _snapshot.ProcessName,
                    ProcessId = _snapshot.ProcessId,
                    StopGeneration = _snapshot.StopGeneration
                };
                return _snapshot;
            },
            cancellationToken);

    private static string? ValidateDacPath(string? dacPath)
    {
        if (dacPath is null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(dacPath) || !Path.IsPathFullyQualified(dacPath))
        {
            throw new ArgumentException("DacPath must be an absolute path.", nameof(dacPath));
        }

        string fullPath = Path.GetFullPath(dacPath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("The selected DAC does not exist.", fullPath);
        }

        return fullPath;
    }
}
