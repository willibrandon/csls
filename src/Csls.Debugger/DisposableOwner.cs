namespace Csls.Debugger;

/// <summary>
/// Makes temporary ownership and successful transfer of one disposable resource explicit.
/// </summary>
/// <typeparam name="T">The disposable resource type.</typeparam>
internal sealed class DisposableOwner<T> : IDisposable
    where T : class, IDisposable
{
    private T? _value;

    /// <summary>
    /// Creates an owner with no acquired disposable value.
    /// </summary>
    internal DisposableOwner()
    {
    }

    /// <summary>
    /// Gets the currently owned value, or null before acquisition or after transfer.
    /// </summary>
    internal T? Value => _value;

    /// <summary>
    /// Acquires exactly one disposable value from a deferred factory.
    /// </summary>
    /// <param name="factory">The factory that creates the optional owned value.</param>
    internal void Acquire(Func<T?> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        T? value = factory();
        if (value is null)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _value, value, null) is not null)
        {
            value.Dispose();
            throw new InvalidOperationException("A disposable value is already owned.");
        }
    }

    /// <summary>
    /// Transfers the currently owned value to its successful long-lived owner.
    /// </summary>
    /// <returns>The detached disposable value.</returns>
    internal T Detach() => Interlocked.Exchange(ref _value, null)
        ?? throw new InvalidOperationException("No disposable value is currently owned.");

    /// <inheritdoc />
    public void Dispose() => Interlocked.Exchange(ref _value, null)?.Dispose();
}
