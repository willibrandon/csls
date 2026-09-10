using Csls.Debugger.Contracts;
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

        using var metadata = new ManagedMetadataImage(peReader.GetMetadataReader(), frame.MetadataDeltas);
        return GetArguments(metadata, frame.MethodToken, frame.ExpressionLanguage);
    }

    /// <summary>
    /// Resolves physical parameter slots from the selected module metadata and symbol language.
    /// </summary>
    /// <param name="metadata">The exact module metadata snapshot.</param>
    /// <param name="methodToken">The captured or live method definition token.</param>
    /// <param name="language">The method's symbol language.</param>
    /// <returns>Argument names and tuple declarations keyed by runtime slot.</returns>
    internal static IReadOnlyDictionary<int, ManagedSymbolVariable> GetArguments(
        ManagedMetadataImage metadata, uint methodToken, DebugExpressionLanguage language)
    {
        var methodHandle = (MethodDefinitionHandle)MetadataTokens.EntityHandle(checked((int)methodToken));
        MethodDefinition method = metadata.GetMethodDefinition(methodHandle);
        bool hasThis = (method.Attributes & MethodAttributes.Static) == 0;
        Dictionary<int, ManagedSymbolVariable> result = [];
        if (hasThis)
        {
            result[0] = new ManagedSymbolVariable(
                language == DebugExpressionLanguage.VisualBasic ? "Me" : "this",
                TupleCustomTypeInfo: null);
        }

        foreach (ParameterHandle parameterHandle in metadata.GetParameters(methodHandle))
        {
            Parameter parameter = metadata.GetParameter(parameterHandle);
            if (parameter.SequenceNumber == 0)
            {
                continue;
            }

            int runtimeIndex = checked(parameter.SequenceNumber - (hasThis ? 0 : 1));
            string name = metadata.GetString(parameter.Name);
            result[runtimeIndex] = new ManagedSymbolVariable(
                string.IsNullOrEmpty(name) ? $"argument {runtimeIndex}" : name,
                ManagedTupleElementNameReader.ReadAttribute(metadata, parameterHandle));
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

}
