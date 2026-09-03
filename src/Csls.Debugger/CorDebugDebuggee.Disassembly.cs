using Csls.Debugger.Contracts;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace Csls.Debugger;

/// <summary>
/// Produces bounded ECMA-335 disassembly for generation-bound managed frames.
/// </summary>
internal sealed partial class CorDebugDebuggee
{
    private const int MaximumDisassemblyInstructionCount = 4096;

    /// <summary>
    /// Disassembles an exact-count managed-IL window around a stopped frame.
    /// </summary>
    /// <param name="request">The opaque frame location and requested window.</param>
    /// <param name="generation">The current debugger stop generation.</param>
    /// <returns>The requested instructions and out-of-range placeholders.</returns>
    internal DebugDisassembly Disassemble(
        DebugDisassemblyRequest request,
        DebugStopGeneration generation)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.InstructionReference);
        ArgumentOutOfRangeException.ThrowIfNegative(request.InstructionCount);
        if (request.InstructionCount > MaximumDisassemblyInstructionCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                $"Disassembly cannot exceed {MaximumDisassemblyInstructionCount} instructions.");
        }

        if (!_instructionFrames.TryGetValue(
            request.InstructionReference,
            out ManagedInstructionReferenceHandle? location))
        {
            throw new InvalidOperationException(
                $"Instruction reference '{request.InstructionReference}' is stale or unknown.");
        }

        ManagedFrameHandle frame = location.Frame;
        ValidateGeneration(frame.Id, frame.Generation, generation);
        if (frame.ModulePath is null || frame.MethodToken == 0)
        {
            throw new InvalidOperationException("The selected frame has no managed IL body.");
        }

        return ReadDisassembly(frame, location.IlOffset, request);
    }

    private DebugDisassembly ReadDisassembly(
        ManagedFrameHandle frame,
        uint baseIlOffset,
        DebugDisassemblyRequest request)
    {
        using FileStream stream = File.OpenRead(frame.ModulePath!);
        using var peReader = new PEReader(stream);
        MetadataReader metadata = peReader.GetMetadataReader();
        int rowNumber = checked((int)(frame.MethodToken & 0x00ffffff));
        if (rowNumber == 0 || rowNumber > metadata.MethodDefinitions.Count)
        {
            throw new BadImageFormatException(
                $"Method token 0x{frame.MethodToken:X8} is outside the module metadata.");
        }

        MethodDefinition method = metadata.GetMethodDefinition(
            MetadataTokens.MethodDefinitionHandle(rowNumber));
        if (method.RelativeVirtualAddress == 0)
        {
            throw new InvalidOperationException("The selected managed method has no IL body.");
        }

        byte[] bytes = peReader.GetMethodBody(method.RelativeVirtualAddress).GetILBytes()
            ?? throw new BadImageFormatException("The selected managed method has no IL bytes.");
        IReadOnlyList<ManagedIlInstruction> decoded = ManagedIlDecoder.Decode(bytes);
        IReadOnlyDictionary<int, ManagedFrameLocation> sources = ManagedIlSourceMap.Read(
            frame.ModulePath!,
            frame.MethodToken,
            frame.Name);
        return SelectInstructions(frame, baseIlOffset, request, metadata, decoded, sources);
    }

    private DebugDisassembly SelectInstructions(
        ManagedFrameHandle frame,
        uint baseIlOffset,
        DebugDisassemblyRequest request,
        MetadataReader metadata,
        IReadOnlyList<ManagedIlInstruction> decoded,
        IReadOnlyDictionary<int, ManagedFrameLocation> sources)
    {
        long byteOffset = checked((long)baseIlOffset + request.ByteOffset);
        int anchor = FindInstruction(decoded, byteOffset);
        long first = checked(anchor + request.InstructionOffset);
        var result = new List<DebugInstructionInfo>(request.InstructionCount);
        for (int index = 0; index < request.InstructionCount; index++)
        {
            long selected = checked(first + index);
            result.Add(selected >= 0 && selected < decoded.Count
                ? CreateInstruction(
                    frame,
                    decoded[checked((int)selected)],
                    metadata,
                    sources,
                    request.ResolveSymbols)
                : CreateInvalidInstruction(frame.Id, byteOffset, selected - first));
        }

        return new DebugDisassembly(result);
    }

    private DebugInstructionInfo CreateInstruction(
        ManagedFrameHandle frame,
        ManagedIlInstruction instruction,
        MetadataReader metadata,
        IReadOnlyDictionary<int, ManagedFrameLocation> sources,
        bool resolveSymbols)
    {
        string operand = instruction.Operand;
        if (resolveSymbols && instruction.MetadataToken is int token &&
            ManagedMetadataNameResolver.Resolve(metadata, token) is string resolved)
        {
            operand = $"{operand} ({resolved})";
        }

        ManagedFrameLocation? location = FindSource(sources, instruction.Offset);
        DebugSourceInfo? source = location?.SourcePath is string sourcePath
            ? _sourceBreakpoints.GetSourceInfo(frame.ModulePath!, sourcePath)
            : null;
        return new DebugInstructionInfo(
            CreateVirtualAddress(frame.Id, instruction.Offset),
            instruction.Bytes,
            string.IsNullOrEmpty(operand)
                ? instruction.Name
                : $"{instruction.Name} {operand}",
            resolveSymbols ? frame.Name : null,
            source,
            location?.Line ?? 0,
            location?.Column ?? 0,
            IsInvalid: false);
    }

    private static DebugInstructionInfo CreateInvalidInstruction(
        int frameId,
        long byteOffset,
        long relativeIndex)
    {
        long candidate = byteOffset + relativeIndex;
        uint offset = candidate <= 0
            ? 0U
            : candidate >= uint.MaxValue
                ? uint.MaxValue
                : checked((uint)candidate);
        return new DebugInstructionInfo(
            CreateVirtualAddress(frameId, offset),
            ReadOnlyMemory<byte>.Empty,
            "<invalid IL address>",
            Symbol: null,
            Source: null,
            Line: 0,
            Column: 0,
            IsInvalid: true);
    }

    private static int FindInstruction(
        IReadOnlyList<ManagedIlInstruction> instructions,
        long offset)
    {
        for (int index = 0; index < instructions.Count; index++)
        {
            if (instructions[index].Offset >= offset)
            {
                return index;
            }
        }

        return instructions.Count;
    }

    private static ManagedFrameLocation? FindSource(
        IReadOnlyDictionary<int, ManagedFrameLocation> sources,
        int offset) => sources
            .Where(pair => pair.Key <= offset)
            .OrderByDescending(static pair => pair.Key)
            .Select(static pair => pair.Value)
            .FirstOrDefault();

    private static ulong CreateVirtualAddress(int frameId, int ilOffset) =>
        CreateVirtualAddress(frameId, checked((uint)ilOffset));

    private static ulong CreateVirtualAddress(int frameId, uint ilOffset) =>
        ((ulong)checked((uint)frameId) << 32) | ilOffset;
}
