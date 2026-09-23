using Csls.Debugger.Contracts;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace Csls.Debugger;

/// <summary>
/// Resolves user-defined operators from the current loaded metadata generation.
/// </summary>
internal sealed class ManagedUserDefinedOperatorResolver
{
    private const int MaximumHierarchyDepth = 128;
    private readonly ManagedBoundTypeSystem _types;
    private readonly nint _thread;
    private readonly DebugExpressionLanguage _language;
    private readonly ManagedReferenceConversion _referenceConversions;

    /// <summary>
    /// Creates a resolver scoped to one stopped managed thread and loaded type universe.
    /// </summary>
    /// <param name="types">The exact loaded type system.</param>
    /// <param name="thread">The borrowed managed thread used for type identity.</param>
    /// <param name="language">The source language controlling standard conversions.</param>
    internal ManagedUserDefinedOperatorResolver(
        ManagedBoundTypeSystem types,
        nint thread,
        DebugExpressionLanguage language)
    {
        ArgumentNullException.ThrowIfNull(types);
        _types = types;
        _thread = thread;
        _language = language;
        _referenceConversions = new ManagedReferenceConversion(types);
    }

    /// <summary>
    /// Resolves the unique best loaded operator for exact runtime operand declarations.
    /// </summary>
    /// <param name="operation">The normalized source-language operator.</param>
    /// <param name="operands">The exact loaded operand declarations.</param>
    /// <returns>The selected operator, or null when no user-defined operator applies.</returns>
    internal ManagedUserDefinedOperator? Resolve(
        DebugExpressionOperator operation,
        IReadOnlyList<ManagedBoundType?> operands)
    {
        (string primaryName, string? fallbackName) = GetMethodNames(operation);
        if (operands.Count is not (1 or 2))
        {
            throw new InvalidDataException(
                "A user-defined operator requires one or two operands.");
        }

        var candidates = new List<ManagedUserDefinedOperator>();
        var participatingSources = new List<ManagedBoundType>();
        foreach (ManagedBoundType source in operands.OfType<ManagedBoundType>())
        {
            if (source.IsArray || participatingSources.Any(source.IsSameType))
            {
                continue;
            }

            participatingSources.Add(source);
            AddCandidatesFromHierarchy(
                source, primaryName, fallbackName, operands, candidates);
        }

        ManagedUserDefinedOperator[] distinct = [.. candidates.DistinctBy(candidate =>
            (candidate.DeclaringType.ModuleId, candidate.MethodToken))];
        if (distinct.Length == 0)
        {
            return null;
        }

        ManagedUserDefinedOperator[] best = [.. distinct.Where(candidate =>
            !distinct.Any(other => !ReferenceEquals(other, candidate) &&
                IsBetter(other, candidate, operands)))];
        if (best.Length != 1)
        {
            throw new InvalidOperationException(
                $"Operator '{primaryName}' is ambiguous for the loaded operand types.");
        }

        return best[0];
    }

    private void AddCandidatesFromHierarchy(
        ManagedBoundType source,
        string primaryName,
        string? fallbackName,
        IReadOnlyList<ManagedBoundType?> operands,
        List<ManagedUserDefinedOperator> destination)
    {
        ManagedBoundType? current = source;
        for (int depth = 0; current is not null && depth < MaximumHierarchyDepth; depth++)
        {
            if ((_types.GetAttributes(current) & TypeAttributes.Interface) != 0)
            {
                return;
            }

            List<ManagedUserDefinedOperator> declared = ReadDeclared(
                current, primaryName, fallbackName, operands.Count);
            ManagedUserDefinedOperator[] applicable = [.. declared.Where(candidate =>
                IsApplicable(candidate, operands))];
            if (applicable.Length != 0)
            {
                destination.AddRange(applicable);
                return;
            }

            if (!current.IsReference)
            {
                return;
            }

            current = _types.GetParents(current, _thread).FirstOrDefault(parent =>
                (_types.GetAttributes(parent) & TypeAttributes.Interface) == 0);
        }

        if (current is not null)
        {
            throw new InvalidOperationException(
                "User-defined operator lookup exceeds its bounded type hierarchy.");
        }
    }

