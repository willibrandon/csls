using System.Text;

namespace Csls.Debugger.Tests;

/// <summary>
/// Preserves bounded capture records in a file that remains readable throughout the owned process lifetime.
/// </summary>
internal sealed class DebuggerCaptureTrace : IDisposable
{
    private readonly Lock _gate = new();
    private readonly StreamWriter _writer;
    private int _remainingCharacters = 65536;

    /// <summary>
    /// Creates an exclusively named diagnostic file with immediately flushed UTF-8 records.
    /// </summary>
    /// <param name="path">The new diagnostic file in the caller's existing owned directory.</param>
    internal DebuggerCaptureTrace(string path)
    {
        FilePath = path;
        _writer = new StreamWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        { AutoFlush = true };
    }

    /// <summary>
    /// Gets the exclusively owned artifact path for result registration and independent inspection.
    /// </summary>
    internal string FilePath { get; }

    /// <summary>
    /// Publishes one complete record before returning, with a single marker when the character budget is exhausted.
    /// </summary>
    /// <param name="record">The process output or observation to retain.</param>
    internal void WriteLine(string record)
    {
        lock (_gate)
        {
            if (_remainingCharacters < 0)
            {
                return;
            }
            if (record.Length > _remainingCharacters - Environment.NewLine.Length)
            {
                _writer.WriteLine("Capture log truncated.");
                _remainingCharacters = -1;
                return;
            }
            _writer.WriteLine(record);
            _remainingCharacters -= record.Length + Environment.NewLine.Length;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            _writer.Dispose();
        }
    }
}
