using Csls.Debugger.Contracts;
using Microsoft.Diagnostics.Runtime;

namespace Csls.Debugger.Dump;

/// <summary>
/// Exposes bounded managed process-dump inspection operations.
/// </summary>
public sealed partial class DumpDebuggerControlService
{
    /// <inheritdoc />
    public Task<IReadOnlyList<DebugThreadInfo>> GetThreadsAsync(
        CancellationToken cancellationToken) =>
        InvokeAsync(
            () =>
            {
                RequireOpen();
                return (IReadOnlyList<DebugThreadInfo>)[.. _threads.Select(CreateThreadInfo)];
            },
            cancellationToken);

    /// <inheritdoc />
    public Task<DebugStackTrace> GetStackAsync(
        DebugStackRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return InvokeAsync(() => GetStack(request), cancellationToken);
    }

    /// <inheritdoc />
    public Task<DebugModulePage> GetModulesAsync(
        DebugModulesRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return InvokeAsync(
            () =>
            {
                RequireOpen();
                ValidatePage(request.StartModule, request.ModuleCount, "module");
                int count = request.ModuleCount == 0
                    ? _modules.Count - request.StartModule
                    : Math.Min(request.ModuleCount, _modules.Count - request.StartModule);
                IReadOnlyList<DebugModuleInfo> page = count <= 0
                    ? []
                    : [.. _modules.Skip(request.StartModule).Take(count)];
                return new DebugModulePage(page, _modules.Count);
            },
            cancellationToken);
    }

    private DebugStackTrace GetStack(DebugStackRequest request)
    {
        RequireOpen();
        ValidatePage(request.StartFrame, request.Levels, "frame");
        DumpThread thread = _threads.FirstOrDefault(item => item.Id == request.ThreadId) ??
            throw new ArgumentOutOfRangeException(
                nameof(request),
                $"Thread {request.ThreadId} does not exist in this dump session.");
        if (!_framesByThread.TryGetValue(thread.Id, out IReadOnlyList<DumpStackFrame>? frames))
        {
            ClrStackFrame[] runtimeFrames =
                [.. thread.Thread.EnumerateStackTrace(includeContext: false, MaximumFrames + 1)];
            if (runtimeFrames.Length > MaximumFrames)
            {
                throw new InvalidDataException(
                    $"The dump thread exceeds the stack-frame limit of {MaximumFrames}.");
            }

            var materialized = new List<DumpStackFrame>(runtimeFrames.Length);
            foreach (ClrStackFrame runtimeFrame in runtimeFrames)
            {
                var frame = new DumpStackFrame(_nextFrameId++, runtimeFrame);
                materialized.Add(frame);
                _framesById.Add(frame.Id, frame);
            }

            frames = materialized;
            _framesByThread.Add(thread.Id, frames);
        }

        int count = request.Levels == 0
            ? frames.Count - request.StartFrame
            : Math.Min(request.Levels, frames.Count - request.StartFrame);
        IReadOnlyList<DebugStackFrameInfo> page = count <= 0
            ? []
            : [.. frames.Skip(request.StartFrame).Take(count).Select(CreateFrameInfo)];
        return new DebugStackTrace(page, frames.Count);
    }

    private static IReadOnlyList<DumpThread> CreateThreads(ClrRuntime runtime)
    {
        ClrThread[] runtimeThreads = [.. runtime.Threads.Where(static item => item.IsAlive)];
        if (runtimeThreads.Length > MaximumThreads)
        {
            throw new InvalidDataException(
                $"The dump exceeds the managed-thread limit of {MaximumThreads}.");
        }

        return
        [
            .. runtimeThreads
                .OrderBy(static item => item.OSThreadId)
                .ThenBy(static item => item.ManagedThreadId)
                .Select(static (item, index) => new DumpThread(index + 1, item))
        ];
    }

    private static IReadOnlyList<DebugModuleInfo> CreateModules(ClrRuntime runtime)
    {
        ClrModule[] runtimeModules = [.. runtime.EnumerateModules().Take(MaximumModules + 1)];
        if (runtimeModules.Length > MaximumModules)
        {
            throw new InvalidDataException(
                $"The dump exceeds the managed-module limit of {MaximumModules}.");
        }

        return
        [
            .. runtimeModules.Select(static (module, index) =>
            {
                string? path = string.IsNullOrWhiteSpace(module.Name)
                    ? null
                    : module.Name;
                string name = path is null
                    ? BoundName(module.AssemblyName, $"module {index + 1}")
                    : BoundName(Path.GetFileName(path), $"module {index + 1}");
                return new DebugModuleInfo(
                    index + 1,
                    name,
                    path,
                    DebugModuleSymbolKind.None,
                    null,
                    null,
                    null,
                    null,
                    null);
            })
        ];
    }

    private static DebugThreadInfo CreateThreadInfo(DumpThread thread)
    {
        string name = thread.Thread.ManagedThreadId > 0
            ? $"Managed thread {thread.Thread.ManagedThreadId} (OS {thread.Thread.OSThreadId})"
            : $"OS thread {thread.Thread.OSThreadId}";
        return new DebugThreadInfo(thread.Id, name);
    }

    private static DebugStackFrameInfo CreateFrameInfo(DumpStackFrame frame) => new(
        frame.Id,
        BoundName(frame.Frame.ToString(), "[Unknown Frame]"),
        null,
        0,
        0,
        null);

    private static void ValidatePage(int start, int count, string description)
    {
        if (start < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(start),
                $"The {description} page start must not be negative.");
        }

        if (count < 0 || count > MaximumFrames)
        {
            throw new ArgumentOutOfRangeException(
                nameof(count),
                $"The {description} page count must be between zero and {MaximumFrames}.");
        }
    }
}
