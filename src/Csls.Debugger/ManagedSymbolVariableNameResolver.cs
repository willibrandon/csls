using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace Csls.Debugger;

/// <summary>
/// Resolves argument and active-local names from PE metadata and managed symbols.
/// </summary>
internal static class ManagedSymbolVariableNameResolver
{
    /// <summary>
    /// Resolves runtime argument indexes to source parameter names.
    /// </summary>
    /// <param name="frame">The generation-bound managed frame and module snapshot.</param>
    /// <returns>Argument names keyed by their ICorDebug argument index.</returns>
    internal static IReadOnlyDictionary<int, string> GetArguments(ManagedFrameHandle frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        using PEReader? peReader = frame.OpenPeReader();
        if (peReader is null)
        {
            return new Dictionary<int, string>();
        }

        MetadataReader metadata = peReader.GetMetadataReader();
        MethodDefinition method = GetMethod(metadata, frame.MethodToken);
        bool hasThis = (method.Attributes & MethodAttributes.Static) == 0;
        Dictionary<int, string> result = [];
        if (hasThis)
        {
            result[0] = "this";
        }

        foreach (ParameterHandle parameterHandle in method.GetParameters())
        {
            Parameter parameter = metadata.GetParameter(parameterHandle);
            if (parameter.SequenceNumber == 0)
            {
                continue;
            }

            int runtimeIndex = checked(parameter.SequenceNumber - (hasThis ? 0 : 1));
            string name = metadata.GetString(parameter.Name);
            result[runtimeIndex] = string.IsNullOrEmpty(name)
                ? $"argument {runtimeIndex}"
                : name;
        }

        return result;
    }

    /// <summary>
    /// Resolves active symbol local slots to source names.
    /// </summary>
    /// <param name="frame">The generation-bound managed frame and symbol snapshot.</param>
    /// <returns>Local names keyed by their ICorDebug local slot.</returns>
    internal static IReadOnlyDictionary<int, string> GetLocals(ManagedFrameHandle frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        using DebugSymbolReader? symbols = frame.OpenSymbols();
        if (symbols is null)
        {
            return new Dictionary<int, string>();
        }

        return symbols.GetLocalNames(frame.MethodToken, frame.IlOffset);
    }

    private static MethodDefinition GetMethod(MetadataReader metadata, uint methodToken)
    {
        int rowNumber = checked((int)(methodToken & 0x00ffffff));
        if (rowNumber == 0 || rowNumber > metadata.MethodDefinitions.Count)
        {
            throw new BadImageFormatException(
                $"Method token 0x{methodToken:X8} is outside the module metadata.");
        }

        return metadata.GetMethodDefinition(MetadataTokens.MethodDefinitionHandle(rowNumber));
    }
}
