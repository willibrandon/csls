namespace Csls.Protocol;

/// <summary>
/// Represents an LSP request that the server cancels because its input snapshot became stale.
/// </summary>
public sealed class LspServerCancelledException : Exception
{
    /// <summary>
    /// Gets the LSP error code for a request cancelled by the server.
    /// </summary>
    public const int ErrorCode = -32802;

    /// <summary>
    /// Initializes a server-cancelled request error without a detail message.
    /// </summary>
    public LspServerCancelledException()
    {
        CancellationData = new DiagnosticServerCancellationData();
    }

    /// <summary>
    /// Initializes a server-cancelled request error with a detail message.
    /// </summary>
    /// <param name="message">The reason the request was cancelled.</param>
    public LspServerCancelledException(string message)
        : base(message)
    {
        CancellationData = new DiagnosticServerCancellationData();
    }

    /// <summary>
    /// Initializes a server-cancelled request error with a detail message and inner failure.
    /// </summary>
    /// <param name="message">The reason the request was cancelled.</param>
    /// <param name="innerException">The failure that caused the cancellation.</param>
    public LspServerCancelledException(string message, Exception innerException)
        : base(message, innerException)
    {
        CancellationData = new DiagnosticServerCancellationData();
    }

    /// <summary>
    /// Initializes a server-cancelled request error.
    /// </summary>
    /// <param name="message">The reason the request could not return a current result.</param>
    /// <param name="retriggerRequest">Whether the client should repeat the request.</param>
    public LspServerCancelledException(string message, bool retriggerRequest)
        : base(message)
    {
        CancellationData = new DiagnosticServerCancellationData
        {
            RetriggerRequest = retriggerRequest
        };
    }

    /// <summary>
    /// Gets the diagnostic cancellation behavior returned in the LSP error data.
    /// </summary>
    public DiagnosticServerCancellationData CancellationData { get; }
}
