using Csls.Debugger.Contracts;
using System.Reflection.Metadata;

namespace Csls.Debugger;

/// <summary>
/// Maps source-ordered call arguments to exact CLR metadata parameter positions.
/// </summary>
internal static class ManagedFunctionArgumentMap
{
    /// <summary>
    /// Resolves named arguments for one candidate without changing source evaluation order.
    /// </summary>
    /// <param name="metadata">The loaded method metadata generation.</param>
    /// <param name="method">The candidate declaration.</param>
    /// <param name="argumentNames">Names aligned with source-order arguments, or null for positional calls.</param>
    /// <param name="parameterCount">The candidate's exact CLR parameter count.</param>
    /// <param name="language">The source language's identifier comparison policy.</param>
    /// <returns>Parameter-to-source indexes, or null when names cannot bind this candidate.</returns>
    internal static int[]? TryCreate(
        ManagedMetadataImage metadata,
        MethodDefinitionHandle method,
        IReadOnlyList<string?>? argumentNames,
        int parameterCount,
        DebugExpressionLanguage language)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        if (argumentNames is null || !argumentNames.Any(static name => name is not null))
        {
            return [.. Enumerable.Range(0, parameterCount)];
        }

        if (argumentNames.Count != parameterCount)
        {
            throw new InvalidDataException("Call argument names do not match the argument count.");
        }

        string?[] parameterNames = new string?[parameterCount];
        foreach (Parameter parameter in metadata.GetParameters(method).Select(metadata.GetParameter))
        {
            if (parameter.SequenceNumber == 0)
            {
                continue;
            }

            int position = parameter.SequenceNumber - 1;
            if ((uint)position >= (uint)parameterCount)
            {
                throw new BadImageFormatException("A method parameter number exceeds its signature.");
            }

            parameterNames[position] = metadata.GetString(parameter.Name);
        }

        int[] sourceIndices = new int[parameterCount];
        Array.Fill(sourceIndices, -1);
        StringComparison comparison = language == DebugExpressionLanguage.VisualBasic
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        bool seenOutOfPositionName = false;
        for (int sourceIndex = 0; sourceIndex < argumentNames.Count; sourceIndex++)
        {
            string? name = argumentNames[sourceIndex];
            if (name is null && seenOutOfPositionName)
            {
                return null;
            }

            int position = name is null
                ? sourceIndex
                : Array.FindIndex(parameterNames, candidate =>
                    string.Equals(candidate, name, comparison));
            if (position < 0 || position >= parameterCount || sourceIndices[position] != -1)
            {
                return null;
            }

            sourceIndices[position] = sourceIndex;
            seenOutOfPositionName |= name is not null && position != sourceIndex;
        }

        return sourceIndices.Contains(-1) ? null : sourceIndices;
    }
}
