using Hex1b;
using System.Runtime.CompilerServices;

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
    /// <param name="workspacePath">The optional workspace path used to select or validate a session.</param>
    /// <param name="cancellationToken">The dashboard cancellation token.</param>
    /// <returns>The successful dashboard process exit code.</returns>
    public static async Task<int> RunAsync(
        int processId,
        string? workspacePath,
        CancellationToken cancellationToken)
    {
        DashboardState state = await DashboardState
            .CreateAsync(processId, workspacePath, cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable stateCleanup = state.ConfigureAwait(false);
        Hex1bTerminal terminal = Hex1bTerminal.CreateBuilder()
            .WithHex1bApp(
                static _ => { },
                app =>
                {
                    state.AttachApp(app);
                    return context => DashboardView.Build(context, state);
                })
            .WithMouse()
            .Build();
        await using (terminal.ConfigureAwait(false))
        {
            await terminal.RunAsync(cancellationToken).ConfigureAwait(false);
        }

        return 0;
    }
}
