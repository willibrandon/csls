namespace Csls.Debugger.Contracts;

/// <summary>
/// Extends debugger control with bounded target-output inspection.
/// </summary>
public partial interface IDebuggerControlTarget
{
    /// <summary>
    /// Gets a bounded target-output page after a stable sequence cursor.
    /// </summary>
    /// <param name="request">The output cursor and maximum entry count.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The retained output page.</returns>
    Task<DebugOutputPage> GetOutputAsync(
        DebugOutputRequest request,
        CancellationToken cancellationToken);
}
