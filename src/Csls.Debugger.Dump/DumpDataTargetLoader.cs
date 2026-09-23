using Microsoft.Diagnostics.Runtime;
using Microsoft.Diagnostics.Runtime.DataReaders.Implementation;
using System.Runtime.InteropServices;

namespace Csls.Debugger.Dump;

/// <summary>
/// Completes native dump-reader initialization before transferring ownership of the captured file.
/// </summary>
internal static class DumpDataTargetLoader
{
    /// <summary>
    /// Opens a captured target and observes its background minidump thread-table read while the file remains owned.
    /// </summary>
    /// <param name="path">The captured process file.</param>
    /// <param name="options">The caller's local image and memory-reader policy.</param>
    /// <param name="cancellationToken">Cancels opening and traversal after native initialization completes.</param>
    /// <returns>The initialized captured target owned by the caller.</returns>
    internal static DataTarget Open(string path, DataTargetOptions options, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var target = DataTarget.LoadDump(path, options);
        try
        {
            if (target.DataReader.TargetPlatform == OSPlatform.Windows)
            {
                CompleteThreadRead(target.DataReader, cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            return target;
        }
        catch
        {
            target.Dispose();
            throw;
        }
    }

    private static void CompleteThreadRead(IDataReader reader, CancellationToken cancellationToken)
    {
        if (reader is not IThreadReader threads)
        {
            throw new InvalidDataException("The minidump reader does not expose its captured thread identities.");
        }
        try
        {
            // Enumerating minidump thread identities joins the reader's asynchronous file work.
            // Observe that completion before cancellation can dispose the file used by the read.
            using IEnumerator<uint> ids = threads.EnumerateOSThreadIds().GetEnumerator();
            while (ids.MoveNext())
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
        }
        catch (AggregateException exception) when (exception.InnerExceptions is [InvalidDataException invalid])
        {
            throw new InvalidDataException(invalid.Message, invalid);
        }
    }
}
