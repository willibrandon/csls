using System.Diagnostics;
using System.Text.Json;

namespace Csls.EndToEndPerformance;

/// <summary>
/// Measures a transient workspace through the published agent-ready CLI.
/// </summary>
internal static class CliMeasurementClient
{
    /// <summary>
    /// Runs a real symbol query with a transient language-server session.
    /// </summary>
    /// <param name="serverPath">The published csls executable path.</param>
    /// <param name="workspacePath">The measured workspace path.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>A task that completes after the transient process tree exits.</returns>
    internal static async Task MeasureTransientAsync(
        string serverPath,
        string workspacePath,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = serverPath,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            WorkingDirectory = workspacePath
        };
        startInfo.ArgumentList.Add("query");
        startInfo.ArgumentList.Add("symbols");
        startInfo.ArgumentList.Add("Csls");
        startInfo.ArgumentList.Add("--workspace");
        startInfo.ArgumentList.Add(workspacePath);
        startInfo.ArgumentList.Add("--limit");
        startInfo.ArgumentList.Add("1");
        startInfo.ArgumentList.Add("--json");
        startInfo.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        startInfo.Environment["DOTNET_NOLOGO"] = "1";
        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The transient CLI process did not start.");
        Task<string> outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        string output = await outputTask.ConfigureAwait(false);
        string error = await errorTask.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new InvalidDataException(
                $"The transient CLI exited with code {process.ExitCode}: {error.Trim()}");
        }

        using var document = JsonDocument.Parse(output);
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("schemaVersion", out JsonElement schemaVersion) ||
            schemaVersion.GetInt32() <= 0)
        {
            throw new InvalidDataException("The transient CLI returned an invalid JSON envelope.");
        }
    }
}
