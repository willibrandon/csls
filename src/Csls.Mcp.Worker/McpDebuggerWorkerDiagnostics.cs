using System.Text;

namespace Csls.Mcp.Worker;

/// <summary>
/// Captures a bounded prefix of debugger-worker diagnostics while draining stderr.
/// </summary>
internal static class McpDebuggerWorkerDiagnostics
{
    private const int MaximumCharacters = 64 * 1024;

    /// <summary>
    /// Drains the supplied reader and returns at most the configured diagnostic bound.
    /// </summary>
    /// <param name="reader">The debugger worker standard-error reader.</param>
    /// <returns>The retained diagnostic prefix.</returns>
    internal static async Task<string> ReadAsync(StreamReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var result = new StringBuilder(MaximumCharacters);
        char[] buffer = new char[4096];
        while (true)
        {
            int read = await reader.ReadAsync(buffer, CancellationToken.None)
                .ConfigureAwait(false);
            if (read == 0)
            {
                return result.ToString();
            }

            int remaining = MaximumCharacters - result.Length;
            if (remaining > 0)
            {
                result.Append(buffer, 0, Math.Min(read, remaining));
            }
        }
    }
}
