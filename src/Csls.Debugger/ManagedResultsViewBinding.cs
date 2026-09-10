using Csls.Debugger.Interop;

namespace Csls.Debugger;

/// <summary>
/// Owns the constructor, Items getter, and closed arguments for one Results View.
/// </summary>
internal sealed class ManagedResultsViewBinding
{
    private nint _constructor;
    private nint _itemsGetter;
    private nint[] _typeArguments;

    /// <summary>
    /// Creates one owned Results View runtime binding.
    /// </summary>
    /// <param name="constructor">The owned debug-view constructor function.</param>
    /// <param name="itemsGetter">The owned debug-view Items getter function.</param>
    /// <param name="typeArguments">The owned closed debug-view type arguments.</param>
    internal ManagedResultsViewBinding(
        nint constructor,
        nint itemsGetter,
        nint[] typeArguments)
    {
        ArgumentOutOfRangeException.ThrowIfZero(constructor);
        ArgumentOutOfRangeException.ThrowIfZero(itemsGetter);
        ArgumentNullException.ThrowIfNull(typeArguments);
        _constructor = constructor;
        _itemsGetter = itemsGetter;
        _typeArguments = typeArguments;
    }

    /// <summary>
    /// Transfers the owned constructor function to an active evaluation.
    /// </summary>
    /// <returns>The owned constructor function.</returns>
    internal nint DetachConstructor() => Detach(ref _constructor, "constructor");

    /// <summary>
    /// Transfers the owned Items getter to an active evaluation.
    /// </summary>
    /// <returns>The owned Items getter function.</returns>
    internal nint DetachItemsGetter() => Detach(ref _itemsGetter, "Items getter");

    /// <summary>
    /// Transfers the owned closed runtime type arguments to an active evaluation.
    /// </summary>
    /// <returns>The owned runtime type arguments.</returns>
    internal nint[] DetachTypeArguments()
    {
        nint[] arguments = _typeArguments;
        _typeArguments = [];
        return arguments;
    }

    /// <summary>
    /// Releases every runtime pointer that has not been transferred.
    /// </summary>
    internal void Release()
    {
        ReleasePointer(Interlocked.Exchange(ref _constructor, 0));
        ReleasePointer(Interlocked.Exchange(ref _itemsGetter, 0));
        nint[] arguments = Interlocked.Exchange(ref _typeArguments, []);
        foreach (nint argument in arguments)
        {
            ReleasePointer(argument);
        }
    }

    private static nint Detach(ref nint field, string description)
    {
        nint pointer = Interlocked.Exchange(ref field, 0);
        return pointer != 0
            ? pointer
            : throw new InvalidOperationException(
                $"The Results View binding no longer owns its {description}.");
    }

    private static void ReleasePointer(nint pointer)
    {
        if (pointer != 0)
        {
            _ = ComAbi.Release(pointer);
        }
    }
}
