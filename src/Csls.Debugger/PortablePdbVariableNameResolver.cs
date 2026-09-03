using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace Csls.Debugger;

/// <summary>
/// Resolves argument and active-local names from PE metadata and Portable PDB scopes.
/// </summary>
internal static class PortablePdbVariableNameResolver
{
    /// <summary>
    /// Resolves runtime argument indexes to source parameter names.
    /// </summary>
    /// <param name="modulePath">The loaded managed module path.</param>
    /// <param name="methodToken">The method-definition metadata token.</param>
    /// <returns>Argument names keyed by their ICorDebug argument index.</returns>
    internal static IReadOnlyDictionary<int, string> GetArguments(
        string? modulePath,
        uint methodToken)
    {
        if (modulePath is null || !File.Exists(modulePath))
        {
            return new Dictionary<int, string>();
        }

        using FileStream stream = File.OpenRead(modulePath);
        using var peReader = new PEReader(stream);
        MetadataReader metadata = peReader.GetMetadataReader();
        MethodDefinition method = GetMethod(metadata, methodToken);
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
    /// Resolves active Portable PDB local slots to source names.
    /// </summary>
    /// <param name="modulePath">The loaded managed module path.</param>
    /// <param name="methodToken">The method-definition metadata token.</param>
    /// <param name="ilOffset">The current IL instruction offset.</param>
    /// <returns>Local names keyed by their ICorDebug local slot.</returns>
    internal static IReadOnlyDictionary<int, string> GetLocals(
        string? modulePath,
        uint methodToken,
        uint ilOffset)
    {
        if (modulePath is null)
        {
            return new Dictionary<int, string>();
        }

        using var symbols = PortablePdbReader.TryOpen(modulePath);
        if (symbols is null)
        {
            return new Dictionary<int, string>();
        }

        int rowNumber = checked((int)(methodToken & 0x00ffffff));
        MetadataReader pdb = symbols.Metadata;
        MethodDefinitionHandle methodHandle = MetadataTokens.MethodDefinitionHandle(rowNumber);
        Dictionary<int, string> result = [];
        foreach (LocalScopeHandle scopeHandle in pdb.GetLocalScopes(methodHandle))
        {
            LocalScope scope = pdb.GetLocalScope(scopeHandle);
            uint start = checked((uint)scope.StartOffset);
            uint end = checked((uint)(scope.StartOffset + scope.Length));
            if (ilOffset < start || ilOffset >= end)
            {
                continue;
            }

            foreach (LocalVariableHandle variableHandle in scope.GetLocalVariables())
            {
                LocalVariable variable = pdb.GetLocalVariable(variableHandle);
                result[variable.Index] = pdb.GetString(variable.Name);
            }
        }

        return result;
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
