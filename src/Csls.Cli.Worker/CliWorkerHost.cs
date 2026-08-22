using System.Globalization;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using Csls.Control;
using Csls.Control.Contracts;
using Csls.Protocol;

namespace Csls.Cli.Worker;

/// <summary>
/// Executes normalized CLI operations through the versioned control protocol.
/// </summary>
internal static class CliWorkerHost
{
    /// <summary>
    /// Executes one normalized launcher request and returns its process exit code.
    /// </summary>
    /// <param name="arguments">The normalized internal request arguments.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>The process exit code.</returns>
    internal static async Task<int> RunAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        bool writeJson = arguments.Count > 1 && bool.TryParse(arguments[^1], out bool json) && json;
        try
        {
            return arguments.Count == 0
                ? Fail("invalid-request", "The launcher supplied no CLI operation.", writeJson)
                : arguments[0] switch
                {
                    "sessions-list" => await ListSessionsAsync(writeJson, cancellationToken)
                        .ConfigureAwait(false),
                    "sessions-show" => await ShowSessionAsync(arguments, writeJson, cancellationToken)
                        .ConfigureAwait(false),
                    "query-hover" => await QueryHoverAsync(arguments, writeJson, cancellationToken)
                        .ConfigureAwait(false),
                    _ => Fail(
                        "invalid-request",
                        $"The launcher supplied an unknown CLI operation: {arguments[0]}",
                        writeJson)
                };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return 130;
        }
        catch (Exception exception) when (
            exception is IOException or
                InvalidDataException or
                UnauthorizedAccessException or
                InvalidOperationException or
                SocketException or
                ArgumentException)
        {
            return Fail("operation-failed", exception.Message, writeJson);
        }
    }

    private static async Task<int> ListSessionsAsync(
        bool writeJson,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<ControlSessionInfo> sessions = await ControlSessionDiscovery
            .DiscoverAsync(cancellationToken)
            .ConfigureAwait(false);
        CliOutputWriter.WriteSessions(sessions, writeJson);
        return 0;
    }

    private static async Task<int> ShowSessionAsync(
        IReadOnlyList<string> arguments,
        bool writeJson,
        CancellationToken cancellationToken)
    {
        if (arguments.Count != 3 ||
            !int.TryParse(arguments[1], NumberStyles.None, CultureInfo.InvariantCulture, out int processId))
        {
            return Fail("invalid-request", "The launcher supplied an invalid session request.", writeJson);
        }

        ControlSessionInfo session = await ResolveSessionAsync(processId, cancellationToken)
            .ConfigureAwait(false);
        CliOutputWriter.WriteSession(session, writeJson);
        return 0;
    }

    private static async Task<int> QueryHoverAsync(
        IReadOnlyList<string> arguments,
        bool writeJson,
        CancellationToken cancellationToken)
    {
        if (arguments.Count != 6 ||
            !int.TryParse(arguments[1], NumberStyles.None, CultureInfo.InvariantCulture, out int processId) ||
            !int.TryParse(arguments[3], NumberStyles.None, CultureInfo.InvariantCulture, out int line) ||
            !int.TryParse(arguments[4], NumberStyles.None, CultureInfo.InvariantCulture, out int character))
        {
            return Fail("invalid-request", "The launcher supplied an invalid hover request.", writeJson);
        }

        ControlSessionInfo session = await ResolveSessionAsync(processId, cancellationToken)
            .ConfigureAwait(false);
        var client = new ControlRpcClient(session.SocketPath);
        await using ConfiguredAsyncDisposable clientCleanup = client.ConfigureAwait(false);
        ControlHoverResult hover = await client.GetHoverAsync(
            new ControlHoverRequest
            {
                DocumentPath = arguments[2],
                Position = new Position(line, character)
            },
            cancellationToken).ConfigureAwait(false);
        CliOutputWriter.WriteHover(hover, writeJson);
        return 0;
    }

    private static async Task<ControlSessionInfo> ResolveSessionAsync(
        int processId,
        CancellationToken cancellationToken)
    {
        if (processId > 0)
        {
            var client = new ControlRpcClient(ControlEndpoint.GetSocketPath(processId));
            await using ConfiguredAsyncDisposable clientCleanup = client.ConfigureAwait(false);
            return await client.GetSessionAsync(cancellationToken).ConfigureAwait(false);
        }

        IReadOnlyList<ControlSessionInfo> sessions = await ControlSessionDiscovery
            .DiscoverAsync(cancellationToken)
            .ConfigureAwait(false);
        return sessions.Count switch
        {
            0 => throw new InvalidOperationException(
                "No live csls session was found. Start an editor session or specify --session."),
            1 => sessions[0],
            _ => throw new InvalidOperationException(
                "Multiple live csls sessions were found. Specify one with --session <pid>.")
        };
    }

    private static int Fail(string code, string message, bool writeJson)
    {
        CliOutputWriter.WriteError(code, message, writeJson);
        return 1;
    }
}
