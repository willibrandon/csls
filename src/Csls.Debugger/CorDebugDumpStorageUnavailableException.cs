namespace Csls.Debugger;

/// <summary>
/// Identifies a captured value whose original storage was omitted or filtered by the dump writer.
/// </summary>
public sealed class CorDebugDumpStorageUnavailableException : IOException
{
    /// <summary>
    /// Creates an unavailable captured-storage failure.
    /// </summary>
    public CorDebugDumpStorageUnavailableException() : base("The original captured value storage is unavailable.")
    {
    }

    /// <summary>
    /// Creates a failure with the precise captured-storage reason.
    /// </summary>
    public CorDebugDumpStorageUnavailableException(string message) : base(message)
    {
    }

    /// <summary>
    /// Creates a captured-storage failure preserving its underlying read error.
    /// </summary>
    public CorDebugDumpStorageUnavailableException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
