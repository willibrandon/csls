using Csls.DebugAdapter.Protocol;

namespace Csls.DebugAdapter;

/// <summary>
/// Coordinates adapter-originated DAP requests with replies on the main input stream.
/// </summary>
internal sealed partial class DapSession
{
    private static readonly TimeSpan s_terminalResponseTimeout = TimeSpan.FromSeconds(30);

    private void CompleteReverseResponse(Response response)
    {
        TaskCompletionSource<Response>? completion;
        lock (_reverseRequestGate)
        {
            completion = response.RequestSeq == _pendingReverseSequence
                ? _pendingReverseResponse
                : null;
        }

        if (completion is null)
        {
            // A client may answer an already canceled reverse request after its target has retired.
            return;
        }

        if (!string.Equals(response.Command, "runInTerminal", StringComparison.Ordinal))
        {
            completion.TrySetException(new InvalidDataException(
                "The terminal client answered a different reverse request."));
            return;
        }

        completion.TrySetResult(response);
    }

    private async Task<Response> RunInTerminalAsync(
        string kind,
        string workingDirectory,
        string title,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string> environment,
        CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<Response>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await _writer.WriteRequestAsync(
                "runInTerminal",
                writer =>
                {
                    writer.WriteStartObject();
                    writer.WriteString("kind", kind);
                    writer.WriteString("title", title);
                    writer.WriteString("cwd", workingDirectory);
                    writer.WritePropertyName("args");
                    writer.WriteStartArray();
                    foreach (string argument in arguments)
                    {
                        writer.WriteStringValue(argument);
                    }

                    writer.WriteEndArray();
                    writer.WritePropertyName("env");
                    writer.WriteStartObject();
                    foreach ((string name, string value) in environment)
                    {
                        writer.WriteString(name, value);
                    }

                    writer.WriteEndObject();
                    writer.WriteEndObject();
                },
                sequence =>
                {
                    lock (_reverseRequestGate)
                    {
                        if (_pendingReverseResponse is not null)
                        {
                            throw new InvalidOperationException(
                                "Another terminal request is already pending.");
                        }

                        _pendingReverseSequence = sequence;
                        _pendingReverseResponse = completion;
                    }
                },
                cancellationToken).ConfigureAwait(false);

            Response response = await completion.Task.WaitAsync(
                s_terminalResponseTimeout, cancellationToken).ConfigureAwait(false);
            if (!response.Success)
            {
                throw new InvalidOperationException(
                    response.Message ?? "The terminal client refused to start the debugger target.");
            }

            return response;
        }
        finally
        {
            lock (_reverseRequestGate)
            {
                if (ReferenceEquals(_pendingReverseResponse, completion))
                {
                    _pendingReverseResponse = null;
                    _pendingReverseSequence = 0;
                }
            }
        }
    }
}
