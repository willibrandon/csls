using Csls.Core;
using Csls.Protocol;

namespace Csls.Server;

public sealed partial class LanguageServer
{
    private const int PushDiagnosticChangeDelayMilliseconds = 200;
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
                await Task.Delay(PushDiagnosticChangeDelayMilliseconds, cancellationToken)
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
                RequestMode.ReadOnlyBackground,
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
                                _configuration.ReportInformationAsHint,
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
            LanguageServerLogger.LogPushDiagnosticSkippedDuringShutdown(_logger, uri);
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

}
