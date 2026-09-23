using System.Text;
using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Captures bounded read-only inspection evidence before a failed stop assertion tears down its target.
/// </summary>
internal static class DapStoppedThreadDiagnostics
{
    private const int MaximumResponseCharacters = 4096;

    /// <summary>
    /// Consumes diagnostic responses and intervening messages while preserving inspection failures as evidence.
    /// </summary>
    /// <param name="client">The exclusively owned real DAP connection.</param>
    /// <param name="threadId">The thread reported by the unexpected stopped event.</param>
    /// <param name="cancellationToken">Cancels capture within the owning test's remaining lifetime.</param>
    /// <returns>Bounded response text or the inspection failure that preceded cleanup.</returns>
    internal static async Task<string> CaptureAsync(
        DapTestClient client, int threadId, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        var evidence = new StringBuilder();
        try
        {
            foreach (string command in new[] { "exceptionInfo", "stackTrace" })
            {
                deadline.Token.ThrowIfCancellationRequested();
                int sequence = await client.SendRequestAsync(command, writer =>
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("threadId", threadId);
                    if (command == "stackTrace")
                    {
                        writer.WriteNumber("startFrame", 0);
                        writer.WriteNumber("levels", 32);
                    }

                    writer.WriteEndObject();
                }, deadline.Token).ConfigureAwait(false);
                while (true)
                {
                    using JsonDocument message = await client.ReadMessageAsync(deadline.Token).ConfigureAwait(false);
                    JsonElement root = message.RootElement;
                    if (root.TryGetProperty("request_seq", out JsonElement requestSequence) &&
                        requestSequence.GetInt32() == sequence)
                    {
                        AppendBounded(evidence, root.GetRawText());
                        break;
                    }
                }
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException or
            JsonException or InvalidOperationException or AssertFailedException)
        {
            AppendBounded(evidence, $"Stop inspection failed: {exception.GetType().Name}: {exception.Message}");
        }

        return evidence.ToString();
    }

    private static void AppendBounded(StringBuilder evidence, string text)
    {
        evidence.Append(text.AsSpan(0, Math.Min(text.Length, MaximumResponseCharacters)));
        if (text.Length > MaximumResponseCharacters)
        {
            evidence.Append(" [truncated]");
        }

        evidence.AppendLine();
    }
}
