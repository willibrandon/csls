using System.Reflection;

namespace Csls.Debugger;

/// <summary>
/// Checks reference conversions over exact loaded identities before any target mutation.
/// </summary>
internal sealed class ManagedReferenceConversion
{
    private const int MaximumWork = 4096;
    private const int MaximumDepth = 128;
    private readonly ManagedBoundTypeSystem _types;

    /// <summary>
    /// Creates a conversion checker backed by exact loaded metadata.
    /// </summary>
    internal ManagedReferenceConversion(ManagedBoundTypeSystem types)
    {
        ArgumentNullException.ThrowIfNull(types);
        _types = types;
    }

    /// <summary>
    /// Determines whether an implicit reference conversion exists without inspecting the current referent.
    /// </summary>
    internal bool IsImplicit(ManagedBoundType source, ManagedBoundType destination, nint thread)
    {
        int work = 0;
        return IsImplicitCore(source, destination, thread, depth: 0, ref work);
    }

    /// <summary>
    /// Determines whether a built-in explicit reference conversion exists between the declared types.
    /// </summary>
    internal bool IsExplicit(ManagedBoundType source, ManagedBoundType destination, nint thread)
    {
        int work = 0;
        return IsExplicitCore(source, destination, thread, depth: 0, ref work);
    }

    /// <summary>
    /// Checks an existing heap reference, including an already boxed value, against physical reference storage.
    /// </summary>
    internal bool IsRuntimeAssignable(ManagedBoundType source, ManagedBoundType destination, nint thread)
    {
        if (source.IsSameType(destination))
        {
            return true;
        }

        if (source.IsReference)
        {
            return IsImplicit(source, destination, thread);
        }

        if (!destination.IsReference)
        {
            return false;
        }

        if (destination.ElementType == 0x1c)
        {
            return true;
        }

        int work = 0;
        return _types.GetParents(source, thread).Any(
            parent => IsImplicitCore(parent, destination, thread, depth: 0, ref work));
    }

    private bool IsExplicitCore(ManagedBoundType source, ManagedBoundType destination, nint thread, int depth, ref int work)
    {
        CheckBudget(depth, ref work);
        if (!source.IsReference || !destination.IsReference)
        {
            return false;
        }

        if (IsImplicitCore(source, destination, thread, depth + 1, ref work) ||
            IsImplicitCore(destination, source, thread, depth + 1, ref work))
        {
            return true;
        }

        if (source.IsArray && destination.IsArray)
        {
            return source.ElementType == destination.ElementType && source.ArrayRank == destination.ArrayRank &&
                IsExplicitCore(source.TypeArguments[0], destination.TypeArguments[0], thread, depth + 1, ref work);
        }

        if (source.ElementType == 0x1d && _types.IsVectorInterface(destination, thread))
        {
            return IsExplicitCore(source.TypeArguments[0], destination.TypeArguments[0], thread, depth + 1, ref work);
        }

        if (destination.ElementType == 0x1d && _types.IsVectorInterface(source, thread))
        {
            return IsExplicitCore(source.TypeArguments[0], destination.TypeArguments[0], thread, depth + 1, ref work);
        }

        TypeAttributes sourceAttributes = _types.GetAttributes(source);
        TypeAttributes destinationAttributes = _types.GetAttributes(destination);
        bool sourceInterface = (sourceAttributes & TypeAttributes.Interface) != 0;
        bool destinationInterface = (destinationAttributes & TypeAttributes.Interface) != 0;
        bool betweenInterfaces = sourceInterface && destinationInterface;
        bool interfaceToOpenClass = sourceInterface && (destinationAttributes & TypeAttributes.Sealed) == 0;
        bool openClassToInterface = destinationInterface && (sourceAttributes & TypeAttributes.Sealed) == 0;
        if (betweenInterfaces || interfaceToOpenClass || openClassToInterface)
        {
            return true;
        }

        if (source.ModuleId == destination.ModuleId && source.DefinitionToken == destination.DefinitionToken &&
            source.TypeArguments.Count > 0 && source.TypeArguments.Count == destination.TypeArguments.Count &&
            _types.GetParents(source, thread).Any(parent => _types.IsCoreType(parent, "System.MulticastDelegate", thread)))
        {
            IReadOnlyList<GenericParameterAttributes> variance = _types.GetVariance(source);
            for (int index = 0; index < variance.Count; index++)
            {
                ManagedBoundType from = source.TypeArguments[index];
                ManagedBoundType to = destination.TypeArguments[index];
                if (from.IsSameType(to))
                {
                    continue;
                }

                if (!from.IsReference || !to.IsReference ||
                    variance[index] == GenericParameterAttributes.None ||
                    variance[index] == GenericParameterAttributes.Covariant &&
                    !IsExplicitCore(from, to, thread, depth + 1, ref work))
                {
                    return false;
                }
            }

            return true;
        }

        return false;
    }

