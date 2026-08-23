using Csls.Control;
using Csls.Control.Contracts;
using Csls.Protocol;
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
            EditorToolResolver.ResolveArtifactsRoot(repositoryRoot),
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

    /// <summary>
    /// Shuts down cleanly while a real idle control connection is blocked on input.
    /// </summary>
    [TestMethod]
    public async Task ShutdownClosesIdleControlConnectionCleanly()
    {
        string repositoryRoot = EditorToolResolver.FindRepositoryRoot();
        string workerPath = Path.Join(
            EditorToolResolver.ResolveArtifactsRoot(repositoryRoot),
            "bin",
            "Csls.Worker",
            "debug",
            "csls-worker.dll");
        Assert.IsTrue(File.Exists(workerPath), $"Worker not found at {workerPath}.");

        string fixturePath = Path.Join(
            Path.GetTempPath(),
            $"csls-control-shutdown-{Guid.NewGuid():N}");
        Directory.CreateDirectory(fixturePath);
        try
        {
            await File.WriteAllTextAsync(
                Path.Join(fixturePath, "Fixture.csproj"),
                ProjectText,
                TestContext.CancellationToken).ConfigureAwait(false);
            var lsp = LspProcessSession.Start(
                "csls-control-shutdown-worker",
                EditorToolResolver.ResolveDotNetHost(),
                [workerPath],
                fixturePath);
            await using ConfiguredAsyncDisposable lspCleanup = lsp.ConfigureAwait(false);
            await lsp.InitializeAsync(
                fixturePath,
                TestContext.CancellationToken).ConfigureAwait(false);

            var idleClient = new ControlRpcClient(ControlEndpoint.GetSocketPath(lsp.ProcessId));
            await using ConfiguredAsyncDisposable idleClientCleanup =
                idleClient.ConfigureAwait(false);
            ControlSessionInfo session = await idleClient.GetSessionAsync(
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(lsp.ProcessId, session.ProcessId);

            string diagnostics = await lsp.ShutdownAsync(
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.DoesNotContain("Unhandled exception", diagnostics, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(fixturePath, recursive: true);
        }
    }

    /// <summary>
    /// Cancels a live Roslyn analyzer request and returns its exact lifecycle trace.
    /// </summary>
    [TestMethod]
    public async Task ControlCancelsLiveAnalyzerRequestAndReturnsTrace()
    {
        string repositoryRoot = EditorToolResolver.FindRepositoryRoot();
        string workerPath = Path.Join(
            EditorToolResolver.ResolveArtifactsRoot(repositoryRoot),
            "bin",
            "Csls.Worker",
            "debug",
            "csls-worker.dll");
        Assert.IsTrue(File.Exists(workerPath), $"Worker not found at {workerPath}.");
        CancellationProbeFixture fixture = await CancellationProbeFixture.CreateAsync(
            repositoryRoot,
            TestContext.CancellationToken).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable fixtureCleanup = fixture.ConfigureAwait(false);
        var lsp = LspProcessSession.Start(
            "csls-cancellation-worker",
            EditorToolResolver.ResolveDotNetHost(),
            [workerPath],
            fixture.RootPath);
        await using ConfiguredAsyncDisposable lspCleanup = lsp.ConfigureAwait(false);
        await lsp.InitializeAsync(
            fixture.RootPath,
            TestContext.CancellationToken).ConfigureAwait(false);
        await lsp.OpenDocumentAsync(
            fixture.DocumentPath,
            CancellationProbeFixture.DocumentText).ConfigureAwait(false);

        var controlClient = new ControlRpcClient(ControlEndpoint.GetSocketPath(lsp.ProcessId));
        await using ConfiguredAsyncDisposable controlCleanup =
            controlClient.ConfigureAwait(false);
        ControlTraceInfo startedTrace = await controlClient.StartTraceAsync(
            TestContext.CancellationToken).ConfigureAwait(false);
        Assert.IsTrue(startedTrace.IsActive);
        Assert.IsNotNull(startedTrace.TraceId);
        ControlDashboardSnapshot loadedDashboard = await controlClient.GetDashboardSnapshotAsync(
            new ControlDashboardRequest { IncludeDiagnostics = false },
            TestContext.CancellationToken).ConfigureAwait(false);
        ControlProjectInfo loadedProject = loadedDashboard.Projects.Single();
        Assert.Contains(
            "Csls.CancellationProbeAnalyzer.dll",
            loadedProject.AnalyzerPaths.Select(Path.GetFileName));
        Assert.Contains(
            "Csls.CancellationProbeTransport.dll",
            loadedProject.AnalyzerPaths.Select(Path.GetFileName));

        Task<DocumentDiagnosticReport> diagnosticRequest = lsp.RequestDiagnosticsAsync(
            fixture.DocumentPath,
            previousResultId: null,
            TestContext.CancellationToken);
        await FileTextWaiter.WaitAsync(
            fixture.MarkerPath,
            "started",
            TimeSpan.FromSeconds(60),
            TestContext.CancellationToken).ConfigureAwait(false);
        ControlDashboardSnapshot dashboard = await controlClient.GetDashboardSnapshotAsync(
            new ControlDashboardRequest { IncludeDiagnostics = false },
            TestContext.CancellationToken).ConfigureAwait(false);
        ControlRequestInfo request = dashboard.Requests.ActiveRequests.Single(
            static item => item.Name == "textDocument/diagnostic");
        Assert.AreEqual("Running", request.Status);
        Assert.AreEqual("ReadOnly", request.Mode);
        Assert.IsNotNull(request.WorkspaceGeneration);
        Assert.IsNotNull(request.StartedAt);
        Assert.IsFalse(request.IsCancellationRequested);

        ControlCancelRequestResult cancellation = await controlClient.CancelRequestAsync(
            new ControlCancelRequest { CorrelationId = request.CorrelationId },
            TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(request.CorrelationId, cancellation.CorrelationId);
        Assert.IsTrue(cancellation.CancellationRequested);
        await FileTextWaiter.WaitAsync(
            fixture.MarkerPath,
            "canceled",
            TimeSpan.FromSeconds(60),
            TestContext.CancellationToken).ConfigureAwait(false);
        TaskCanceledException? canceledRequest = null;
        try
        {
            await diagnosticRequest.ConfigureAwait(false);
        }
        catch (TaskCanceledException exception)
        {
            canceledRequest = exception;
        }

        Assert.IsNotNull(canceledRequest);
        Assert.IsFalse(TestContext.CancellationToken.IsCancellationRequested);

        ControlCancelRequestResult retiredCancellation = await controlClient.CancelRequestAsync(
            new ControlCancelRequest { CorrelationId = request.CorrelationId },
            TestContext.CancellationToken).ConfigureAwait(false);
        Assert.IsFalse(retiredCancellation.CancellationRequested);
        ControlTraceInfo stoppedTrace = await controlClient.StopTraceAsync(
            TestContext.CancellationToken).ConfigureAwait(false);
        Assert.IsFalse(stoppedTrace.IsActive);
        Assert.AreEqual(startedTrace.TraceId, stoppedTrace.TraceId);
        ControlTraceEntry traceEntry = stoppedTrace.Entries.Single(
            item => item.CorrelationId == request.CorrelationId);
        Assert.AreEqual("textDocument/diagnostic", traceEntry.Name);
        Assert.AreEqual("Canceled", traceEntry.Status);
        Assert.AreEqual(request.WorkspaceGeneration, traceEntry.WorkspaceGeneration);
        Assert.IsTrue(traceEntry.IsCancellationRequested);
        Assert.IsNotNull(traceEntry.CompletedAt);
        Assert.IsGreaterThanOrEqualTo(0D, traceEntry.DurationMilliseconds);

        string diagnostics = await lsp.ShutdownAsync(
            TestContext.CancellationToken).ConfigureAwait(false);
        Assert.DoesNotContain("Unhandled exception", diagnostics, StringComparison.Ordinal);
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
