using Csls.Debugger.Interop;

namespace Csls.Debugger;

/// <summary>
/// Owns one multi-stage Results View construction and enumeration operation.
/// </summary>
internal sealed class ManagedResultsViewEvaluation
{
    private nint _itemsGetter;
    private nint _constructor;
    private nint[] _constructorTypeArguments;
    private nint _retainedEnumerableValue;

    /// <summary>
    /// Creates Results View presentation state for the remaining getter stage.
    /// </summary>
    /// <param name="itemsGetter">The owned debug-view Items getter function.</param>
    /// <param name="enumerableArgument">The original value supplied to the debug-view constructor.</param>
    /// <param name="retainedEnumerableValue">The owned unboxed struct receiver, or zero.</param>
    /// <param name="constructor">The owned constructor deferred until receiver boxing, or zero.</param>
    /// <param name="constructorTypeArguments">The owned constructor arguments deferred until boxing.</param>
    internal ManagedResultsViewEvaluation(
        nint itemsGetter,
        ManagedExpressionValue enumerableArgument,
        nint retainedEnumerableValue,
        nint constructor,
        nint[] constructorTypeArguments)
    {
        ArgumentOutOfRangeException.ThrowIfZero(itemsGetter);
        _itemsGetter = itemsGetter;
        EnumerableArgument = enumerableArgument;
        _retainedEnumerableValue = retainedEnumerableValue;
        _constructor = constructor;
        _constructorTypeArguments = constructorTypeArguments;
    }

    /// <summary>
    /// Gets the original enumerable argument used after optional receiver boxing.
    /// </summary>
    internal ManagedExpressionValue EnumerableArgument { get; }

    /// <summary>
    /// Gets the owned unboxed receiver whose lifetime requires only COM release.
    /// </summary>
    internal nint RetainedEnumerableValue => _retainedEnumerableValue;

    /// <summary>
    /// Gets or sets whether the runtime produced the boxed enumerable copy.
    /// </summary>
    internal bool EnumerableBoxingCompleted { get; set; }

    /// <summary>
    /// Transfers the deferred constructor to the active evaluation stage.
    /// </summary>
    /// <returns>The owned constructor function.</returns>
    internal nint DetachConstructor() => Interlocked.Exchange(ref _constructor, 0);

    /// <summary>
    /// Transfers the deferred constructor's exact runtime type arguments.
    /// </summary>
    /// <returns>The owned constructor type arguments.</returns>
    internal nint[] DetachConstructorTypeArguments() =>
        Interlocked.Exchange(ref _constructorTypeArguments, []);

    /// <summary>
    /// Gets or sets whether CoreCLR completed debug-view construction.
    /// </summary>
    internal bool ConstructorCompleted { get; set; }

    /// <summary>
    /// Transfers the Items getter to the active evaluation stage.
    /// </summary>
    /// <returns>The owned Items getter function.</returns>
    internal nint DetachItemsGetter()
    {
        nint getter = Interlocked.Exchange(ref _itemsGetter, 0);
        return getter != 0
            ? getter
            : throw new InvalidOperationException(
                "The Results View evaluation no longer owns its Items getter.");
    }

    /// <summary>
    /// Releases retained values and functions that were not transferred to active stages.
    /// </summary>
    internal void Release()
    {
        nint getter = Interlocked.Exchange(ref _itemsGetter, 0);
        if (getter != 0)
        {
            _ = ComAbi.Release(getter);
        }

        nint constructor = Interlocked.Exchange(ref _constructor, 0);
        if (constructor != 0)
        {
            _ = ComAbi.Release(constructor);
        }

        nint enumerable = Interlocked.Exchange(ref _retainedEnumerableValue, 0);
        if (enumerable != 0)
        {
            _ = ComAbi.Release(enumerable);
        }

        foreach (nint argument in Interlocked.Exchange(ref _constructorTypeArguments, []))
        {
            _ = ComAbi.Release(argument);
        }
    }
}
