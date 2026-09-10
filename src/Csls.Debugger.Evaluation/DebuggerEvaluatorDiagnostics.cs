using System.Text;

namespace Csls.Debugger.Evaluation;

/// <summary>
/// Drains evaluator diagnostics while retaining only a bounded prefix.
/// </summary>
internal static class DebuggerEvaluatorDiagnostics
{
    private const int MaximumCharacters = 32 * 1024;

    /// <summary>
    /// Drains one evaluator standard-error stream through end of file.
    /// </summary>
    /// <param name="reader">The diagnostics-only stream reader.</param>
    /// <returns>The bounded retained diagnostic prefix.</returns>
    internal static async ValueTask<string> DrainAsync(StreamReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var retained = new StringBuilder(MaximumCharacters);
        char[] buffer = new char[1024];
        while (true)
        {
            int read = await reader.ReadAsync(buffer, CancellationToken.None)
                .ConfigureAwait(false);
            if (read == 0)
            {
                return retained.ToString();
            }

            int remaining = MaximumCharacters - retained.Length;
            if (remaining > 0)
            {
                retained.Append(buffer, 0, Math.Min(read, remaining));
            }
        }
    }
}
