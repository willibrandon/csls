using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace Csls.Debugger;

/// <summary>
/// Validates managed-IL breakpoint locations against exact instruction boundaries.
/// </summary>
internal sealed partial class CorDebugDebuggee
{
    private static void ValidateInstructionBoundary(ManagedFrameHandle frame, uint ilOffset)
    {
        if (frame.ModulePath is null || frame.MethodToken == 0)
        {
            throw new InvalidOperationException("The selected frame has no managed IL body.");
        }

        using FileStream stream = File.OpenRead(frame.ModulePath);
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
        if (!ManagedIlDecoder.Decode(bytes).Any(instruction => instruction.Offset == ilOffset))
        {
            throw new ArgumentException(
                $"Managed-IL offset 0x{ilOffset:X} is not an instruction boundary.");
        }
    }
}
