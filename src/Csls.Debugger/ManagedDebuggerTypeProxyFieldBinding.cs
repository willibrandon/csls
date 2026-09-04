using Csls.Debugger.Interop;

namespace Csls.Debugger;

/// <summary>
/// Owns one resolved debugger proxy field and its presentation metadata.
/// </summary>
internal sealed class ManagedDebuggerTypeProxyFieldBinding
{
    private nint _declaringClass;

    /// <summary>
    /// Creates an owned debugger proxy field binding.
    /// </summary>
    /// <param name="name">The metadata field name and ordinal sort key.</param>
    /// <param name="fieldToken">The metadata field-definition token.</param>
    /// <param name="browsingState">The effective debugger browsing policy.</param>
    /// <param name="tupleCustomTypeInfo">The optional tuple-name transforms.</param>
    /// <param name="memberDisplay">The optional member display template.</param>
    /// <param name="inheritanceLevel">The zero-based distance from the runtime type.</param>
    /// <param name="declaringClass">The owned ICorDebugClass pointer.</param>
    internal ManagedDebuggerTypeProxyFieldBinding(
        string name,
        uint fieldToken,
        ManagedDebuggerBrowsableState browsingState,
        ManagedTupleCustomTypeInfo? tupleCustomTypeInfo,
        ManagedDebuggerDisplayAttribute? memberDisplay,
        int inheritanceLevel,
        nint declaringClass)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentOutOfRangeException.ThrowIfZero(fieldToken);
        ArgumentOutOfRangeException.ThrowIfNegative(inheritanceLevel);
        ArgumentOutOfRangeException.ThrowIfZero(declaringClass);
        Name = name;
        FieldToken = fieldToken;
        BrowsingState = browsingState;
        TupleCustomTypeInfo = tupleCustomTypeInfo;
        MemberDisplay = memberDisplay;
        InheritanceLevel = inheritanceLevel;
        _declaringClass = declaringClass;
    }

    /// <summary>
    /// Gets the metadata field name and ordinal sort key.
    /// </summary>
    internal string Name { get; }

    /// <summary>
    /// Gets the metadata field-definition token.
    /// </summary>
    internal uint FieldToken { get; }

    /// <summary>
    /// Gets the effective debugger browsing policy.
    /// </summary>
    internal ManagedDebuggerBrowsableState BrowsingState { get; }

    /// <summary>
    /// Gets the optional tuple-name transforms.
    /// </summary>
    internal ManagedTupleCustomTypeInfo? TupleCustomTypeInfo { get; }

    /// <summary>
    /// Gets the optional member display template.
    /// </summary>
    internal ManagedDebuggerDisplayAttribute? MemberDisplay { get; }

    /// <summary>
    /// Gets the zero-based distance from the runtime type.
    /// </summary>
    internal int InheritanceLevel { get; }

    /// <summary>
    /// Gets the retained ICorDebugClass pointer while the binding remains owned.
    /// </summary>
    internal nint DeclaringClass => _declaringClass != 0
        ? _declaringClass
        : throw new InvalidOperationException(
            $"Proxy field '{Name}' no longer owns its declaring class.");

    /// <summary>
    /// Releases the retained declaring class.
    /// </summary>
    internal void Release()
    {
        nint declaringClass = Interlocked.Exchange(ref _declaringClass, 0);
        if (declaringClass != 0)
        {
            _ = ComAbi.Release(declaringClass);
        }
    }
}
