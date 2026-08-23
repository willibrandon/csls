using Hex1b;

namespace Csls.Dashboard;

/// <summary>
/// Runs the full-screen Hex1b dashboard against real versioned control services.
/// </summary>
public static class DashboardHost
{
    /// <summary>
    /// Discovers a live session and runs the interactive dashboard until cancellation.
    /// </summary>
    /// <param name="processId">The requested worker process, or zero to infer one.</param>
    /// <param name="cancellationToken">The dashboard cancellation token.</param>
    /// <returns>The successful dashboard process exit code.</returns>
    public static async Task<int> RunAsync(
        int processId,
        CancellationToken cancellationToken)
    {
        DashboardState state = await DashboardState
            .CreateAsync(processId, cancellationToken)
            .ConfigureAwait(false);
        Hex1bTerminal terminal = Hex1bTerminal.CreateBuilder()
            .WithHex1bApp(context => DashboardView.Build(context, state))
            .Build();
        await using (terminal.ConfigureAwait(false))
        {
            await terminal.RunAsync(cancellationToken).ConfigureAwait(false);
        }

        return 0;
    }
}
