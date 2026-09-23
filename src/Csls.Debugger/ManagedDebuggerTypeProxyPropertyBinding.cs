using Csls.Debugger.Interop;

namespace Csls.Debugger;

/// <summary>
/// Owns one resolved proxy property getter and its debugger presentation metadata.
/// </summary>
internal sealed class ManagedDebuggerTypeProxyPropertyBinding
{
    private nint _function;
    private nint _declaringType;

    /// <summary>
    /// Creates an owned proxy property getter binding.
    /// </summary>
    /// <param name="name">The debugger-facing property name.</param>
    /// <param name="declaredType">The metadata return-type display.</param>
    /// <param name="browsingState">The declared debugger browsing policy.</param>
    /// <param name="isStatic">Whether the getter is static.</param>
    /// <param name="function">The owned ICorDebugFunction pointer.</param>
    /// <param name="declaringType">The borrowed exact declaring type of the getter.</param>
    internal ManagedDebuggerTypeProxyPropertyBinding(
        string name,
        string declaredType,
        ManagedDebuggerBrowsableState browsingState,
        bool isStatic,
        nint function,
        nint declaringType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(declaredType);
        ArgumentOutOfRangeException.ThrowIfZero(function);
        ArgumentOutOfRangeException.ThrowIfZero(declaringType);
        Name = name;
        DeclaredType = declaredType;
        BrowsingState = browsingState;
        IsStatic = isStatic;
        _function = function;
        _ = ComAbi.AddRef(declaringType);
        _declaringType = declaringType;
    }

    /// <summary>
    /// Gets the debugger-facing property name.
    /// </summary>
    internal string Name { get; }

    /// <summary>
    /// Gets the metadata return-type display.
    /// </summary>
    internal string DeclaredType { get; }

    /// <summary>
    /// Gets the debugger browsing policy declared on the property.
    /// </summary>
    internal ManagedDebuggerBrowsableState BrowsingState { get; }

    /// <summary>
    /// Gets whether the getter is static.
    /// </summary>
    internal bool IsStatic { get; }

    /// <summary>
    /// Transfers the owned ICorDebugFunction pointer to an active evaluation.
    /// </summary>
    /// <returns>The owned ICorDebugFunction pointer.</returns>
    internal nint DetachFunction()
    {
        nint function = _function;
        _function = 0;
        return function != 0
            ? function
            : throw new InvalidOperationException(
                $"Proxy property '{Name}' no longer owns its getter function.");
    }

    /// <summary>
    /// Retains the exact type arguments required by this property's declaring type.
    /// </summary>
    /// <returns>The owned declaring-type arguments for the active call.</returns>
    internal nint[] RetainTypeArguments() => ManagedRuntimeTypeArguments.Retain(_declaringType);

    /// <summary>
    /// Releases the declaring type and getter function that have not been transferred.
    /// </summary>
    internal void Release()
    {
        nint declaringType = Interlocked.Exchange(ref _declaringType, 0);
        if (declaringType != 0)
        {
            _ = ComAbi.Release(declaringType);
        }

        nint function = Interlocked.Exchange(ref _function, 0);
        if (function != 0)
        {
            _ = ComAbi.Release(function);
        }
    }
}
