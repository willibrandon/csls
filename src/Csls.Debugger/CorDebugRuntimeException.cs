namespace Csls.Debugger;

/// <summary>
/// Preserves an unrecoverable CoreCLR debugging-services failure and its native error code.
/// </summary>
internal sealed class CorDebugRuntimeException : InvalidOperationException
{
    /// <summary>
    /// Creates an unrecoverable debugging-services failure without native error details.
    /// </summary>
    public CorDebugRuntimeException()
        : base("CoreCLR debugging services encountered an unrecoverable error.")
    {
    }

    /// <summary>
    /// Creates an unrecoverable debugging-services failure with a diagnostic message.
    /// </summary>
    /// <param name="message">The failure description.</param>
    public CorDebugRuntimeException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Creates an unrecoverable debugging-services failure caused by another error.
    /// </summary>
    /// <param name="message">The failure description.</param>
    /// <param name="innerException">The error that caused the failure.</param>
    public CorDebugRuntimeException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>
    /// Creates the failure reported by the runtime's DebuggerError callback.
    /// </summary>
    /// <param name="errorHResult">The original failing runtime HRESULT.</param>
    /// <param name="errorCode">The runtime-specific error code.</param>
    internal CorDebugRuntimeException(int errorHResult, uint errorCode)
        : base(
            $"CoreCLR disabled debugging services after an unrecoverable error " +
            $"(HRESULT 0x{errorHResult:X8}, error code 0x{errorCode:X8}).")
    {
        HResult = errorHResult;
        ErrorCode = errorCode;
    }

    /// <summary>
    /// Gets the runtime-specific code supplied with the failing HRESULT.
    /// </summary>
    internal uint ErrorCode { get; }
}
