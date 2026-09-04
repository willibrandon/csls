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
    internal static IReadOnlyDictionary<int, ManagedSymbolVariable> GetArguments(
        ManagedFrameHandle frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        using PEReader? peReader = frame.OpenPeReader();
        if (peReader is null)
        {
            return new Dictionary<int, ManagedSymbolVariable>();
        }

        MetadataReader metadata = peReader.GetMetadataReader();
        MethodDefinition method = GetMethod(metadata, frame.MethodToken);
        bool hasThis = (method.Attributes & MethodAttributes.Static) == 0;
        Dictionary<int, ManagedSymbolVariable> result = [];
        if (hasThis)
        {
            result[0] = new ManagedSymbolVariable("this", TupleCustomTypeInfo: null);
        }

        foreach (Parameter parameter in method.GetParameters().Select(metadata.GetParameter))
        {
            if (parameter.SequenceNumber == 0)
            {
                continue;
            }

            int runtimeIndex = checked(parameter.SequenceNumber - (hasThis ? 0 : 1));
            string name = metadata.GetString(parameter.Name);
            result[runtimeIndex] = new ManagedSymbolVariable(
                string.IsNullOrEmpty(name) ? $"argument {runtimeIndex}" : name,
                ManagedTupleElementNameReader.ReadAttribute(
                    metadata,
                    parameter.GetCustomAttributes()));
        }

        return result;
    }

    /// <summary>
    /// Resolves active symbol local slots to source names.
    /// </summary>
    /// <param name="frame">The generation-bound managed frame and symbol snapshot.</param>
    /// <returns>Local names keyed by their ICorDebug local slot.</returns>
    internal static IReadOnlyDictionary<int, ManagedSymbolVariable> GetLocals(
        ManagedFrameHandle frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        using DebugSymbolReader? symbols = frame.OpenSymbols();
        if (symbols is null)
        {
            return new Dictionary<int, ManagedSymbolVariable>();
        }

        return symbols.GetLocalVariables(frame.MethodToken, frame.IlOffset);
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
