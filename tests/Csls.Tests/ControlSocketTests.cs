using Csls.Control;
using Csls.Control.Contracts;
using System.Buffers.Binary;
using System.Net.Sockets;
using System.Runtime.CompilerServices;

namespace Csls.Tests;

/// <summary>
/// Verifies defensive control behavior through a real worker Unix-domain socket.
/// </summary>
[TestClass]
public sealed class ControlSocketTests
{
    private const int MaximumMessageBytes = 4 * 1024 * 1024;

    /// <summary>
    /// Gets the active MSTest context and its framework-managed cancellation token.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Rejects an oversized frame before its payload while preserving subsequent control requests.
    /// </summary>
    [TestMethod]
    public async Task OversizedLengthHeaderDisconnectsOnlyMalformedClient()
    {
        string repositoryRoot = EditorToolResolver.FindRepositoryRoot();
        string workerPath = Path.Join(
            repositoryRoot,
            "artifacts",
            "bin",
            "Csls.Worker",
            "debug",
            "csls-worker.dll");
        Assert.IsTrue(File.Exists(workerPath), $"Worker not found at {workerPath}.");

        string fixturePath = Path.Join(
            Path.GetTempPath(),
            $"csls-control-{Guid.NewGuid():N}");
        Directory.CreateDirectory(fixturePath);
        try
        {
            string documentPath = Path.Join(fixturePath, "Program.cs");
            await File.WriteAllTextAsync(
                Path.Join(fixturePath, "Fixture.csproj"),
                ProjectText,
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                documentPath,
                DocumentText,
                TestContext.CancellationToken).ConfigureAwait(false);
            var lsp = LspProcessSession.Start(
                "csls-control-worker",
                EditorToolResolver.ResolveDotNetHost(),
                [workerPath],
                fixturePath);
            await using ConfiguredAsyncDisposable lspCleanup = lsp.ConfigureAwait(false);
            await lsp.InitializeAsync(
                fixturePath,
                TestContext.CancellationToken).ConfigureAwait(false);
            await lsp.OpenDocumentAsync(documentPath, DocumentText).ConfigureAwait(false);

            string socketPath = ControlEndpoint.GetSocketPath(lsp.ProcessId);
            Assert.IsTrue(File.Exists(socketPath), $"Control socket not found at {socketPath}.");
            using var malformedClient = new Socket(
                AddressFamily.Unix,
                SocketType.Stream,
                ProtocolType.Unspecified);
            await malformedClient.ConnectAsync(
                new UnixDomainSocketEndPoint(socketPath),
                TestContext.CancellationToken).ConfigureAwait(false);
            byte[] header = new byte[sizeof(int)];
            BinaryPrimitives.WriteInt32BigEndian(header, MaximumMessageBytes + 1);
            await malformedClient.SendAsync(
                header,
                SocketFlags.None,
                TestContext.CancellationToken).ConfigureAwait(false);

            bool disconnected = await WaitForDisconnectionAsync(
                malformedClient,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.IsTrue(disconnected, "The oversized control client remained connected.");

            ControlSessionInfo session = await ControlSessionWaiter.WaitForRunningAsync(
                fixturePath,
                TimeSpan.FromSeconds(60),
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(lsp.ProcessId, session.ProcessId);
            Assert.AreEqual("Running", session.LifecycleState);

            string diagnostics = await lsp.ShutdownAsync(
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.DoesNotContain("Unhandled exception", diagnostics, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(fixturePath, recursive: true);
        }
    }

    private static async Task<bool> WaitForDisconnectionAsync(
        Socket socket,
        CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[1];
        try
        {
            int bytesRead = await socket.ReceiveAsync(
                buffer,
                SocketFlags.None,
                cancellationToken).ConfigureAwait(false);
            return bytesRead == 0;
        }
        catch (SocketException)
        {
            return true;
        }
    }

    private const string ProjectText = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
            <ImplicitUsings>enable</ImplicitUsings>
          </PropertyGroup>
        </Project>
        """;

    private const string DocumentText = """
        namespace Fixture;

        public static class Program
        {
            public static void Main()
            {
            }
        }
        """;
}
