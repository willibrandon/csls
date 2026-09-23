namespace Csls.Debugger;

/// <summary>
/// Owns a dynamically sized set of disposable resources through one structured lifetime.
/// </summary>
/// <typeparam name="T">The disposable resource type.</typeparam>
internal sealed class DisposableCollection<T> : IDisposable
    where T : class, IDisposable
{
    private readonly List<T> _values = [];

    /// <summary>
    /// Acquires one resource and transfers it to this collection without a leak window.
    /// </summary>
    /// <param name="factory">The factory that creates the resource.</param>
    /// <returns>The resource now owned by this collection.</returns>
    internal T Acquire(Func<T> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        using var owner = new DisposableOwner<T>();
        owner.Acquire(factory);
        T value = owner.Value
            ?? throw new InvalidOperationException("The disposable factory returned no value.");
        _values.Add(value);
        return owner.Detach();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _values.ForEach(static value => value.Dispose());
        _values.Clear();
    }
}
