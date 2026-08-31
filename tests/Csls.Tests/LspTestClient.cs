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
    private readonly Channel<RegistrationParams> _capabilityRegistrations =
        Channel.CreateUnbounded<RegistrationParams>(
            new UnboundedChannelOptions
            {
                AllowSynchronousContinuations = false,
                SingleReader = true,
                SingleWriter = false
            });
    private readonly Channel<bool> _diagnosticRefreshRequests = Channel.CreateUnbounded<bool>(
        new UnboundedChannelOptions
        {
            AllowSynchronousContinuations = false,
            SingleReader = true,
            SingleWriter = false
        });
    private readonly Channel<bool> _inlayHintRefreshRequests = Channel.CreateUnbounded<bool>(
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
    private readonly Channel<WorkDoneProgressCreateParams> _workDoneProgressCreations =
        Channel.CreateUnbounded<WorkDoneProgressCreateParams>(
            new UnboundedChannelOptions
            {
                AllowSynchronousContinuations = false,
                SingleReader = true,
                SingleWriter = false
            });
    private readonly Channel<WorkDoneProgressParams> _workDoneProgress =
        Channel.CreateUnbounded<WorkDoneProgressParams>(
            new UnboundedChannelOptions
            {
                AllowSynchronousContinuations = false,
                SingleReader = true,
                SingleWriter = false
            });
    private readonly Channel<PublishDiagnosticsParams> _publishedDiagnostics =
        Channel.CreateUnbounded<PublishDiagnosticsParams>(
            new UnboundedChannelOptions
            {
                AllowSynchronousContinuations = false,
                SingleReader = true,
                SingleWriter = false
            });
    private JsonElement? _legacyConfiguration;
    private JsonElement? _dotNetConfiguration;
    private JsonElement? _preferredConfiguration;
    private TaskCompletionSource? _capabilityRegistrationRelease;
    private int _configurationRequestCount;

    /// <summary>
    /// Creates a client with independently controlled legacy and preferred sections.
    /// </summary>
    /// <param name="legacyConfiguration">The legacy csharp section JSON.</param>
    /// <param name="preferredConfiguration">The preferred csls section JSON.</param>
    /// <param name="dotNetConfiguration">The shared dotnet section JSON.</param>
    internal LspTestClient(
        string? legacyConfiguration,
        string? preferredConfiguration,
        string? dotNetConfiguration = null)
    {
        SetConfiguration(legacyConfiguration, preferredConfiguration, dotNetConfiguration);
    }

    /// <summary>
    /// Replaces the configuration returned by subsequent server pull requests.
    /// </summary>
    /// <param name="legacyConfiguration">The legacy csharp section JSON.</param>
    /// <param name="preferredConfiguration">The preferred csls section JSON.</param>
    /// <param name="dotNetConfiguration">The shared dotnet section JSON.</param>
    internal void SetConfiguration(
        string? legacyConfiguration,
        string? preferredConfiguration,
        string? dotNetConfiguration = null)
    {
        JsonElement? legacy = Parse(legacyConfiguration);
        JsonElement? dotNet = Parse(dotNetConfiguration);
        JsonElement? preferred = Parse(preferredConfiguration);
        lock (_gate)
        {
            _legacyConfiguration = legacy;
            _dotNetConfiguration = dotNet;
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
                    "dotnet" => _dotNetConfiguration,
                    "csls" => _preferredConfiguration,
                    _ => null
                })
            ];
            return Task.FromResult(values);
        }
    }

    /// <summary>
    /// Accepts dynamically registered capabilities over the real LSP connection.
    /// </summary>
    /// <param name="parameters">The ordered capability registrations.</param>
    /// <param name="cancellationToken">The request cancellation token.</param>
    /// <returns>A task that completes after the registrations are retained and released.</returns>
    internal async Task RegisterCapabilityAsync(
        RegistrationParams parameters,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        cancellationToken.ThrowIfCancellationRequested();
        if (!_capabilityRegistrations.Writer.TryWrite(parameters))
        {
            throw new InvalidOperationException(
                "The capability registration could not be observed.");
        }

        Task? releaseTask;
        lock (_gate)
        {
            releaseTask = _capabilityRegistrationRelease?.Task;
        }

        if (releaseTask is not null)
        {
            await releaseTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Holds the next capability registration response after its request is observable.
    /// </summary>
    internal void HoldCapabilityRegistration()
    {
        lock (_gate)
        {
            if (_capabilityRegistrationRelease is not null)
            {
                throw new InvalidOperationException(
                    "A capability registration response is already held.");
            }

            _capabilityRegistrationRelease = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    /// <summary>
    /// Releases a held capability registration response.
    /// </summary>
    internal void ReleaseCapabilityRegistration()
    {
        TaskCompletionSource? release;
        lock (_gate)
        {
            release = _capabilityRegistrationRelease;
            _capabilityRegistrationRelease = null;
        }

        release?.TrySetResult();
    }

    /// <summary>
    /// Accepts one server request to refresh pull diagnostics.
    /// </summary>
    /// <param name="cancellationToken">The request cancellation token.</param>
    /// <returns>A completed task after the refresh is retained.</returns>
    internal Task RefreshDiagnosticsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_diagnosticRefreshRequests.Writer.TryWrite(true))
        {
            throw new InvalidOperationException(
                "The diagnostic refresh request could not be observed.");
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Reads the next dynamic capability registration from the real LSP connection.
    /// </summary>
    /// <param name="cancellationToken">The test cancellation token.</param>
    /// <returns>The next capability registration request.</returns>
    internal ValueTask<RegistrationParams> ReadCapabilityRegistrationAsync(
        CancellationToken cancellationToken) =>
        _capabilityRegistrations.Reader.ReadAsync(cancellationToken);

    /// <summary>
    /// Waits for the next server request to refresh pull diagnostics.
    /// </summary>
    /// <param name="cancellationToken">The test cancellation token.</param>
    /// <returns>A task that completes after the refresh request arrives.</returns>
    internal async Task WaitForDiagnosticRefreshAsync(CancellationToken cancellationToken)
    {
        await _diagnosticRefreshRequests.Reader
            .ReadAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Accepts one server request to refresh inlay hints.
    /// </summary>
    /// <param name="cancellationToken">The request cancellation token.</param>
    /// <returns>A completed task after the refresh is retained.</returns>
    internal Task RefreshInlayHintsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_inlayHintRefreshRequests.Writer.TryWrite(true))
        {
            throw new InvalidOperationException(
                "The inlay-hint refresh request could not be observed.");
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Waits for the next server request to refresh inlay hints.
    /// </summary>
    /// <param name="cancellationToken">The test cancellation token.</param>
    /// <returns>A task that completes after the refresh request arrives.</returns>
    internal async Task WaitForInlayHintRefreshAsync(CancellationToken cancellationToken)
    {
        await _inlayHintRefreshRequests.Reader
            .ReadAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Accepts one server-generated work-done progress token over the real LSP connection.
    /// </summary>
    /// <param name="parameters">The unique progress token requested by the server.</param>
    /// <param name="cancellationToken">The request cancellation token.</param>
    /// <returns>A completed task after the request is retained.</returns>
    internal Task CreateWorkDoneProgressAsync(
        WorkDoneProgressCreateParams parameters,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        cancellationToken.ThrowIfCancellationRequested();
        if (!_workDoneProgressCreations.Writer.TryWrite(parameters))
        {
            throw new InvalidOperationException(
                "The work-done progress creation could not be observed.");
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Records one typed diagnostic or work-done progress value from the real LSP connection.
    /// </summary>
    /// <param name="parameters">The raw progress parameters dispatched by value shape.</param>
    /// <returns>A completed task after the notification is retained.</returns>
    internal Task PublishProgressAsync(JsonElement parameters)
    {
        if (!parameters.TryGetProperty("value", out JsonElement value))
        {
            throw new InvalidDataException("The progress notification has no value.");
        }

        if (value.TryGetProperty("kind", out _))
        {
            WorkDoneProgressParams workDone = parameters.Deserialize(
                LspJsonSerializerContext.Default.WorkDoneProgressParams)
                ?? throw new InvalidDataException("The work-done progress value is invalid.");
            if (!_workDoneProgress.Writer.TryWrite(workDone))
            {
                throw new InvalidOperationException(
                    "The work-done progress value could not be observed.");
            }

            return Task.CompletedTask;
        }

        WorkspaceDiagnosticProgressParams diagnostics = parameters.Deserialize(
            LspJsonSerializerContext.Default.WorkspaceDiagnosticProgressParams)
            ?? throw new InvalidDataException("The diagnostic progress value is invalid.");
        if (!_workspaceDiagnosticProgress.Writer.TryWrite(diagnostics))
        {
            throw new InvalidOperationException(
                "The workspace diagnostic partial result could not be observed.");
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Reads the next server-generated work-done progress token request.
    /// </summary>
    /// <param name="cancellationToken">The test cancellation token.</param>
    /// <returns>The next requested progress token.</returns>
    internal ValueTask<WorkDoneProgressCreateParams> ReadWorkDoneProgressCreationAsync(
        CancellationToken cancellationToken) =>
        _workDoneProgressCreations.Reader.ReadAsync(cancellationToken);

    /// <summary>
    /// Reads the next work-done begin, report, or end value from the real LSP connection.
    /// </summary>
    /// <param name="cancellationToken">The test cancellation token.</param>
    /// <returns>The next typed work-done progress value.</returns>
    internal ValueTask<WorkDoneProgressParams> ReadWorkDoneProgressAsync(
        CancellationToken cancellationToken) =>
        _workDoneProgress.Reader.ReadAsync(cancellationToken);

    /// <summary>
    /// Reads the next workspace diagnostic partial result from the real LSP connection.
    /// </summary>
    /// <param name="cancellationToken">The test cancellation token.</param>
    /// <returns>The next partial result notification.</returns>
    internal ValueTask<WorkspaceDiagnosticProgressParams> ReadWorkspaceDiagnosticProgressAsync(
        CancellationToken cancellationToken) =>
        _workspaceDiagnosticProgress.Reader.ReadAsync(cancellationToken);

    /// <summary>
    /// Records one complete document diagnostic state received over the real LSP connection.
    /// </summary>
    /// <param name="parameters">The published document version and diagnostics.</param>
    /// <returns>A completed task after the notification is retained.</returns>
    internal Task PublishDiagnosticsAsync(PublishDiagnosticsParams parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        if (!_publishedDiagnostics.Writer.TryWrite(parameters))
        {
            throw new InvalidOperationException("The published diagnostics could not be observed.");
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Reads the next complete document diagnostic state from the real LSP connection.
    /// </summary>
    /// <param name="cancellationToken">The test cancellation token.</param>
    /// <returns>The next published document diagnostic notification.</returns>
    internal ValueTask<PublishDiagnosticsParams> ReadPublishedDiagnosticsAsync(
        CancellationToken cancellationToken) =>
        _publishedDiagnostics.Reader.ReadAsync(cancellationToken);

    /// <summary>
    /// Attempts to read a published diagnostic state without waiting.
    /// </summary>
    /// <param name="parameters">The published state when one is already available.</param>
    /// <returns>True when a diagnostic notification was available.</returns>
    internal bool TryReadPublishedDiagnostics(
        out PublishDiagnosticsParams? parameters) =>
        _publishedDiagnostics.Reader.TryRead(out parameters);

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