    private bool IsImplicitCore(ManagedBoundType source, ManagedBoundType destination, nint thread, int depth, ref int work)
    {
        CheckBudget(depth, ref work);
        if (source.IsSameType(destination))
        {
            return true;
        }

        if (!source.IsReference || !destination.IsReference)
        {
            return false;
        }

        if (destination.ElementType == 0x1c)
        {
            return true;
        }

        if (source.IsArray && destination.IsArray)
        {
            return source.ElementType == destination.ElementType && source.ArrayRank == destination.ArrayRank &&
                IsCovariantElement(source.TypeArguments[0], destination.TypeArguments[0], thread, depth + 1, ref work);
        }

        if (source.ElementType == 0x1d && _types.IsVectorInterface(destination, thread))
        {
            return IsCovariantElement(source.TypeArguments[0], destination.TypeArguments[0], thread, depth + 1, ref work);
        }

        List<ManagedBoundType> visited = [];
        var pending = new Queue<ManagedBoundType>();
        pending.Enqueue(source);
        while (pending.TryDequeue(out ManagedBoundType? current))
        {
            CheckBudget(depth, ref work);
            if (visited.Any(current.IsSameType))
            {
                continue;
            }

            visited.Add(current);
            if (current.IsSameType(destination) || HasVariantConversion(current, destination, thread, depth + 1, ref work))
            {
                return true;
            }

            foreach (ManagedBoundType parent in _types.GetParents(current, thread))
            {
                pending.Enqueue(parent);
            }
        }

        return false;
    }

    private bool IsCovariantElement(
        ManagedBoundType source, ManagedBoundType destination, nint thread, int depth, ref int work) =>
        source.IsSameType(destination) ||
        (source.IsReference && destination.IsReference && IsImplicitCore(source, destination, thread, depth, ref work));

    private bool HasVariantConversion(
        ManagedBoundType source, ManagedBoundType destination, nint thread, int depth, ref int work)
    {
        if (source.IsArray || destination.IsArray || source.ModuleId != destination.ModuleId ||
            source.DefinitionToken != destination.DefinitionToken || source.TypeArguments.Count == 0 ||
            source.TypeArguments.Count != destination.TypeArguments.Count)
        {
            return false;
        }

        IReadOnlyList<GenericParameterAttributes> variance = _types.GetVariance(source);
        if (variance.Count != source.TypeArguments.Count)
        {
            throw new BadImageFormatException("A closed type does not match its declared generic arity.");
        }

        for (int index = 0; index < variance.Count; index++)
        {
            ManagedBoundType from = source.TypeArguments[index];
            ManagedBoundType to = destination.TypeArguments[index];
            if (from.IsSameType(to))
            {
                continue;
            }

            if (!from.IsReference || !to.IsReference)
            {
                return false;
            }

            bool compatible = variance[index] switch
            {
                GenericParameterAttributes.Covariant => IsImplicitCore(from, to, thread, depth, ref work),
                GenericParameterAttributes.Contravariant => IsImplicitCore(to, from, thread, depth, ref work),
                _ => false
            };
            if (!compatible)
            {
                return false;
            }
        }

        return true;
    }

    private static void CheckBudget(int depth, ref int work)
    {
        if (depth >= MaximumDepth || ++work > MaximumWork)
        {
            throw new InvalidOperationException("The reference conversion exceeds its bounded type-graph budget.");
        }
    }
}
