using Hex1b;
using Hex1b.Automation;
using Hex1b.Input;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace Csls.EndToEndPerformance;

/// <summary>
/// Measures the published Hex1b dashboard through a real pseudoterminal.
/// </summary>
internal static class DashboardMeasurementClient
{
    private const int Width = 140;
    private const int Height = 35;

    /// <summary>
    /// Opens, verifies, and closes the dashboard for one live session.
    /// </summary>
    /// <param name="serverPath">The published csls executable path.</param>
    /// <param name="languageServerProcessId">The attached language-server process.</param>
    /// <param name="workingDirectory">The measured workspace directory.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>A task that completes after the dashboard exits successfully.</returns>
    internal static async Task MeasureAsync(
        string serverPath,
        int languageServerProcessId,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
            ["DOTNET_NOLOGO"] = "1"
        };
        var workload = new DashboardPtyWorkload(
            serverPath,
            [
                "dashboard",
                "--session",
                languageServerProcessId.ToString(CultureInfo.InvariantCulture)
            ],
            workingDirectory,
            Width,
            Height,
            environment);
        await using ConfiguredAsyncDisposable workloadCleanup = workload.ConfigureAwait(false);
        using Hex1bTerminal terminal = Hex1bTerminal.CreateBuilder()
            .WithWorkload(workload)
            .WithHeadless()
            .WithDimensions(Width, Height)
            .Build();
        int exitCode = await workload.RunAsync(
            terminal,
            async () =>
            {
                var automator = new Hex1bTerminalAutomator(
                    terminal,
                    defaultTimeout: TimeSpan.FromSeconds(60));
                await automator.WaitUntilAsync(
                    screen => screen.InAlternateScreen ||
                        screen.ContainsText("Unhandled exception"),
                    description: "dashboard startup").ConfigureAwait(false);
                using (Hex1bTerminalSnapshot snapshot = automator.CreateSnapshot())
                {
                    if (!snapshot.InAlternateScreen)
                    {
                        throw new InvalidDataException(snapshot.GetScreenText());
                    }
                }

                await automator.WaitUntilTextAsync("csls dashboard").ConfigureAwait(false);
                await automator.WaitUntilTextAsync(
                    languageServerProcessId.ToString(CultureInfo.InvariantCulture))
                    .ConfigureAwait(false);
                await automator.Ctrl().KeyAsync(
                    Hex1bKey.C,
                    cancellationToken).ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);
        if (exitCode != 0)
        {
            throw new InvalidDataException(
                $"The dashboard exited with code {exitCode.ToString(CultureInfo.InvariantCulture)}.");
        }
    }
}
