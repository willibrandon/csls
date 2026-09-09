using Microsoft.Diagnostics.Runtime;
using Microsoft.Diagnostics.Runtime.DataReaders.Implementation;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Csls.Debugger.Dump;

/// <summary>
/// Associates Apple core-file register contexts with their recorded operating-system thread identifiers.
/// </summary>
internal sealed class DumpMachODataReader : IDataReader, IThreadReader, IDisposable
{
    private readonly DataTarget _owner;
    private readonly IReadOnlyDictionary<uint, uint> _readerThreadIds;
    private readonly IReadOnlyDictionary<uint, byte[]> _nativeContexts;

    private DumpMachODataReader(DataTarget owner, IReadOnlyDictionary<uint, uint> readerThreadIds,
        IReadOnlyDictionary<uint, byte[]> nativeContexts)
    {
        _owner = owner;
        _readerThreadIds = readerThreadIds;
        _nativeContexts = nativeContexts;
    }

    /// <summary>
    /// Opens a captured target and applies the native Mach-O thread metadata before runtime inspection.
    /// </summary>
    /// <param name="path">The absolute captured process file.</param>
    /// <param name="options">The caller's local image and memory-reader policy.</param>
    /// <param name="cancellationToken">Cancels bounded metadata traversal.</param>
    /// <returns>The owned captured target.</returns>
    internal static DataTarget Open(string path, DataTargetOptions options, CancellationToken cancellationToken)
    {
        DataTarget target = DumpDataTargetLoader.Open(path, options, cancellationToken);
        try
        {
            if (target.DataReader.TargetPlatform != OSPlatform.OSX)
            {
                return target;
            }

            (IReadOnlyDictionary<uint, uint>? ordinals, IReadOnlyDictionary<uint, byte[]> contexts) =
                ReadThreadMetadata(path, cancellationToken);
            if ((ordinals is null || ordinals.Count == 0) && contexts.Count == 0)
            {
                return target;
            }

            if (target.DataReader is not IThreadReader threads)
            {
                throw new InvalidDataException("The Mach-O reader does not expose its captured thread identities.");
            }
            uint[] readerIds = [.. threads.EnumerateOSThreadIds().Take(4097)];
            if (readerIds.Length > 4096)
            {
                throw new InvalidDataException("The Mach-O core exceeds the 4096-thread limit.");
            }
            bool ordinalIds = readerIds.Order().SequenceEqual(Enumerable.Range(0, readerIds.Length).Select(static id => (uint)id));
            if (ordinals is null || ordinals.Count == 0)
            {
                if (!ordinalIds)
                {
                    throw new InvalidDataException("The Mach-O core cannot associate native register records with its thread identities.");
                }
                ordinals = readerIds.ToDictionary(static id => id);
            }
            bool recordedIds = readerIds.ToHashSet().SetEquals(ordinals.Keys);
            if ((!ordinalIds && !recordedIds) || ordinals.Values.Any(ordinal => ordinal >= readerIds.Length))
            {
                throw new InvalidDataException("The Mach-O thread metadata does not match the captured register contexts.");
            }
            if (recordedIds && contexts.Count == 0)
            {
                return target;
            }
            var nativeContexts = new Dictionary<uint, byte[]>();
            foreach ((uint threadId, uint ordinal) in ordinals)
            {
                if (contexts.TryGetValue(ordinal, out byte[]? context))
                {
                    nativeContexts.Add(threadId, context);
                }
            }
            IReadOnlyDictionary<uint, uint> readerThreadIds = recordedIds
                ? ordinals.Keys.ToDictionary(static id => id) : ordinals;
            using var reader = new DumpDataReaderLease(() => new DumpMachODataReader(target, readerThreadIds, nativeContexts));
            return reader.CreateTarget(options);
        }
        catch
        {
            target.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Reads the bounded process-metadata note and maps each recorded thread identifier to its register-context ordinal.
    /// </summary>
    /// <param name="path">The original Mach-O core file.</param>
    /// <param name="cancellationToken">Cancels file and JSON traversal.</param>
    /// <returns>The recorded thread mapping, or null when the file contains no process-metadata note.</returns>
    internal static IReadOnlyDictionary<uint, uint>? ReadThreadOrdinals(string path, CancellationToken cancellationToken) =>
        ReadThreadMetadata(path, cancellationToken).Ordinals;

    private static (IReadOnlyDictionary<uint, uint>? Ordinals, IReadOnlyDictionary<uint, byte[]> Contexts) ReadThreadMetadata(
        string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using FileStream stream = File.OpenRead(path);
        Span<byte> header = stackalloc byte[32];
        stream.ReadExactly(header);
        uint cpu = BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
        uint commands = BinaryPrimitives.ReadUInt32LittleEndian(header[16..]);
        uint bytes = BinaryPrimitives.ReadUInt32LittleEndian(header[20..]);
        if (BinaryPrimitives.ReadUInt32LittleEndian(header) != 0xfeedfacf ||
            cpu is not (0x01000007 or 0x0100000c) || BinaryPrimitives.ReadUInt32LittleEndian(header[12..]) != 4 ||
            commands > 65536 || bytes > 16 * 1024 * 1024 || commands > bytes / 8 || stream.Length - 32 < bytes)
        {
            throw new InvalidDataException("The dump has an invalid or oversized Mach-O command table.");
        }

        long end = 32L + bytes;
        long position = 32;
        int threads = 0;
        var contexts = new Dictionary<uint, byte[]>();
        byte[]? metadata = null;
        Span<byte> command = stackalloc byte[40];
        for (uint index = 0; index < commands; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (end - position < 8)
            {
                throw new InvalidDataException("The Mach-O load command header exceeds its recorded table.");
            }
            stream.Position = position;
            stream.ReadExactly(command[..8]);
            uint kind = BinaryPrimitives.ReadUInt32LittleEndian(command);
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(command[4..]);
            uint alignment = kind == 4 ? 4u : 8u;
            if (size < 8 || size % alignment != 0 || size > end - position)
            {
                throw new InvalidDataException("The Mach-O load command has an invalid size.");
            }
            if (kind == 4)
            {
                if (++threads > 4096)
                {
                    throw new InvalidDataException("The Mach-O core exceeds the 4096-thread limit.");
                }
                byte[]? context = DumpMachOThreadContexts.Read(stream, cpu, position + 8, position + size, cancellationToken);
                if (context is not null)
                {
                    contexts.Add((uint)(threads - 1), context);
                }
            }
            if (kind == 0x31)
            {
                if (size != command.Length)
                {
                    throw new InvalidDataException("The Mach-O note command has an invalid size.");
                }
                stream.ReadExactly(command[8..]);
                if (command[8..24].SequenceEqual("process metadata"u8))
                {
                    if (metadata is not null)
                    {
                        throw new InvalidDataException("The Mach-O core contains multiple process-metadata notes.");
                    }
                    ulong offset = BinaryPrimitives.ReadUInt64LittleEndian(command[24..]);
                    ulong length = BinaryPrimitives.ReadUInt64LittleEndian(command[32..]);
                    if (length is 0 or > 1024 * 1024 || offset < (ulong)end || offset > (ulong)stream.Length ||
                        length > (ulong)stream.Length - offset)
                    {
                        throw new InvalidDataException("The Mach-O process metadata exceeds its file range or size limit.");
                    }
                    metadata = new byte[checked((int)length)];
                    stream.Position = checked((long)offset);
                    stream.ReadExactly(metadata);
                }
            }
            position += size;
        }
        if (position != end)
        {
            throw new InvalidDataException("The Mach-O load commands do not fill their recorded table.");
        }
        return (metadata is null ? null : ParseThreadOrdinals(metadata, threads, cancellationToken), contexts);
    }

    private static Dictionary<uint, uint> ParseThreadOrdinals(byte[] metadata, int threadCount,
        CancellationToken cancellationToken)
    {
        try
        {
            using var document = JsonDocument.Parse(metadata, new JsonDocumentOptions { MaxDepth = 16 });
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("The Mach-O process metadata must be a JSON object.");
            }
            JsonElement threads = default;
            foreach (JsonProperty property in root.EnumerateObject())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (property.NameEquals("threads"))
                {
                    if (threads.ValueKind != JsonValueKind.Undefined)
                    {
                        throw new InvalidDataException("The Mach-O process metadata repeats its thread table.");
                    }
                    threads = property.Value;
                }
            }
            if (threads.ValueKind == JsonValueKind.Undefined)
            {
                return [];
            }
            if (threads.ValueKind != JsonValueKind.Array || threads.GetArrayLength() != threadCount)
            {
                throw new InvalidDataException("The Mach-O thread metadata and register-context counts differ.");
            }
            var ordinals = new Dictionary<uint, uint>();
            uint ordinal = 0;
            foreach (JsonElement thread in threads.EnumerateArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (thread.ValueKind != JsonValueKind.Object)
                {
                    throw new InvalidDataException("The Mach-O thread metadata entry must be a JSON object.");
                }
                bool hasId = false;
                foreach (JsonProperty property in thread.EnumerateObject())
                {
                    if (!property.NameEquals("thread_id"))
                    {
                        continue;
                    }
                    if (hasId || property.Value.ValueKind != JsonValueKind.Number ||
                        !property.Value.TryGetUInt32(out uint id) || id == 0 || !ordinals.TryAdd(id, ordinal))
                    {
                        throw new InvalidDataException("The Mach-O thread identifier is invalid, repeated, or exceeds the runtime's identifier range.");
                    }
                    hasId = true;
                }
                ordinal++;
            }
            return ordinals;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The Mach-O process metadata contains invalid JSON.", exception);
        }
    }

