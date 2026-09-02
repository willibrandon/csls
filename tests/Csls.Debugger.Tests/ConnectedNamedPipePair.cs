using System.IO.Pipes;

namespace Csls.Debugger.Tests;

/// <summary>
/// Provides two connected endpoints backed by one operating-system named pipe.
/// </summary>
internal sealed class ConnectedNamedPipePair : IAsyncDisposable
{
    private ConnectedNamedPipePair(Stream server, Stream client)
    {
        Server = server;
        Client = client;
    }

    /// <summary>
    /// Creates a connected asynchronous byte-mode pipe with duplex endpoints.
    /// </summary>
    /// <param name="cancellationToken">Cancels connection establishment.</param>
    /// <returns>The connected operating-system pipe endpoints.</returns>
    internal static async Task<ConnectedNamedPipePair> CreateAsync(
        CancellationToken cancellationToken)
    {
        string pipeName = $"csls-debugger-tests-{Guid.NewGuid():N}";
        var server = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);
        var client = new NamedPipeClientStream(
            serverName: ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        try
        {
            Task connection = server.WaitForConnectionAsync(cancellationToken);
            await client
                .ConnectAsync(timeout: 5_000, cancellationToken)
                .ConfigureAwait(false);
            await connection.ConfigureAwait(false);
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            await server.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return new ConnectedNamedPipePair(server, client);
    }

    /// <summary>
    /// Gets the endpoint used by production debugger components.
    /// </summary>
    internal Stream Server { get; }

    /// <summary>
    /// Gets the endpoint used by the test client.
    /// </summary>
    internal Stream Client { get; }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await Client.DisposeAsync().ConfigureAwait(false);
        await Server.DisposeAsync().ConfigureAwait(false);
    }
}
