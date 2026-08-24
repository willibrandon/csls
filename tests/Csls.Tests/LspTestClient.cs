using Csls.Protocol;
using System.Text.Json;
using System.Threading.Channels;

namespace Csls.Tests;

/// <summary>
/// Implements the client side of real bidirectional LSP requests and notifications.
/// </summary>
internal sealed class LspTestClient
{
    private readonly Lock _gate = new();
    private readonly Channel<int> _configurationRequests = Channel.CreateUnbounded<int>(
        new UnboundedChannelOptions
        {
            AllowSynchronousContinuations = false,
            SingleReader = true,
            SingleWriter = false
        });
    private readonly Channel<WorkspaceDiagnosticProgressParams> _workspaceDiagnosticProgress =
        Channel.CreateUnbounded<WorkspaceDiagnosticProgressParams>(
            new UnboundedChannelOptions
            {
                AllowSynchronousContinuations = false,
                SingleReader = true,
                SingleWriter = false
            });
    private JsonElement? _legacyConfiguration;
    private JsonElement? _preferredConfiguration;
    private int _configurationRequestCount;

    /// <summary>
    /// Creates a client with independently controlled legacy and preferred sections.
    /// </summary>
    /// <param name="legacyConfiguration">The legacy csharp section JSON.</param>
    /// <param name="preferredConfiguration">The preferred csls section JSON.</param>
    internal LspTestClient(string? legacyConfiguration, string? preferredConfiguration)
    {
        SetConfiguration(legacyConfiguration, preferredConfiguration);
    }

    /// <summary>
    /// Replaces the configuration returned by subsequent server pull requests.
    /// </summary>
    /// <param name="legacyConfiguration">The legacy csharp section JSON.</param>
    /// <param name="preferredConfiguration">The preferred csls section JSON.</param>
    internal void SetConfiguration(
        string? legacyConfiguration,
        string? preferredConfiguration)
    {
        JsonElement? legacy = Parse(legacyConfiguration);
        JsonElement? preferred = Parse(preferredConfiguration);
        lock (_gate)
        {
            _legacyConfiguration = legacy;
            _preferredConfiguration = preferred;
        }
    }

    /// <summary>
    /// Waits until the server starts the specified configuration pull request.
    /// </summary>
    /// <param name="expectedCount">The one-based request count to observe.</param>
    /// <param name="cancellationToken">The test cancellation token.</param>
    /// <returns>A task that completes after the request crosses the real RPC boundary.</returns>
    internal async Task WaitForConfigurationRequestAsync(
        int expectedCount,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(expectedCount);
        while (Volatile.Read(ref _configurationRequestCount) < expectedCount)
        {
            await _configurationRequests.Reader
                .ReadAsync(cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Returns configuration values in the exact order requested by the server.
    /// </summary>
    /// <param name="parameters">The server's ordered configuration request.</param>
    /// <param name="cancellationToken">The request cancellation token.</param>
    /// <returns>The matching nullable configuration values.</returns>
    internal Task<JsonElement?[]> GetConfigurationAsync(
        ConfigurationParams parameters,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        cancellationToken.ThrowIfCancellationRequested();
        int requestCount = Interlocked.Increment(ref _configurationRequestCount);
        if (!_configurationRequests.Writer.TryWrite(requestCount))
        {
            throw new InvalidOperationException("The configuration request could not be observed.");
        }

        lock (_gate)
        {
            JsonElement?[] values =
            [
                .. parameters.Items.Select(item => item.Section switch
                {
                    "csharp" => _legacyConfiguration,
                    "csls" => _preferredConfiguration,
                    _ => null
                })
            ];
            return Task.FromResult(values);
        }
    }

    /// <summary>
    /// Records one workspace diagnostic partial result received over the real LSP connection.
    /// </summary>
    /// <param name="parameters">The partial result token and diagnostic batch.</param>
    /// <returns>A completed task after the notification is retained.</returns>
    internal Task PublishWorkspaceDiagnosticProgressAsync(
        WorkspaceDiagnosticProgressParams parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        if (!_workspaceDiagnosticProgress.Writer.TryWrite(parameters))
        {
            throw new InvalidOperationException(
                "The workspace diagnostic partial result could not be observed.");
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Reads the next workspace diagnostic partial result from the real LSP connection.
    /// </summary>
    /// <param name="cancellationToken">The test cancellation token.</param>
    /// <returns>The next partial result notification.</returns>
    internal ValueTask<WorkspaceDiagnosticProgressParams> ReadWorkspaceDiagnosticProgressAsync(
        CancellationToken cancellationToken) =>
        _workspaceDiagnosticProgress.Reader.ReadAsync(cancellationToken);

    private static JsonElement? Parse(string? json)
    {
        if (json is null)
        {
            return null;
        }

        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
