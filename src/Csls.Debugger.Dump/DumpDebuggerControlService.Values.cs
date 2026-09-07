using Csls.Debugger.Contracts;
using Microsoft.Diagnostics.Runtime;

namespace Csls.Debugger.Dump;

/// <summary>
/// Recovers captured managed frame scopes through the public CoreCLR virtual-process API.
/// </summary>
public sealed partial class DumpDebuggerControlService
{
    /// <inheritdoc />
    public Task<IReadOnlyList<DebugScopeInfo>> GetScopesAsync(DebugScopesRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return InvokeAsync<IReadOnlyList<DebugScopeInfo>>(() =>
        {
            RequireOpen();
            if (!_framesById.TryGetValue(request.FrameId, out DumpStackFrame? frame))
            {
                throw new ArgumentException("The requested frame does not belong to this dump session.", nameof(request));
            }

            if (frame.Frame.Kind != ClrStackFrameKind.ManagedMethod || frame.Frame.Method is null)
            {
                return [];
            }

            return
            [
                new DebugScopeInfo("Arguments", checked(frame.Id * 2), false),
                new DebugScopeInfo("Locals", checked(frame.Id * 2 + 1), false)
            ];
        }, cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<DebugVariableInfo>> GetVariablesAsync(DebugVariablesRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return InvokeAsync(() =>
        {
            RequireOpen();
            ValidatePage(request.Start, request.Count, "variable");
            if (!Enum.IsDefined(request.Filter))
            {
                throw new ArgumentOutOfRangeException(nameof(request), "The variable category is invalid.");
            }

            if (request.AllowTargetCodeExecution)
            {
                throw CreateReadOnlyException("target-code execution");
            }

            if (_values.Contains(request.VariablesReference))
            {
                return ReadCaptured(process => process.ReadChildren(request, cancellationToken), cancellationToken);
            }

            int frameId = request.VariablesReference / 2;
            if (request.VariablesReference < 2 || !_framesById.TryGetValue(frameId, out DumpStackFrame? frame) ||
                frame.Frame.Kind != ClrStackFrameKind.ManagedMethod || frame.Frame.Method is not { } method ||
                frame.Frame.Thread is not { } thread)
            {
                throw new ArgumentException("The variable container does not belong to a captured managed frame.", nameof(request));
            }

            if (request.Filter == DebugVariableFilter.Indexed)
            {
                return [];
            }

            ClrRuntime runtime = _runtime ?? throw new InvalidOperationException("The dump is closed.");
            CorDebugDumpFrameInfo captured = ReadCaptured(process => process.ReadFrame(thread.OSThreadId, frame.Frame.StackPointer,
                    unchecked((uint)method.MetadataToken), request.VariablesReference % 2 == 0,
                    request.Start, request.Count, cancellationToken, request.DumpReadProgress), cancellationToken);
            IReadOnlyDictionary<int, string> names = ReadVariableNames(runtime, method, captured,
                request.VariablesReference % 2 == 0, cancellationToken);
            return captured.Values.Select((value, index) =>
                names.TryGetValue(checked(request.Start + index), out string? name) && !string.IsNullOrEmpty(name)
                    ? value with { Name = BoundName(name, value.Name) }
                    : value).ToArray();
        }, cancellationToken);
    }

    private T ReadCaptured<T>(Func<CorDebugDumpProcess, T> read, CancellationToken cancellationToken)
    {
        ClrRuntime runtime = _runtime ?? throw new InvalidOperationException("The dump is closed.");
        _corDebug ??= new CorDebugDumpProcess(new DumpCorDebugSource(runtime.ClrInfo, _dacPath, _binarySearchPaths, _memoryFilter),
            runtime.ClrInfo.ModuleInfo.ImageBase, DescribeCapturedModule, new DumpCorDebugHeap(runtime),
            _values, cancellationToken);
        try
        {
            return read(_corDebug);
        }
        catch
        {
            _corDebug.Dispose();
            _corDebug = null;
            throw;
        }
    }

    private CorDebugDumpModuleInfo DescribeCapturedModule(ulong address, CancellationToken cancellationToken)
    {
        ClrRuntime runtime = _runtime ?? throw new InvalidOperationException("The dump is closed.");
        int count = 0;
        foreach (ClrModule module in runtime.EnumerateModules())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++count > MaximumModules)
            {
                throw new InvalidDataException("Captured metadata exceeds the module inspection limit.");
            }
            if (module.ImageBase != address)
            {
                continue;
            }
            ModuleInfo? recorded = runtime.DataTarget.EnumerateModules().Take(MaximumModules)
                .FirstOrDefault(candidate => candidate.ImageBase == address);
            return new CorDebugDumpModuleInfo(address, module.Size, module.Layout != ModuleLayout.Flat,
                module.Name ?? recorded?.FileName ?? "captured-module",
                unchecked((uint)(recorded?.IndexTimeStamp ?? 0)),
                checked((uint)(recorded?.IndexFileSize ?? 0)));
        }
        throw new InvalidDataException("The runtime type belongs to an unknown captured module.");
    }

    private IReadOnlyDictionary<int, string> ReadVariableNames(ClrRuntime runtime, ClrMethod method,
        CorDebugDumpFrameInfo captured, bool arguments, CancellationToken cancellationToken)
    {
        ClrModule? module = method.Type?.Module;
        if (module is null || module.ImageBase == 0 || module.Size is 0 or > 512 * 1024 * 1024)
        {
            return new Dictionary<int, string>();
        }

        try
        {
            using var image = new DumpMemoryStream(runtime.DataTarget.DataReader, module.ImageBase, (long)module.Size);
            return CapturedModuleVariableNames.Read(image, module.Layout != ModuleLayout.Flat,
                module.Name ?? module.AssemblyName ?? "captured-module", captured.MethodToken, captured.IlOffset, arguments,
                _binarySearchPaths);
        }
        catch (Exception exception) when (exception is IOException or BadImageFormatException or OverflowException)
        {
            // Read-only image sections may be omitted even when the dump retains the PE headers and debug directory.
            return ReadModuleVariableNames(runtime, module, captured, arguments, cancellationToken);
        }
    }

    private IReadOnlyDictionary<int, string> ReadModuleVariableNames(ClrRuntime runtime, ClrModule module,
        CorDebugDumpFrameInfo captured, bool arguments, CancellationToken cancellationToken)
    {
        try
        {
            ModuleInfo? recorded = runtime.DataTarget.EnumerateModules().Take(MaximumModules)
                .FirstOrDefault(candidate => candidate.ImageBase == module.ImageBase);
            if (recorded is null || recorded.IndexFileSize <= 0)
            {
                return new Dictionary<int, string>();
            }
            CorDebugDumpProcess process = _corDebug ?? throw new InvalidOperationException("No captured frame has been read.");
            return process.ReadModuleVariableNames(module.Name ?? recorded.FileName,
                unchecked((uint)recorded.IndexTimeStamp), checked((uint)recorded.IndexFileSize), captured.MethodToken,
                captured.IlOffset, arguments, _binarySearchPaths, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or BadImageFormatException or OverflowException)
        {
            return new Dictionary<int, string>();
        }
    }
}
