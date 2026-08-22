namespace Csls.Control.Contracts;

/// <summary>
/// Defines the explicitly registered methods implemented by a csls control session.
/// </summary>
public interface IControlRpcTarget
{
    /// <summary>
    /// Gets the current language-server session state.
    /// </summary>
    /// <param name="cancellationToken">The peer cancellation token.</param>
    /// <returns>The current session information.</returns>
    Task<ControlSessionInfo> GetSessionAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Resolves hover information from the current workspace snapshot.
    /// </summary>
    /// <param name="request">The absolute document path and UTF-16 position.</param>
    /// <param name="cancellationToken">The peer cancellation token.</param>
    /// <returns>The optional hover information.</returns>
    Task<ControlHoverResult> GetHoverAsync(
        ControlHoverRequest request,
        CancellationToken cancellationToken);
}
