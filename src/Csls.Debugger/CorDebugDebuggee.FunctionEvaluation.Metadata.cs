using Csls.Debugger.Contracts;
using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace Csls.Debugger;

/// <summary>
/// Resolves managed function-evaluation targets from CLR metadata.
/// </summary>
internal sealed partial class CorDebugDebuggee
{
    private static uint ResolveInstanceMethod(
        nint module,
        nint runtimeClass,
        string methodName,
        DebugExpressionLanguage language,
        ManagedExpressionValue[] arguments)
    {
        string modulePath = CorDebugModulePath.Get(module);
        using FileStream stream = File.OpenRead(modulePath);
        using var peReader = new PEReader(stream);
        MetadataReader metadata = peReader.GetMetadataReader();
        uint typeToken = GetClassToken(runtimeClass);
        StringComparison comparison = language == DebugExpressionLanguage.VisualBasic
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        while (typeToken != 0)
        {
            int row = checked((int)(typeToken & 0x00FFFFFF));
            if (row == 0 || row > metadata.TypeDefinitions.Count)
            {
                break;
            }

            TypeDefinition type = metadata.GetTypeDefinition(
                MetadataTokens.TypeDefinitionHandle(row));
            List<(MethodDefinitionHandle Handle, int Score)> matches = [];
            foreach (MethodDefinitionHandle methodHandle in type.GetMethods())
            {
                MethodDefinition method = metadata.GetMethodDefinition(methodHandle);
                if ((method.Attributes & (MethodAttributes.Static | MethodAttributes.Abstract)) != 0 ||
                    !string.Equals(metadata.GetString(method.Name), methodName, comparison))
                {
                    continue;
                }

                MethodSignature<string> signature = method.DecodeSignature(
                    FunctionEvaluationSignatureTypeProvider.Instance,
                    genericContext: null);
                if (signature.Header.IsGeneric ||
                    signature.ParameterTypes.Length != arguments.Length)
                {
                    continue;
                }

                int score = ScoreParameters(signature.ParameterTypes, arguments);
                if (score >= 0)
                {
                    matches.Add((methodHandle, score));
                }
            }

            if (matches.Count > 0)
            {
                int bestScore = matches.Max(static candidate => candidate.Score);
                MethodDefinitionHandle[] bestMatches =
                    [.. matches
                        .Where(candidate => candidate.Score == bestScore)
                        .Select(static candidate => candidate.Handle)];
                if (bestMatches.Length > 1)
                {
                    throw new InvalidOperationException(
                        $"Method call '{methodName}' with {arguments.Length} argument(s) is " +
                        "ambiguous on the runtime type.");
                }

                return checked((uint)MetadataTokens.GetToken(bestMatches[0]));
            }

            EntityHandle baseType = type.BaseType;
            typeToken = baseType.Kind == HandleKind.TypeDefinition
                ? checked((uint)MetadataTokens.GetToken((TypeDefinitionHandle)baseType))
                : 0;
        }

        throw new InvalidOperationException(
            $"No instance method named '{methodName}' with {arguments.Length} argument(s) " +
            "is available on the runtime type in its defining module.");
    }

    private static int ScoreParameters(
        ImmutableArray<string> parameterTypes,
        ManagedExpressionValue[] arguments)
    {
        int score = 0;
        for (int index = 0; index < arguments.Length; index++)
        {
            int parameterScore = ScoreParameter(parameterTypes[index], arguments[index]);
            if (parameterScore < 0)
            {
                return -1;
            }

            score = checked(score + parameterScore);
        }

        return score;
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

        if (string.Equals(normalizedType, argument.Display.Type, StringComparison.Ordinal))
        {
            return 4;
        }

        return argument.Display.VariablesReference > 0 && referenceType ? 1 : -1;
    }
}
