using Csls.Debugger.Contracts;
using System.Globalization;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace Csls.Debugger;

/// <summary>
/// Resolves compiler-authored hoisted locals within their exact Portable PDB scope and method generation.
/// </summary>
internal static class ManagedStateMachineLocalResolver
{
    /// <summary>
    /// Finds active source locals stored in the selected state-machine receiver.
    /// </summary>
    /// <param name="frame">The stopped physical frame and its immutable metadata and symbol snapshots.</param>
    /// <returns>Active hoisted locals in compiler slot order.</returns>
    internal static IReadOnlyList<ManagedStateMachineVariable> Resolve(ManagedFrameHandle frame)
    {
        if (frame.ExpressionLanguage is not (DebugExpressionLanguage.CSharp or DebugExpressionLanguage.VisualBasic))
        {
            return [];
        }

        using DebugSymbolReader? symbols = frame.OpenSymbols();
        if (symbols?.GetStateMachineKickoffMethod(frame.MethodToken) is not uint kickoff)
        {
            return [];
        }

        IReadOnlySet<int> active = symbols.GetActiveHoistedLocalScopes(frame.MethodToken, frame.IlOffset);
        using PEReader? pe = frame.OpenPeReader();
        if (active.Count == 0 || pe is null)
        {
            return [];
        }

        using var metadata = new ManagedMetadataImage(pe.GetMetadataReader(), frame.MetadataDeltas);
        var method = (MethodDefinitionHandle)MetadataTokens.EntityHandle(checked((int)frame.MethodToken));
        TypeDefinitionHandle type = metadata.GetDeclaringType(method);
        var variables = new SortedDictionary<int, ManagedStateMachineVariable>();
        foreach (FieldDefinitionHandle handle in metadata.Baseline.GetTypeDefinition(type).GetFields())
        {
            (MetadataReader reader, EntityHandle relative) = metadata.Resolve(handle);
            FieldDefinition field = reader.GetFieldDefinition((FieldDefinitionHandle)relative);
            string fieldName = metadata.GetString(field.Name);
            if ((field.Attributes & FieldAttributes.Static) == 0 &&
                TryParseFieldName(fieldName, frame.ExpressionLanguage, out string name, out int slot) && active.Contains(slot))
            {
                variables.Add(slot, new ManagedStateMachineVariable(name, fieldName, kickoff, null,
                    ManagedTupleElementNameReader.ReadAttribute(metadata, handle)));
            }
        }

        return [.. variables.Values];
    }

    private static bool TryParseFieldName(string field, DebugExpressionLanguage language, out string name, out int slot)
    {
        name = string.Empty;
        slot = -1;
        if (language == DebugExpressionLanguage.CSharp)
        {
            int separator = field.IndexOf(">5__", StringComparison.Ordinal);
            if (separator <= 1 || field[0] != '<' ||
                !int.TryParse(field.AsSpan(separator + 4), NumberStyles.None, CultureInfo.InvariantCulture, out int ordinal) || ordinal <= 0)
            {
                return false;
            }

            name = field[1..separator];
            slot = ordinal - 1;
            return true;
        }

        const string prefix = "$VB$ResumableLocal_";
        int suffix = field.LastIndexOf('$');
        if (!field.StartsWith(prefix, StringComparison.Ordinal) || suffix <= prefix.Length ||
            !int.TryParse(field.AsSpan(suffix + 1), NumberStyles.None, CultureInfo.InvariantCulture, out slot))
        {
            return false;
        }

        name = field[prefix.Length..suffix];
        return true;
    }
}
