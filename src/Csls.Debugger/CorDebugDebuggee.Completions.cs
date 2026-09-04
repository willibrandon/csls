using Csls.Debugger.Contracts;
using Csls.Debugger.Interop;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace Csls.Debugger;

/// <summary>
/// Produces bounded expression completions from live frame values and runtime metadata.
/// </summary>
internal sealed partial class CorDebugDebuggee
{
    private const int MaximumCompletionCount = 2048;

    /// <summary>
    /// Gets current locals, arguments, and literal keywords matching one prefix.
    /// </summary>
    /// <param name="frameId">The selected managed frame.</param>
    /// <param name="prefix">The source identifier prefix.</param>
    /// <param name="replacementStart">The zero-based replacement start.</param>
    /// <param name="replacementLength">The replacement length.</param>
    /// <param name="generation">The current stopped generation.</param>
    /// <returns>The bounded root completion candidates.</returns>
    internal IReadOnlyList<DebugCompletionInfo> GetRootCompletions(
        int frameId,
        string prefix,
        int replacementStart,
        int replacementLength,
        DebugStopGeneration generation)
    {
        ManagedFrameHandle frame = GetFrame(frameId, generation);
        Dictionary<string, DebugCompletionInfo> candidates = CreateCompletionMap(
            frame.ExpressionLanguage);
        AddVariableCompletions(
            candidates,
            EnumerateValues(
                frame,
                ManagedScopeKind.Locals,
                GetVariableNames(frame, ManagedScopeKind.Locals),
                generation,
                start: 0,
                MaximumCompletionCount),
            prefix,
            frame.ExpressionLanguage,
            replacementStart,
            replacementLength);
        AddVariableCompletions(
            candidates,
            EnumerateValues(
                frame,
                ManagedScopeKind.Arguments,
                GetVariableNames(frame, ManagedScopeKind.Arguments),
                generation,
                start: 0,
                MaximumCompletionCount),
            prefix,
            frame.ExpressionLanguage,
            replacementStart,
            replacementLength);
        AddKeywordCompletions(
            candidates,
            prefix,
            frame.ExpressionLanguage,
            replacementStart,
            replacementLength);
        return OrderCompletions(candidates);
    }

    /// <summary>
    /// Gets supported fields and methods for one safe receiver expression.
    /// </summary>
    /// <param name="frameId">The selected managed frame.</param>
    /// <param name="receiverPlan">The compiler-lowered receiver expression.</param>
    /// <param name="prefix">The source member prefix.</param>
    /// <param name="replacementStart">The zero-based replacement start.</param>
    /// <param name="replacementLength">The replacement length.</param>
    /// <param name="generation">The current stopped generation.</param>
    /// <returns>The bounded member completion candidates.</returns>
    internal IReadOnlyList<DebugCompletionInfo> GetMemberCompletions(
        int frameId,
        DebugExpressionPlan receiverPlan,
        string prefix,
        int replacementStart,
        int replacementLength,
        DebugStopGeneration generation)
    {
        ManagedFrameHandle frame = GetFrame(frameId, generation);
        ManagedExpressionPlanValidator.Validate(receiverPlan, frame.ExpressionLanguage);
        try
        {
            ManagedExpressionValue receiver = EvaluateNode(
                frame,
                receiverPlan,
                receiverPlan.Root,
                generation);
            return GetInstanceMemberCompletions(
                receiver,
                prefix,
                replacementStart,
                replacementLength,
                frame.ExpressionLanguage);
        }
        catch (InvalidOperationException) when (TryGetQualifiedTypeName(
            receiverPlan.Root,
            out string typeName))
        {
            return GetStaticMemberCompletions(
                typeName,
                prefix,
                replacementStart,
                replacementLength,
                frame.ExpressionLanguage);
        }
    }

