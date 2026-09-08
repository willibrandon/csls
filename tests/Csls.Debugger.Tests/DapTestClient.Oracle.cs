using System.Diagnostics;

namespace Csls.Debugger.Tests;

/// <summary>
/// Connects the real DAP transport to an explicitly selected independent debugger.
/// </summary>
internal sealed partial class DapTestClient
{
    /// <summary>
    /// Starts an independent netcoredbg process using its standard DAP entry point.
    /// </summary>
    /// <param name="path">The explicitly selected oracle executable.</param>
    /// <param name="environment">Changes applied only to the oracle and its owned target.</param>
    /// <param name="cancellationToken">Cancels process creation.</param>
    /// <returns>The connected DAP client owning the oracle process.</returns>
    internal static async Task<DapTestClient> CreateOracleAsync(string path,
        IReadOnlyDictionary<string, string?> environment, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var client = new DapTestClient();
        try
        {
            var start = new ProcessStartInfo(Path.GetFullPath(path))
            {
                WorkingDirectory = FindRepositoryRoot(),
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            start.ArgumentList.Add("--interpreter=vscode");
            foreach ((string name, string? value) in environment)
            {
                if (value is null)
                {
                    start.Environment.Remove(name);
                }
                else
                {
                    start.Environment[name] = value;
                }
            }
            client._process = Process.Start(start)
                ?? throw new InvalidOperationException("The independent debugger did not start.");
            client._diagnostics = new ValueTask(client.CaptureDiagnosticsAsync(client._process.StandardError));
            return client;
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
