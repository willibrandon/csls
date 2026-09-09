using Csls.Debugger.Contracts;
using Csls.Debugger.Interop;
using System.Reflection.PortableExecutable;

namespace Csls.Debugger;

/// <summary>
/// Reacquires captured storage paths for bounded array and physical object-field inspection.
/// </summary>
internal sealed unsafe class CorDebugDumpValueReader
{
    private readonly CorDebugDumpValues _values;
    private readonly CorDebugDumpCallbacks _callbacks;
    private readonly ManagedRuntimeTypeFormatter _types;
    private readonly CorDebugDumpStorageReader _storage;
    private readonly CorDebugDumpArrayReader _arrays;
    private readonly CorDebugDumpObjectReader _objects;

    /// <summary>
    /// Creates captured value inspection over session-owned paths and request-owned exact types.
    /// </summary>
    internal CorDebugDumpValueReader(CorDebugDumpValues values, ICorDebugDumpSource source, ICorDebugDumpHeap heap,
        CorDebugDumpCallbacks callbacks, Func<nint, PEReader> openModule, Func<nint> getProcess)
    {
        _values = values;
        _callbacks = callbacks;
        _types = new ManagedRuntimeTypeFormatter(openModule);
        _storage = new CorDebugDumpStorageReader(source, heap, callbacks, getProcess, new ManagedCapturedTypeResolver(openModule));
        _arrays = new CorDebugDumpArrayReader(callbacks, _storage, _types, Describe);
        _objects = new CorDebugDumpObjectReader(new ManagedInstanceFieldReader(openModule), callbacks, _storage, Describe);
    }

    /// <summary>
    /// Describes a captured frame value and publishes a logical handle for its physical children.
    /// </summary>
    internal DebugVariableInfo Describe(nint value, string name, CorDebugDumpValuePath path, bool indexed = false)
    {
        string type = string.Empty;
        long missingMemory = _callbacks.MissingMemoryReads;
        try
        {
            type = _types.FormatValueType(value);
            using CorDebugDumpStorage storage = _storage.FromValue(value);
            if (storage.Address == 0 && storage.Size != 0)
            {
                ManagedValueDisplay immediate = CorDebugValueFormatter.Format(value);
                return new DebugVariableInfo(name, immediate.Value, type, 0, null, null, IsIndexed: indexed);
            }
            return Describe(storage, name, path, indexed);
        }
        catch (InvalidOperationException exception) when (IsUnavailable(exception) ||
            _callbacks.IsMissingMemoryFailure(exception, missingMemory))
        {
            return Unavailable(name, exception, indexed) with { Type = type };
        }
        catch (CorDebugDumpStorageUnavailableException exception)
        {
            return Unavailable(name, exception, indexed) with { Type = type };
        }
    }

    private DebugVariableInfo Describe(CorDebugDumpStorage value, string name, CorDebugDumpValuePath path, bool indexed)
    {
        string type = _types.Format(value.Type, 0, null, out uint kind, out uint? intrinsic);
        if (value.Address == 0 && value.Size == 0)
        {
            return new DebugVariableInfo(name, "null", type, 0, null, null, IsIndexed: indexed);
        }
        if (kind is 0x14 or 0x1d)
        {
            int length = _storage.GetArrayCount(value, _storage.GetArrayLayout(value));
            return new DebugVariableInfo(name, $"{{Length = {length}}}", type,
                length == 0 ? 0 : _values.Retain(path, value), null, null,
                NamedVariables: 0, IndexedVariables: length, IsIndexed: indexed);
        }
        if (type == "string")
        {
            return new DebugVariableInfo(name, _storage.ReadString(value), type, 0, null, null, IsIndexed: indexed);
        }
        uint primitive = intrinsic ?? kind;
        int size = primitive switch
        {
            0x02 or 0x04 or 0x05 => 1,
            0x03 or 0x06 or 0x07 => 2,
            0x08 or 0x09 or 0x0c => 4,
            0x0a or 0x0b or 0x0d => 8,
            0x18 or 0x19 => IntPtr.Size,
            _ => 0
        };
        if (size != 0)
        {
            Span<byte> bytes = stackalloc byte[size];
            _storage.Read(value, 0, bytes);
            return new DebugVariableInfo(name, CorDebugValueFormatter.FormatPrimitive(primitive, bytes), type,
                0, null, null, IsIndexed: indexed);
        }
        int fields = _objects.GetCount(value);
        return new DebugVariableInfo(name, "{...}", type, fields == 0 ? 0 : _values.Retain(path, value), null, null,
            NamedVariables: fields, IndexedVariables: 0, IsIndexed: indexed);
    }

    /// <summary>
    /// Reads a child page from retained storage or its nearest captured ancestor and exact inline selections.
    /// </summary>
    internal IReadOnlyList<DebugVariableInfo> Read(nint frame, CorDebugDumpValuePath path,
        int reference, int start, int count, DebugVariableFilter filter, CancellationToken cancellationToken)
    {
        List<CorDebugDumpValuePath> selections = [];
        CorDebugDumpValuePath root = path;
        CorDebugDumpStorageLocation? location = _values.GetStorage(reference);
        while (location is null && root.ParentId != 0)
        {
            selections.Add(root);
            location = _values.GetStorage(root.ParentId);
            root = _values.Get(root.ParentId);
        }
        nint native = 0;
        using var storage = new DisposableCollection<CorDebugDumpStorage>();
        try
        {
            CorDebugDumpStorage value;
            if (location is not null)
            {
                value = storage.Acquire(() => _storage.FromLocation(location));
            }
            else
            {
                var api = new ICorDebugILFrameAbi(frame);
                int result = root.Arguments ? api.GetArgument((uint)root.Slot, (nint)(&native))
                    : api.GetLocalVariable((uint)root.Slot, (nint)(&native));
                native = Volatile.Read(ref native);
                CorDebugHResult.ThrowIfFailed(result, "ICorDebugILFrame.GetValue");
                nint rootValue = native;
                value = storage.Acquire(() => _storage.FromValue(rootValue));
            }
            for (int index = selections.Count - 1; index >= 0; index--)
            {
                cancellationToken.ThrowIfCancellationRequested();
                CorDebugDumpValuePath selection = selections[index];
                value = storage.Acquire(() => selection.Field is { } field
                    ? _objects.ReadField(value, field, cancellationToken)
                    : _storage.GetElement(value, selection.ElementIndex));
            }
            _ = _types.Format(value.Type, 0, null, out uint kind, out _);
            return kind is 0x14 or 0x1d
                ? filter == DebugVariableFilter.Named ? [] : _arrays.ReadPage(value, path, reference, start, count, cancellationToken)
                : filter == DebugVariableFilter.Indexed ? [] : _objects.ReadPage(value, path, reference, start, count, cancellationToken);
        }
        finally
        {
            if (native != 0)
            {
                _ = ComAbi.Release(native);
            }
        }
    }

    /// <summary>
    /// Identifies runtime errors caused by missing captured storage or optimized values.
    /// </summary>
    internal static bool IsUnavailable(InvalidOperationException exception) => exception.InnerException?.HResult is
        unchecked((int)0x80131304) or unchecked((int)0x80131305) or unchecked((int)0x80131c49);

    /// <summary>
    /// Projects unavailable captured storage without inventing a value or expansion handle.
    /// </summary>
    internal static DebugVariableInfo Unavailable(string name, Exception exception, bool indexed = false) =>
        new(name, $"Captured value unavailable: {exception.Message}", string.Empty, 0, null, null,
            DebugVariablePresentationKind.Unavailable, IsIndexed: indexed);
}
