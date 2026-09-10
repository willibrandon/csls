using Csls.Debugger.Contracts;
using Csls.Debugger.Control;
using System.Buffers.Binary;
using System.Net.Sockets;
using System.Runtime.CompilerServices;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies debugger connection cancellation and transport ownership through real sockets.
/// </summary>
[TestClass]
public sealed class DebuggerRpcClientTests
{
    /// <summary>
    /// Gets the framework-managed cancellation token.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Cancels an unanswered protocol handshake after the peer receives the real request.
    /// </summary>
    /// <param name="transport">The socket-owned, stream-owned, or borrowed-stream connection.</param>
    [TestMethod]
    [DataRow("socket")]
    [DataRow("stream-owned")]
    [DataRow("stream-borrowed")]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task CancelingUnansweredHandshakeReleasesOwnedTransport(string transport)
    {
        DirectoryInfo directory = Directory.CreateTempSubdirectory("csls-rpc-cancel-");
        try
        {
            string path = Path.Join(directory.FullName, "control.sock");
            using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            var endpoint = new UnixDomainSocketEndPoint(path);
            listener.Bind(endpoint);
            listener.Listen(1);
            using Socket? outbound = transport == "socket" ? null
                : new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            if (outbound is not null)
            {
                await outbound.ConnectAsync(endpoint, TestContext.CancellationToken).ConfigureAwait(false);
            }
            using NetworkStream? stream = outbound is null ? null : new NetworkStream(outbound, ownsSocket: true);
            bool borrowed = transport == "stream-borrowed";
            DebuggerRpcClient client = stream is null ? new DebuggerRpcClient(path)
                : new DebuggerRpcClient(stream, stream, leaveOpen: borrowed);
            await using ConfiguredAsyncDisposable cleanup = client.ConfigureAwait(false);
            using var cancellation = new CancellationTokenSource();
            Task connection = client.ConnectAsync(cancellation.Token);
            using Socket accepted = await listener.AcceptAsync(TestContext.CancellationToken).ConfigureAwait(false);
            using var peer = new NetworkStream(accepted, ownsSocket: false);
            byte[] header = new byte[sizeof(int)];
            await peer.ReadExactlyAsync(header, TestContext.CancellationToken).ConfigureAwait(false);
            int length = BinaryPrimitives.ReadInt32BigEndian(header);
            Assert.IsGreaterThan(0, length);
            Assert.IsLessThanOrEqualTo(4096, length);
            byte[] request = new byte[4096];
            await peer.ReadExactlyAsync(request.AsMemory(0, length), TestContext.CancellationToken).ConfigureAwait(false);
            Assert.IsFalse(connection.IsCompleted);

            await cancellation.CancelAsync().ConfigureAwait(false);
            OperationCanceledException canceled = await Assert.ThrowsAsync<OperationCanceledException>(
                () => connection.WaitAsync(TimeSpan.FromSeconds(5), TestContext.CancellationToken)).ConfigureAwait(false);
            Assert.AreEqual(cancellation.Token, canceled.CancellationToken);
            Assert.IsFalse(TestContext.CancellationToken.IsCancellationRequested);
            await Assert.ThrowsExactlyAsync<ObjectDisposedException>(
                () => client.ConnectAsync(TestContext.CancellationToken)).ConfigureAwait(false);
            if (borrowed)
            {
                NetworkStream borrowedStream = Assert.IsInstanceOfType<NetworkStream>(stream);
                Assert.IsTrue(borrowedStream.CanRead);
                Assert.IsTrue(borrowedStream.CanWrite);
                byte[] marker = [0x42];
                await peer.WriteAsync(marker, TestContext.CancellationToken).ConfigureAwait(false);
                byte[] received = new byte[1];
                await borrowedStream.ReadExactlyAsync(received, TestContext.CancellationToken).ConfigureAwait(false);
                Assert.AreSequenceEqual(marker, received);
                await borrowedStream.WriteAsync(received, TestContext.CancellationToken).ConfigureAwait(false);
            }
            else
            {
                if (stream is not null)
                {
                    Assert.IsFalse(stream.CanRead);
                    Assert.IsFalse(stream.CanWrite);
                }
                byte[] remaining = new byte[4096];
                int received = 0;
                int count;
                while ((count = await peer.ReadAsync(remaining, TestContext.CancellationToken).ConfigureAwait(false)) != 0)
                {
                    received += count;
                    Assert.IsLessThanOrEqualTo(remaining.Length, received);
                }
            }
        }
        finally
        {
            Directory.Delete(directory.FullName, recursive: true);
        }
    }

    /// <summary>
    /// Keeps a completed connection usable when its former connection token is canceled.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task CancelingCompletedHandshakePreservesConnectedSession()
    {
        DirectoryInfo directory = Directory.CreateTempSubdirectory("csls-rpc-connected-");
        try
        {
            var service = new DebuggerControlService();
            await using ConfiguredAsyncDisposable serviceCleanup = service.ConfigureAwait(false);
            var server = new DebuggerRpcServer(Path.Join(directory.FullName, "control.sock"), service);
            await using ConfiguredAsyncDisposable serverCleanup = server.ConfigureAwait(false);
            server.Start();
            var client = new DebuggerRpcClient(Path.Join(directory.FullName, "control.sock"));
            await using ConfiguredAsyncDisposable clientCleanup = client.ConfigureAwait(false);
            using var cancellation = new CancellationTokenSource();
            await client.ConnectAsync(cancellation.Token).ConfigureAwait(false);
            await cancellation.CancelAsync().ConfigureAwait(false);
            DebugSessionSnapshot session = await client.GetSessionAsync(TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(DebugSessionState.Created, session.State);
            Assert.IsNull(session.ProcessId);
            Assert.AreEqual(0L, session.StopGeneration);
        }
        finally
        {
            Directory.Delete(directory.FullName, recursive: true);
        }
    }
}
