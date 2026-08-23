namespace Csls.Core;

/// <summary>
/// Identifies the current or final lifecycle state of one scheduled request.
/// </summary>
public enum RequestExecutionStatus
{
    /// <summary>
    /// Indicates that the request is waiting for execution.
    /// </summary>
    Queued,

    /// <summary>
    /// Indicates that the request operation is executing.
    /// </summary>
    Running,

    /// <summary>
    /// Indicates that the request completed successfully.
    /// </summary>
    Succeeded,

    /// <summary>
    /// Indicates that cancellation ended the request.
    /// </summary>
    Canceled,

    /// <summary>
    /// Indicates that an exception ended the request.
    /// </summary>
    Failed
}