    private IReadOnlyList<DebugCompletionInfo> GetInstanceMemberCompletions(
        ManagedExpressionValue receiver,
        string prefix,
        int replacementStart,
        int replacementLength,
        DebugExpressionLanguage language)
    {
        nint runtimeValue = GetRuntimeValue(receiver);
        nint dereferenced = 0;
        nint value2 = 0;
        nint currentType = 0;
        Dictionary<string, DebugCompletionInfo> candidates = CreateCompletionMap(language);
        try
        {
            dereferenced = DereferenceValue(runtimeValue);
            value2 = ComAbi.QueryInterface(dereferenced, ICorDebugValue2Abi.InterfaceId);
            unsafe
            {
                nint* exactTypeAddress = &currentType;
                CorDebugHResult.ThrowIfFailed(
                    new ICorDebugValue2Abi(value2).GetExactType((nint)exactTypeAddress),
                    "ICorDebugValue2.GetExactType");
                currentType = RequirePointer(
                    Volatile.Read(ref *exactTypeAddress),
                    "ICorDebugValue2.GetExactType");
            }

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
                    AddDeclaredMemberCompletions(
                        peReader.GetMetadataReader(),
                        typeToken,
                        staticMembers: false,
                        prefix,
                        language,
                        replacementStart,
                        replacementLength,
                        candidates);
                    unsafe
                    {
                        nint* baseTypeAddress = &baseType;
                        CorDebugHResult.ThrowIfFailed(
                            new ICorDebugTypeAbi(currentType).GetBase((nint)baseTypeAddress),
                            "ICorDebugType.GetBase");
                        baseType = Volatile.Read(ref *baseTypeAddress);
                    }
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

            return OrderCompletions(candidates);
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

            if (dereferenced != 0)
            {
                _ = ComAbi.Release(dereferenced);
            }
        }
    }

    private IReadOnlyList<DebugCompletionInfo> GetStaticMemberCompletions(
        string typeName,
        string prefix,
        int replacementStart,
        int replacementLength,
        DebugExpressionLanguage language)
    {
        StringComparison comparison = CompletionComparison(language);
        bool simpleName = !typeName.Contains('.', StringComparison.Ordinal) &&
            !typeName.Contains('+', StringComparison.Ordinal);
        var matches = new List<(CorDebugLoadedModule Module, uint TypeToken)>();
        int scannedTypeCount = 0;
        foreach (CorDebugLoadedModule module in _sourceBreakpoints.GetRuntimeModules())
        {
            scannedTypeCount = AddFunctionEvaluationTypeMatches(
                module,
                typeName,
                simpleName,
                comparison,
                matches,
                scannedTypeCount);
        }

        if (matches.Count == 0)
        {
            throw new InvalidOperationException(
                $"No loaded runtime type named '{typeName}' is available for completion.");
        }

        if (matches.Count > 1)
        {
            throw new InvalidOperationException(
                $"Static type name '{typeName}' is ambiguous across loaded runtime modules. " +
                "Use its fully qualified metadata name.");
        }

        Dictionary<string, DebugCompletionInfo> candidates = CreateCompletionMap(language);
        (CorDebugLoadedModule resolvedModule, uint typeToken) = matches[0];
        using PEReader? peReader = resolvedModule.OpenPeReader();
        if (peReader is null)
        {
            throw new InvalidOperationException(
                $"Loaded module '{resolvedModule.Name ?? "unnamed module"}' no longer has a readable " +
                "PE image.");
        }

        AddDeclaredMemberCompletions(
            peReader.GetMetadataReader(),
            typeToken,
            staticMembers: true,
            prefix,
            language,
            replacementStart,
            replacementLength,
            candidates);
        return OrderCompletions(candidates);
    }

    private static void AddVariableCompletions(
        Dictionary<string, DebugCompletionInfo> candidates,
        IEnumerable<DebugVariableInfo> variables,
        string prefix,
        DebugExpressionLanguage language,
        int replacementStart,
        int replacementLength)
    {
        foreach (DebugVariableInfo variable in variables)
        {
            if (candidates.Count >= MaximumCompletionCount || variable.EvaluateName is null ||
                !MatchesCompletionPrefix(variable.Name, prefix, language))
            {
                continue;
            }

            candidates.TryAdd(
                variable.Name,
                new DebugCompletionInfo(
                    variable.Name,
                    variable.Name,
                    variable.Type,
                    DebugCompletionItemKind.Variable,
                    replacementStart,
                    replacementLength));
        }
    }

    private static void AddKeywordCompletions(
        Dictionary<string, DebugCompletionInfo> candidates,
        string prefix,
        DebugExpressionLanguage language,
        int replacementStart,
        int replacementLength)
    {
        string[] keywords = language switch
        {
            DebugExpressionLanguage.VisualBasic => ["False", "Me", "Nothing", "True"],
            DebugExpressionLanguage.FSharp => ["false", "null", "true"],
            _ => ["false", "null", "this", "true"]
        };
        foreach (string keyword in keywords.Where(keyword =>
            MatchesCompletionPrefix(keyword, prefix, language)))
        {
            candidates.TryAdd(
                keyword,
                new DebugCompletionInfo(
                    keyword,
                    keyword,
                    "language keyword",
                    DebugCompletionItemKind.Keyword,
                    replacementStart,
                    replacementLength));
        }
    }