    private List<ManagedUserDefinedOperator> ReadDeclared(
        ManagedBoundType declaringType,
        string primaryName,
        string? fallbackName,
        int parameterCount)
    {
        CorDebugLoadedModule module = _types.GetModule(declaringType);
        using PEReader? reader = module.OpenPeReader();
        if (reader is null)
        {
            return [];
        }

        using var metadata = new ManagedMetadataImage(
            reader.GetMetadataReader(), module.MetadataDeltas);
        EntityHandle entity = MetadataTokens.EntityHandle(
            checked((int)declaringType.DefinitionToken));
        if (entity.Kind != HandleKind.TypeDefinition)
        {
            throw new BadImageFormatException(
                $"Runtime type token 0x{declaringType.DefinitionToken:X8} is not a TypeDef token.");
        }

        var primary = new List<ManagedUserDefinedOperator>();
        var fallback = new List<ManagedUserDefinedOperator>();
        foreach (MethodDefinitionHandle handle in metadata.GetMethods((TypeDefinitionHandle)entity))
        {
            MethodDefinition method = metadata.GetMethodDefinition(handle);
            string name = metadata.GetString(method.Name);
            bool supportedName = string.Equals(name, primaryName, StringComparison.Ordinal) ||
                string.Equals(name, fallbackName, StringComparison.Ordinal);
            if (!supportedName ||
                (method.Attributes & MethodAttributes.MemberAccessMask) != MethodAttributes.Public ||
                (method.Attributes & (MethodAttributes.Static | MethodAttributes.SpecialName)) !=
                    (MethodAttributes.Static | MethodAttributes.SpecialName) ||
                (method.Attributes & MethodAttributes.Abstract) != 0 ||
                method.GetGenericParameters().Count != 0)
            {
                continue;
            }

            MethodSignature<ManagedMetadataTypeSignature> signature =
                metadata.DecodeMethodSignature(handle, module.Pointer);
            if (signature.ParameterTypes.Length != parameterCount ||
                signature.ParameterTypes.Any(static parameter =>
                    parameter.UnsupportedKind is not null) ||
                signature.ReturnType.UnsupportedKind is not null)
            {
                continue;
            }

            ManagedBoundType[] parameters = [.. signature.ParameterTypes.Select(parameter =>
                _types.Bind(parameter, declaringType.TypeArguments, [], _thread))];
            ManagedBoundType result = _types.Bind(
                signature.ReturnType, declaringType.TypeArguments, [], _thread);
            if (result.ElementType == 0x01)
            {
                continue;
            }

            var candidate = new ManagedUserDefinedOperator(
                declaringType,
                checked((uint)MetadataTokens.GetToken(handle)),
                parameters,
                result);
            (string.Equals(name, primaryName, StringComparison.Ordinal)
                ? primary
                : fallback).Add(candidate);
        }

        if (fallbackName is null)
        {
            return primary;
        }

        primary.AddRange(fallback.Where(ordinary => !primary.Any(@checked =>
            HasPairedSignature(@checked, ordinary))));
        return primary;
    }

    private bool IsApplicable(
        ManagedUserDefinedOperator candidate,
        IReadOnlyList<ManagedBoundType?> operands) => operands.Zip(
            candidate.ParameterTypes,
            (operand, parameter) => HasStandardImplicitConversion(operand, parameter))
            .All(static applicable => applicable);