    /// <inheritdoc />
    public string DisplayName => _owner.DataReader.DisplayName;

    /// <inheritdoc />
    public bool IsThreadSafe => _owner.DataReader.IsThreadSafe;

    /// <inheritdoc />
    public OSPlatform TargetPlatform => _owner.DataReader.TargetPlatform;

    /// <inheritdoc />
    public Architecture Architecture => _owner.DataReader.Architecture;

    /// <inheritdoc />
    public int ProcessId => _owner.DataReader.ProcessId;

    /// <inheritdoc />
    public int PointerSize => _owner.DataReader.PointerSize;

    /// <inheritdoc />
    public IEnumerable<ModuleInfo> EnumerateModules() => _owner.DataReader.EnumerateModules();

    /// <inheritdoc />
    public IEnumerable<uint> EnumerateOSThreadIds() => _readerThreadIds.Keys;

    /// <inheritdoc />
    public ulong GetThreadTeb(uint osThreadId) => 0;

    /// <inheritdoc />
    public bool GetThreadContext(uint threadID, uint contextFlags, Span<byte> context)
    {
        if (!_readerThreadIds.TryGetValue(threadID, out uint readerId))
        {
            return false;
        }
        return _nativeContexts.TryGetValue(threadID, out byte[]? captured)
            ? captured.AsSpan().TryCopyTo(context)
            : _owner.DataReader.GetThreadContext(readerId, contextFlags, context);
    }

    /// <inheritdoc />
    public int Read(ulong address, Span<byte> buffer) => _owner.DataReader.Read(address, buffer);

    /// <inheritdoc />
    public bool Read<T>(ulong address, out T value) where T : unmanaged => _owner.DataReader.Read(address, out value);

    /// <inheritdoc />
    public T Read<T>(ulong address) where T : unmanaged => _owner.DataReader.Read<T>(address);

    /// <inheritdoc />
    public bool ReadPointer(ulong address, out ulong value) => _owner.DataReader.ReadPointer(address, out value);

    /// <inheritdoc />
    public ulong ReadPointer(ulong address) => _owner.DataReader.ReadPointer(address);

    /// <inheritdoc />
    public void FlushCachedData() => _owner.DataReader.FlushCachedData();

    /// <inheritdoc />
    public void Dispose() => _owner.Dispose();
}
