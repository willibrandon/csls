using Csls.Core;
using Csls.Protocol;
using Microsoft.Extensions.Logging;

namespace Csls.Server;

public sealed partial class LanguageServer
{
    private static readonly TimeSpan s_pushDiagnosticChangeDelay = TimeSpan.FromMilliseconds(200);
    private readonly Lock _pushDiagnosticGate = new();
    private readonly Dictionary<DocumentUri, long> _pushDiagnosticRequests = [];

    private async Task PublishDiagnosticsAsync(
        DocumentUri uri,
        long requestId,
        bool delay,
        CancellationToken cancellationToken)
    {
        if (_supportsPullDiagnostics)
        {
            return;
        }

        try
        {
            if (delay)
            {
                await Task.Delay(s_pushDiagnosticChangeDelay, cancellationToken)
                    .ConfigureAwait(false);
            }

            lock (_pushDiagnosticGate)
            {
                if (!_pushDiagnosticRequests.TryGetValue(uri, out long currentRequestId) ||
                    currentRequestId != requestId)
                {
                    return;
                }
            }

            await _scheduler.ScheduleAsync(
                "textDocument/publishDiagnostics",
                RequestMode.ReadOnly,
                () => _workspaceManager.Generation,
                async context =>
                {
                    lock (_pushDiagnosticGate)
                    {
                        if (!_pushDiagnosticRequests.TryGetValue(
                            uri,
                            out long currentRequestId) ||
                            currentRequestId != requestId)
                        {
                            return false;
                        }
                    }

                    int? version = _workspaceManager.GetOpenDocumentVersion(uri);
                    IReadOnlyList<Diagnostic> diagnostics = [];
                    if (version is not null)
                    {
                        DocumentDiagnosticReport report = await _workspaceManager
                            .GetDiagnosticsAsync(
                                new DocumentDiagnosticParams
                                {
                                    TextDocument = new TextDocumentIdentifier { Uri = uri },
                                    Identifier = "csls"
                                },
                                context.CancellationToken)
                            .ConfigureAwait(false);
                        diagnostics = report.Items ?? [];
                    }

                    if (_workspaceManager.Generation != context.WorkspaceGeneration)
                    {
                        throw new InvalidOperationException(
                            "The workspace changed while push diagnostics were being computed.");
                    }

                    await _client.PublishDiagnosticsAsync(
                        new PublishDiagnosticsParams
                        {
                            Uri = uri,
                            Version = version,
                            Diagnostics = diagnostics
                        }).ConfigureAwait(false);
                    return true;
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (ObjectDisposedException) when (Volatile.Read(ref _disposeState) != 0)
        {
            LogPushDiagnosticSkippedDuringShutdown(uri);
        }
        finally
        {
            lock (_pushDiagnosticGate)
            {
                if (_pushDiagnosticRequests.TryGetValue(uri, out long currentRequestId) &&
                    currentRequestId == requestId)
                {
                    _pushDiagnosticRequests.Remove(uri);
                }
            }
        }
    }

    private void RegisterPushDiagnosticRequest(DocumentUri uri, long requestId)
    {
        if (_supportsPullDiagnostics)
        {
            return;
        }

        lock (_pushDiagnosticGate)
        {
            _pushDiagnosticRequests[uri] = requestId;
        }
    }

    private void ClearPushDiagnosticRequests()
    {
        lock (_pushDiagnosticGate)
        {
            _pushDiagnosticRequests.Clear();
        }
    }

    [LoggerMessage(
        EventId = 4,
        Level = LogLevel.Debug,
        Message = "Skipped push diagnostics for {Uri} during shutdown")]
    private partial void LogPushDiagnosticSkippedDuringShutdown(DocumentUri uri);
}
