using Csls.Debugger.Contracts;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace Csls.Debugger;

/// <summary>
/// Discovers generation-bound managed calls for source-aware Step Into.
/// </summary>
internal sealed partial class CorDebugDebuggee
{
    private const int MaximumStepTargetCount = 256;

    /// <summary>
    /// Gets selectable call instructions in the current source statement.
    /// </summary>
    /// <param name="frameId">The generation-bound active frame identifier.</param>
    /// <param name="generation">The current debugger stop generation.</param>
    /// <returns>The ordered selectable calls.</returns>
    internal IReadOnlyList<DebugStepTargetInfo> GetStepTargets(
        int frameId,
        DebugStopGeneration generation)
    {
        ManagedFrameHandle frame = GetFrame(frameId, generation);
        ValidateActiveFrame(frame);
        if ((frame.ModulePath is null && frame.ModuleImage is null) || frame.MethodToken == 0)
        {
            return [];
        }

        using PEReader? peReader = frame.OpenPeReader();
        if (peReader is null)
        {
            return [];
        }

        MetadataReader metadata = peReader.GetMetadataReader();
        MethodDefinition method = GetMethodDefinition(metadata, frame.MethodToken);
        if (method.RelativeVirtualAddress == 0)
        {
            return [];
        }

        byte[] bytes = peReader.GetMethodBody(method.RelativeVirtualAddress).GetILBytes() ?? [];
        IReadOnlyList<ManagedSequencePoint> points = PortablePdbMethodMap.Read(
            frame,
            frame.MethodToken);
        ManagedSequencePoint? current = points.LastOrDefault(
            point => point.IlOffset <= frame.IlOffset);
        if (current is null)
        {
            return [];
        }

        int endOffset = points.FirstOrDefault(point => point.IlOffset > current.IlOffset)
            ?.IlOffset ?? bytes.Length;
        _stepTargets.Clear();
        var result = new List<DebugStepTargetInfo>();
        var calleeOccurrences = new Dictionary<uint, int>();
        foreach (ManagedIlInstruction instruction in ManagedIlDecoder.Decode(bytes))
        {
            if (instruction.Offset < frame.IlOffset || instruction.Offset >= endOffset ||
                !IsCallInstruction(instruction.Name) ||
                !TryGetMethodDefinitionToken(instruction, out uint calleeToken))
            {
                continue;
            }

            IReadOnlyList<ManagedSequencePoint> calleePoints = PortablePdbMethodMap.Read(
                frame,
                calleeToken);
            if (calleePoints.Count == 0)
            {
                continue;
            }

            ManagedSequencePoint calleeEntry = calleePoints[0];
            int hitsToSkip = calleeOccurrences.GetValueOrDefault(calleeToken);
            calleeOccurrences[calleeToken] = checked(hitsToSkip + 1);
            int id = checked(++_nextStepTargetId);
            string label = ManagedMetadataNameResolver.Resolve(
                metadata,
                checked((int)calleeToken)) ?? instruction.Operand;
            var info = new DebugStepTargetInfo(
                id,
                label,
                current.StartLine,
                current.StartColumn,
                current.EndLine,
                current.EndColumn);
            _stepTargets.Add(id, new ManagedStepTargetHandle
            {
                Generation = generation,
                FrameId = frame.Id,
                ThreadId = frame.ThreadId,
                StartIlOffset = frame.IlOffset,
                EndIlOffset = checked((uint)endOffset),
                CalleeMethodToken = calleeToken,
                CalleeEntryIlOffset = checked((uint)calleeEntry.IlOffset),
                HitsToSkip = hitsToSkip
            });
            result.Add(info);
            if (result.Count == MaximumStepTargetCount)
            {
                break;
            }
        }

        return result;
    }

    private static MethodDefinition GetMethodDefinition(
        MetadataReader metadata,
        uint methodToken)
    {
        int rowNumber = checked((int)(methodToken & 0x00ffffff));
        if (rowNumber == 0 || rowNumber > metadata.MethodDefinitions.Count)
        {
            throw new BadImageFormatException(
                $"Method token 0x{methodToken:X8} is outside the module metadata.");
        }

        return metadata.GetMethodDefinition(MetadataTokens.MethodDefinitionHandle(rowNumber));
    }

    private static bool IsCallInstruction(string name) => name is "call" or "callvirt" or "newobj" or "calli";

    private static bool TryGetMethodDefinitionToken(
        ManagedIlInstruction instruction,
        out uint methodToken)
    {
        methodToken = 0;
        if (instruction.MetadataToken is not int token ||
            MetadataTokens.Handle(token).Kind != HandleKind.MethodDefinition)
        {
            return false;
        }

        methodToken = checked((uint)token);
        return true;
    }

    private static void ValidateActiveFrame(ManagedFrameHandle frame)
    {
        if (frame.FrameIndex != 0)
        {
            throw new InvalidOperationException(
                "Step Into targets are available only for a thread's active managed frame.");
        }
    }
}
