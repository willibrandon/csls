using Microsoft.Diagnostics.Runtime;

namespace Csls.Debugger.Dump;

/// <summary>
/// Retains a newly created captured-data reader until its data target accepts ownership.
/// </summary>
internal sealed class DumpDataReaderLease : IDisposable
{
    private IDataReader? _reader;

    /// <summary>
    /// Acquires one reader from a deferred factory.
    /// </summary>
    /// <param name="factory">Creates the owned captured-data reader.</param>
    internal DumpDataReaderLease(Func<IDataReader> factory)
    {
        _reader = factory();
    }

    /// <summary>
    /// Transfers the reader to a newly constructed data target after construction succeeds.
    /// </summary>
    /// <param name="options">The caller's reader and image-location policy.</param>
    /// <returns>The data target that now owns the reader.</returns>
    internal DataTarget CreateTarget(DataTargetOptions options)
    {
        var target = new DataTarget(_reader ?? throw new InvalidOperationException("The reader was already transferred."), options);
        _reader = null;
        return target;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _reader, null) is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }
}