    private bool IsBetter(
        ManagedUserDefinedOperator candidate,
        ManagedUserDefinedOperator alternative,
        IReadOnlyList<ManagedBoundType?> operands)
    {
        bool strictlyBetter = false;
        for (int index = 0; index < candidate.ParameterTypes.Count; index++)
        {
            ManagedBoundType preferred = candidate.ParameterTypes[index];
            ManagedBoundType other = alternative.ParameterTypes[index];
            if (preferred.IsSameType(other))
            {
                continue;
            }

            ManagedBoundType? operand = operands[index];
            if (operand?.IsSameType(preferred) == true)
            {
                strictlyBetter = true;
                continue;
            }

            bool preferredToOther = HasStandardImplicitConversion(preferred, other);
            bool otherToPreferred = HasStandardImplicitConversion(other, preferred);
            bool preferredSigned = ManagedPrimitiveConversionEvaluator.IsPreferredSignedInvocationTarget(
                preferred, other, _language);
            if (operand?.IsSameType(other) == true ||
                !(preferredToOther && !otherToPreferred || preferredSigned))
            {
                return false;
            }

            strictlyBetter = true;
        }

        return strictlyBetter;
    }

    private bool HasStandardImplicitConversion(
        ManagedBoundType? source,
        ManagedBoundType destination) => source is null
        ? destination.IsReference
        : source.IsSameType(destination) ||
            _referenceConversions.IsImplicit(source, destination, _thread) ||
            _referenceConversions.IsImplicitBoxing(source, destination, _thread) ||
            ManagedPrimitiveConversionEvaluator.IsImplicitInvocationConversion(
                source, destination, _language);

    private static bool HasPairedSignature(
        ManagedUserDefinedOperator left,
        ManagedUserDefinedOperator right) =>
        left.ResultType.IsSameType(right.ResultType) &&
        left.ParameterTypes.Count == right.ParameterTypes.Count &&
        left.ParameterTypes.Zip(right.ParameterTypes).All(static pair =>
            pair.First.IsSameType(pair.Second));

    private static (string Primary, string? Fallback) GetMethodNames(
        DebugExpressionOperator operation) => operation switch
        {
            DebugExpressionOperator.UnaryPlus => ("op_UnaryPlus", null),
            DebugExpressionOperator.Negate => ("op_UnaryNegation", null),
            DebugExpressionOperator.CheckedNegate =>
                ("op_CheckedUnaryNegation", "op_UnaryNegation"),
            DebugExpressionOperator.LogicalNot => ("op_LogicalNot", null),
            DebugExpressionOperator.OnesComplement => ("op_OnesComplement", null),
            DebugExpressionOperator.Add => ("op_Addition", null),
            DebugExpressionOperator.CheckedAdd =>
                ("op_CheckedAddition", "op_Addition"),
            DebugExpressionOperator.Subtract => ("op_Subtraction", null),
            DebugExpressionOperator.CheckedSubtract =>
                ("op_CheckedSubtraction", "op_Subtraction"),
            DebugExpressionOperator.Multiply => ("op_Multiply", null),
            DebugExpressionOperator.CheckedMultiply =>
                ("op_CheckedMultiply", "op_Multiply"),
            DebugExpressionOperator.Divide => ("op_Division", null),
            DebugExpressionOperator.CheckedDivide =>
                ("op_CheckedDivision", "op_Division"),
            DebugExpressionOperator.Remainder => ("op_Modulus", null),
            DebugExpressionOperator.Equal => ("op_Equality", null),
            DebugExpressionOperator.NotEqual => ("op_Inequality", null),
            DebugExpressionOperator.LessThan => ("op_LessThan", null),
            DebugExpressionOperator.LessThanOrEqual => ("op_LessThanOrEqual", null),
            DebugExpressionOperator.GreaterThan => ("op_GreaterThan", null),
            DebugExpressionOperator.GreaterThanOrEqual => ("op_GreaterThanOrEqual", null),
            DebugExpressionOperator.BitwiseAnd => ("op_BitwiseAnd", null),
            DebugExpressionOperator.BitwiseOr => ("op_BitwiseOr", null),
            DebugExpressionOperator.ExclusiveOr => ("op_ExclusiveOr", null),
            _ => throw new NotSupportedException(
                $"User-defined operator {operation} is not supported.")
        };
}