    private static void AddDeclaredMemberCompletions(
        MetadataReader metadata,
        uint typeToken,
        bool staticMembers,
        string prefix,
        DebugExpressionLanguage language,
        int replacementStart,
        int replacementLength,
        Dictionary<string, DebugCompletionInfo> candidates)
    {
        TypeDefinition type = metadata.GetTypeDefinition(
            MetadataTokens.TypeDefinitionHandle(checked((int)(typeToken & 0x00FFFFFF))));
        string declaringType = metadata.GetString(type.Name);
        if (!staticMembers)
        {
            foreach (FieldDefinition field in type.GetFields().Select(metadata.GetFieldDefinition))
            {
                string name = metadata.GetString(field.Name);
                if ((field.Attributes & FieldAttributes.Static) != 0 ||
                    !ManagedExpressionName.IsSimpleIdentifier(name) ||
                    !MatchesCompletionPrefix(name, prefix, language))
                {
                    continue;
                }

                string fieldType = field.DecodeSignature(
                    FunctionEvaluationSignatureTypeProvider.Instance,
                    genericContext: null);
                AddMemberCompletion(
                    candidates,
                    name,
                    fieldType,
                    declaringType,
                    DebugCompletionItemKind.Field,
                    replacementStart,
                    replacementLength);
            }
        }

        foreach (MethodDefinitionHandle methodHandle in type.GetMethods())
        {
            MethodDefinition method = metadata.GetMethodDefinition(methodHandle);
            string name = metadata.GetString(method.Name);
            bool methodIsStatic = (method.Attributes & MethodAttributes.Static) != 0;
            if (methodIsStatic != staticMembers ||
                (method.Attributes & (MethodAttributes.Abstract | MethodAttributes.SpecialName)) != 0 ||
                !ManagedExpressionName.IsSimpleIdentifier(name) ||
                !MatchesCompletionPrefix(name, prefix, language))
            {
                continue;
            }

            MethodSignature<string> signature = method.DecodeSignature(
                FunctionEvaluationSignatureTypeProvider.Instance,
                genericContext: null);
            string detail = $"{FormatCompletionType(signature.ReturnType)} {declaringType}." +
                $"{name}({string.Join(", ", signature.ParameterTypes.Select(FormatCompletionType))})";
            AddMemberCompletion(
                candidates,
                name,
                detail,
                declaringType,
                DebugCompletionItemKind.Method,
                replacementStart,
                replacementLength,
                detailIncludesDeclaringType: true);
        }
    }

    private static void AddMemberCompletion(
        Dictionary<string, DebugCompletionInfo> candidates,
        string name,
        string detail,
        string declaringType,
        DebugCompletionItemKind kind,
        int replacementStart,
        int replacementLength,
        bool detailIncludesDeclaringType = false)
    {
        if (candidates.Count >= MaximumCompletionCount || candidates.ContainsKey(name))
        {
            return;
        }

        candidates.Add(
            name,
            new DebugCompletionInfo(
                name,
                name,
                detailIncludesDeclaringType ? detail : $"{FormatCompletionType(detail)} {declaringType}.{name}",
                kind,
                replacementStart,
                replacementLength));
    }

    private static Dictionary<string, DebugCompletionInfo> CreateCompletionMap(
        DebugExpressionLanguage language) => new(
            language == DebugExpressionLanguage.VisualBasic
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal);

    private static IReadOnlyList<DebugCompletionInfo> OrderCompletions(
        Dictionary<string, DebugCompletionInfo> candidates) =>
        [.. candidates.Values
            .OrderBy(static candidate => candidate.Label, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static candidate => candidate.Label, StringComparer.Ordinal)];

    private static bool MatchesCompletionPrefix(
        string candidate,
        string prefix,
        DebugExpressionLanguage language) => candidate.StartsWith(
            prefix,
            CompletionComparison(language));

    private static StringComparison CompletionComparison(
        DebugExpressionLanguage language) => language == DebugExpressionLanguage.VisualBasic
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    private static string FormatCompletionType(string type) => type
        .Replace("reference:", string.Empty, StringComparison.Ordinal)
        .Replace("value:", string.Empty, StringComparison.Ordinal);
}
