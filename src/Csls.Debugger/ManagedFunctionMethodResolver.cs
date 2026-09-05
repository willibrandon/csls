using Csls.Debugger.Contracts;
using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text;

namespace Csls.Debugger;

/// <summary>
/// Selects concrete callable declarations from the current aggregate module metadata.
/// </summary>
internal static class ManagedFunctionMethodResolver
{
    /// <summary>
    /// Opens and owns the current module metadata while selecting one callable declaration.
    /// </summary>
    internal static uint? Resolve(
        CorDebugLoadedModule module,
        uint typeToken,
        string methodName,
        DebugExpressionLanguage language,
        ManagedExpressionValue[] arguments,
        bool staticMethod)
    {
        using PEReader? reader = module.OpenPeReader();
        if (reader is null)
        {
            throw new InvalidOperationException("The method's runtime metadata is unavailable.");
        }

        using var metadata = new ManagedMetadataImage(reader.GetMetadataReader(), module.MetadataDeltas);
        return Resolve(metadata, typeToken, methodName, language, arguments, staticMethod);
    }

    /// <summary>
    /// Resolves a uniquely matching static, instance, or constructor declaration before target execution.
    /// </summary>
    internal static uint? Resolve(
        ManagedMetadataImage metadata,
        uint typeToken,
        string methodName,
        DebugExpressionLanguage language,
        ManagedExpressionValue[] arguments,
        bool staticMethod,
        IReadOnlyList<string>? declaringTypeArguments = null)
    {
        EntityHandle entity = MetadataTokens.EntityHandle(checked((int)typeToken));
        if (entity.Kind != HandleKind.TypeDefinition)
        {
            throw new BadImageFormatException(
                $"Runtime type token 0x{typeToken:X8} is not a TypeDef token.");
        }

        var typeHandle = (TypeDefinitionHandle)entity;
        TypeDefinition type = metadata.GetTypeDefinition(typeHandle);
        StringComparison comparison = language == DebugExpressionLanguage.VisualBasic
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        List<(MethodDefinitionHandle Handle, int Score)> matches = [];
        foreach (MethodDefinitionHandle methodHandle in metadata.GetMethods(typeHandle))
        {
            MethodDefinition method = metadata.GetMethodDefinition(methodHandle);
            bool methodIsStatic = (method.Attributes & MethodAttributes.Static) != 0;
            if ((method.Attributes & MethodAttributes.Abstract) != 0 ||
                methodIsStatic != staticMethod ||
                !string.Equals(metadata.GetString(method.Name), methodName, comparison))
            {
                continue;
            }

            BlobReader blob = metadata.GetBlobReader(method.Signature);
            var decoder = new SignatureDecoder<string, object?>(new FunctionEvaluationSignatureTypeProvider(metadata),
                metadata.Baseline, genericContext: null);
            MethodSignature<string> signature = decoder.DecodeMethodSignature(ref blob);
            if (signature.Header.IsGeneric ||
                signature.ParameterTypes.Length != arguments.Length)
            {
                continue;
            }

            int score = ScoreParameters(
                signature.ParameterTypes,
                arguments,
                declaringTypeArguments);
            if (score >= 0)
            {
                matches.Add((methodHandle, score));
            }
        }

        if (matches.Count == 0)
        {
            return null;
        }

        int bestScore = matches.Max(static candidate => candidate.Score);
        MethodDefinitionHandle[] bestMatches =
            [.. matches
                .Where(candidate => candidate.Score == bestScore)
                .Select(static candidate => candidate.Handle)];
        if (bestMatches.Length > 1)
        {
            string typeName = metadata.GetString(type.Name);
            throw new InvalidOperationException(
                $"Method call '{methodName}' with {arguments.Length} argument(s) is " +
                $"ambiguous on runtime type '{typeName}'.");
        }

        return checked((uint)MetadataTokens.GetToken(bestMatches[0]));
    }

    private static int ScoreParameters(
        ImmutableArray<string> parameterTypes,
        ManagedExpressionValue[] arguments,
        IReadOnlyList<string>? declaringTypeArguments)
    {
        int score = 0;
        for (int index = 0; index < arguments.Length; index++)
        {
            string? parameterType = SubstituteDeclaringTypeArguments(
                parameterTypes[index],
                declaringTypeArguments);
            if (parameterType is null)
            {
                return -1;
            }

            int parameterScore = ScoreParameter(parameterType, arguments[index]);
            if (parameterScore < 0)
            {
                return -1;
            }

            score = checked(score + parameterScore);
        }

        return score;
    }

    private static string? SubstituteDeclaringTypeArguments(
        string parameterType,
        IReadOnlyList<string>? declaringTypeArguments)
    {
        const string marker = "type-parameter:";
        int markerIndex = parameterType.IndexOf(marker, StringComparison.Ordinal);
        if (markerIndex < 0)
        {
            return parameterType;
        }

        if (declaringTypeArguments is null)
        {
            return null;
        }

        var result = new StringBuilder(parameterType.Length);
        int consumed = 0;
        while (markerIndex >= 0)
        {
            _ = result.Append(parameterType, consumed, markerIndex - consumed);
            int numberStart = markerIndex + marker.Length;
            int numberEnd = numberStart;
            while (numberEnd < parameterType.Length &&
                char.IsAsciiDigit(parameterType[numberEnd]))
            {
                numberEnd++;
            }

            if (numberEnd == numberStart || !int.TryParse(
                parameterType.AsSpan(numberStart, numberEnd - numberStart),
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out int argumentIndex) ||
                argumentIndex >= declaringTypeArguments.Count)
            {
                return null;
            }

            _ = result.Append(declaringTypeArguments[argumentIndex]);
            consumed = numberEnd;
            markerIndex = parameterType.IndexOf(marker, consumed, StringComparison.Ordinal);
        }

        _ = result.Append(parameterType, consumed, parameterType.Length - consumed);
        return result.ToString();
    }

    private static int ScoreParameter(string parameterType, ManagedExpressionValue argument)
    {
        if (parameterType.StartsWith("by-reference:", StringComparison.Ordinal) ||
            parameterType.StartsWith("pointer:", StringComparison.Ordinal) ||
            parameterType.StartsWith("method-parameter:", StringComparison.Ordinal) ||
            parameterType.StartsWith("type-parameter:", StringComparison.Ordinal) ||
            string.Equals(parameterType, "function-pointer", StringComparison.Ordinal))
        {
            return -1;
        }

        bool referenceType = parameterType.StartsWith(
            "reference:",
            StringComparison.Ordinal) ||
            string.Equals(parameterType, "string", StringComparison.Ordinal) ||
            string.Equals(parameterType, "object", StringComparison.Ordinal);
        string normalizedType = parameterType.StartsWith(
            "reference:",
            StringComparison.Ordinal)
                ? parameterType["reference:".Length..]
                : parameterType.StartsWith("value:", StringComparison.Ordinal)
                    ? parameterType["value:".Length..]
                    : parameterType;
        if (argument.HasScalar && argument.Scalar is null)
        {
            return referenceType ? 1 : -1;
        }

        if (string.Equals(normalizedType, argument.Type, StringComparison.Ordinal))
        {
            return 4;
        }

        return argument.RuntimeValueReference > 0 && referenceType &&
            (!argument.HasScalar || argument.Scalar is string) ? 1 : -1;
    }

}
