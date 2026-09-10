namespace Csls.Mcp.Worker;

/// <summary>
/// Represents an expected debugger tool failure with a stable machine-readable code.
/// </summary>
internal sealed class McpDebuggerException : Exception
{
    private const string DefaultCode = "debugger_operation_failed";

    /// <summary>
    /// Creates a debugger operation failure without additional detail.
    /// </summary>
    public McpDebuggerException()
        : this(DefaultCode, "The debugger operation failed.")
    {
    }

    /// <summary>
    /// Creates a debugger operation failure with human-readable detail.
    /// </summary>
    /// <param name="message">The human-readable failure detail.</param>
    public McpDebuggerException(string message)
        : this(DefaultCode, message)
    {
    }

    /// <summary>
    /// Creates a debugger operation failure caused by another exception.
    /// </summary>
    /// <param name="message">The human-readable failure detail.</param>
    /// <param name="innerException">The underlying failure.</param>
    public McpDebuggerException(string message, Exception innerException)
        : base(message, innerException)
    {
        Code = DefaultCode;
    }

    /// <summary>
    /// Creates an expected debugger tool failure.
    /// </summary>
    /// <param name="code">The stable debugger error code.</param>
    /// <param name="message">The human-readable failure detail.</param>
    internal McpDebuggerException(string code, string message)
        : base(message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        Code = code;
    }

    /// <summary>
    /// Gets the stable machine-readable debugger error code.
    /// </summary>
    internal string Code { get; }
}
