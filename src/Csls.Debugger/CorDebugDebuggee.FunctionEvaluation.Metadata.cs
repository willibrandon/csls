using Csls.Debugger.Contracts;
using Csls.Debugger.Interop;
using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text;

namespace Csls.Debugger;

/// <summary>
/// Resolves managed function-evaluation targets from CLR metadata.
/// </summary>
internal sealed partial class CorDebugDebuggee
{
    private const int MaximumFunctionEvaluationHierarchyDepth = 256;

    private unsafe nint ResolveInstanceFunction(
        nint receiver,
        string methodName,
        DebugExpressionLanguage language,
        ManagedExpressionValue[] arguments)
    {
        nint value2 = 0;
        nint currentType = 0;
        try
        {
            value2 = ComAbi.QueryInterface(receiver, ICorDebugValue2Abi.InterfaceId);
            nint* exactTypeAddress = &currentType;
            CorDebugHResult.ThrowIfFailed(
                new ICorDebugValue2Abi(value2).GetExactType((nint)exactTypeAddress),
                "ICorDebugValue2.GetExactType");
            currentType = RequirePointer(
                Volatile.Read(ref *exactTypeAddress),
                "ICorDebugValue2.GetExactType");

            for (int depth = 0;
                currentType != 0 && depth < MaximumFunctionEvaluationHierarchyDepth;
                depth++)
            {
                nint runtimeClass = 0;
                nint module = 0;
                nint baseType = 0;
                try
                {
                    runtimeClass = GetRuntimeTypeClass(currentType);
                    module = GetClassModule(runtimeClass);
                    uint typeToken = GetClassToken(runtimeClass);
                    using PEReader peReader = _sourceBreakpoints
                        .FindModule(module)
                        ?.OpenPeReader() ?? new PEReader(new FileStream(
                            CorDebugModulePath.Get(module),
                            FileMode.Open,
                            FileAccess.Read,
                            FileShare.Read | FileShare.Delete));
                    uint? methodToken = TryResolveDeclaredMethod(
                        peReader.GetMetadataReader(),
                        typeToken,
                        methodName,
                        language,
                        arguments,
                        staticMethod: false);
                    if (methodToken is uint resolvedMethodToken)
                    {
                        return GetModuleFunction(module, resolvedMethodToken);
                    }

                    nint* baseTypeAddress = &baseType;
                    CorDebugHResult.ThrowIfFailed(
                        new ICorDebugTypeAbi(currentType).GetBase((nint)baseTypeAddress),
                        "ICorDebugType.GetBase");
                    baseType = Volatile.Read(ref *baseTypeAddress);
                }
                finally
                {
                    if (module != 0)
                    {
                        _ = ComAbi.Release(module);
                    }

                    if (runtimeClass != 0)
                    {
                        _ = ComAbi.Release(runtimeClass);
                    }

                    if (currentType != 0)
                    {
                        _ = ComAbi.Release(currentType);
                    }

                    currentType = baseType;
                }
            }

            if (currentType != 0)
            {
                throw new InvalidOperationException(
                    $"The runtime type hierarchy exceeds the supported depth of " +
                    $"{MaximumFunctionEvaluationHierarchyDepth}.");
            }
        }
        finally
        {
            if (currentType != 0)
            {
                _ = ComAbi.Release(currentType);
            }

            if (value2 != 0)
            {
                _ = ComAbi.Release(value2);
            }
        }

        throw new InvalidOperationException(
            $"No instance method named '{methodName}' with {arguments.Length} argument(s) " +
            "is available on the runtime type hierarchy.");
    }

    private static uint? TryResolveDeclaredMethod(
        MetadataReader metadata,
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
        foreach (MethodDefinitionHandle methodHandle in type.GetMethods())
        {
            MethodDefinition method = metadata.GetMethodDefinition(methodHandle);
            bool methodIsStatic = (method.Attributes & MethodAttributes.Static) != 0;
            if ((method.Attributes & MethodAttributes.Abstract) != 0 ||
                methodIsStatic != staticMethod ||
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

        if (string.Equals(normalizedType, argument.Display.Type, StringComparison.Ordinal))
        {
            return 4;
        }

        return argument.Display.VariablesReference > 0 && referenceType ? 1 : -1;
    }

    private static unsafe nint GetRuntimeTypeClass(nint type)
    {
        nint runtimeClass = 0;
        nint* runtimeClassAddress = &runtimeClass;
        CorDebugHResult.ThrowIfFailed(
            new ICorDebugTypeAbi(type).GetClass((nint)runtimeClassAddress),
            "ICorDebugType.GetClass");
        return RequirePointer(
            Volatile.Read(ref *runtimeClassAddress),
            "ICorDebugType.GetClass");
    }
}
