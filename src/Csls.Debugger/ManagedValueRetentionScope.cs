namespace Csls.Debugger;

/// <summary>
/// Releases unpublished expression values when their synchronous actor operation ends.
/// </summary>
/// <param name="values">The operation's runtime-value ownership graph.</param>
/// <param name="complete">The owning debuggee's value-release operation.</param>
internal sealed class ManagedValueRetentionScope(
    ManagedExpressionValues values,
    Action<ManagedExpressionValues> complete) : IDisposable
{
    private bool _disposed;

    /// <summary>
    /// Transfers a result and its physical owners to the stopped generation.
    /// </summary>
    /// <param name="reference">The returned expansion handle, or zero for a scalar result.</param>
    internal void Preserve(int reference) => values.Preserve(reference);

    /// <summary>
    /// Keeps binding values until their active function evaluation retires the generation.
    /// </summary>
    internal void PreserveAll() => values.PreserveAll();

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        complete(values);
    }
}
