using System.Diagnostics;
using System.Globalization;

namespace Csls.Debugger;

/// <summary>
/// Records bounded, explicitly enabled source-step decisions in a caller-selected diagnostic file.
/// </summary>
internal sealed class ManagedStepTrace
{
    private const int MaximumRecords = 256;
    private const int MaximumMessageLength = 1024;
    private static readonly Lazy<ManagedStepTrace?> s_current = new(FromEnvironment);
    private readonly Lock _gate = new();
    private readonly string _path;
    private readonly long _started = Stopwatch.GetTimestamp();
    private int _records;
    private Exception? _failure;

    /// <summary>
    /// Creates a new diagnostic file without replacing existing evidence.
    /// </summary>
    /// <param name="path">The absolute path of the new diagnostic file.</param>
    internal ManagedStepTrace(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException("The step trace path must be absolute.", nameof(path));
        }

        _path = path;
        using FileStream file = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
    }

    /// <summary>
    /// Gets the explicitly selected process-wide trace shared across target restarts.
    /// </summary>
    internal static ManagedStepTrace? Current => s_current.Value;

    /// <summary>
    /// Gets the file failure that stopped recording while leaving debugger execution unchanged.
    /// </summary>
    internal Exception? Failure
    {
        get
        {
            lock (_gate)
            {
                return _failure;
            }
        }
    }

    /// <summary>
    /// Appends one bounded decision and closes the file before returning to runtime callback handling.
    /// </summary>
    /// <param name="message">The internal step decision and its runtime identifiers.</param>
    internal void Write(FormattableString message)
    {
        lock (_gate)
        {
            if (_failure is not null || _records >= MaximumRecords)
            {
                return;
            }

            string text = message.ToString(CultureInfo.InvariantCulture);
            if (text.Length > MaximumMessageLength)
            {
                text = text[..MaximumMessageLength];
            }

            string record = FormattableString.Invariant(
                $"{++_records}: {Stopwatch.GetElapsedTime(_started).TotalMilliseconds:F3} ms {text}{Environment.NewLine}");
            try
            {
                File.AppendAllText(_path, record);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Diagnostic storage failure must not interrupt native breakpoint and handle ownership changes.
                _failure = exception;
                Debug.WriteLine(exception);
            }
        }
    }

    private static ManagedStepTrace? FromEnvironment()
    {
        string? path = Environment.GetEnvironmentVariable("CSLS_DEBUGGER_STEP_TRACE");
        return string.IsNullOrEmpty(path) ? null : new ManagedStepTrace(path);
    }
}
