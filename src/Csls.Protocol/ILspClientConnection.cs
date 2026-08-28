using System.Text.Json;

namespace Csls.Protocol;

/// <summary>
/// Sends the supported server-to-client LSP requests and notifications.
/// </summary>
public interface ILspClientConnection
{
    /// <summary>
    /// Requests ordered configuration sections from the connected client.
    /// </summary>
    /// <param name="parameters">The ordered configuration section requests.</param>
    /// <param name="cancellationToken">The request cancellation token.</param>
    /// <returns>The client values in the same order as the requested sections.</returns>
    Task<JsonElement?[]> GetConfigurationAsync(
        ConfigurationParams parameters,
        CancellationToken cancellationToken);

    /// <summary>
    /// Creates one server-owned work-done progress token in the connected client.
    /// </summary>
    /// <param name="parameters">The unique server-generated progress token.</param>
    /// <param name="cancellationToken">The request cancellation token.</param>
    /// <returns>A task that completes after the client accepts the token.</returns>
    Task CreateWorkDoneProgressAsync(
        WorkDoneProgressCreateParams parameters,
        CancellationToken cancellationToken);

    /// <summary>
    /// Publishes one work-done progress value to the connected client.
    /// </summary>
    /// <param name="parameters">The token and typed work-done progress value.</param>
    /// <returns>A task that completes after the notification is written.</returns>
    Task PublishWorkDoneProgressAsync(WorkDoneProgressParams parameters);

    /// <summary>
    /// Publishes one workspace diagnostic partial-result batch.
    /// </summary>
    /// <param name="parameters">The client token and next diagnostic batch.</param>
    /// <returns>A task that completes after the notification is written.</returns>
    Task PublishWorkspaceDiagnosticProgressAsync(WorkspaceDiagnosticProgressParams parameters);

    /// <summary>
    /// Replaces the connected client's diagnostic state for one document.
    /// </summary>
    /// <param name="parameters">The document version and complete diagnostic collection.</param>
    /// <returns>A task that completes after the notification is written.</returns>
    Task PublishDiagnosticsAsync(PublishDiagnosticsParams parameters);

    /// <summary>
    /// Registers server-requested capabilities with the connected client.
    /// </summary>
    /// <param name="parameters">The ordered capability registrations.</param>
    /// <param name="cancellationToken">The request cancellation token.</param>
    /// <returns>A task that completes after the client accepts the registrations.</returns>
    Task RegisterCapabilityAsync(
        RegistrationParams parameters,
        CancellationToken cancellationToken);

    /// <summary>
    /// Requests that the connected client refresh pull diagnostics.
    /// </summary>
    /// <param name="cancellationToken">The request cancellation token.</param>
    /// <returns>A task that completes after the client accepts the refresh.</returns>
    Task RefreshDiagnosticsAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Requests that the connected client refresh inlay hints.
    /// </summary>
    /// <param name="cancellationToken">The request cancellation token.</param>
    /// <returns>A task that completes after the client accepts the refresh.</returns>
    Task RefreshInlayHintsAsync(CancellationToken cancellationToken);
}
