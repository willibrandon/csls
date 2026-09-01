using Csls.Control;
using Csls.Control.Contracts;
using Csls.Protocol;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Csls.Tests;

/// <summary>
/// Verifies Windows-targeted workspaces from non-Windows development hosts.
/// </summary>
[TestClass]
public sealed class WindowsTargetingLanguageServerTests
{
    /// <summary>
    /// Gets the active MSTest context and its framework-managed cancellation token.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Restores and loads a Windows desktop project through the real cross-platform server.
    /// </summary>
    [TestMethod]
    [OSCondition(ConditionMode.Exclude, OperatingSystems.Windows)]
    public async Task WindowsDesktopProjectRestoresAndLoadsOnUnix()
    {
        string fixturePath = Path.Join(
            Path.GetTempPath(),
            $"csls-windows-targeting-{Guid.NewGuid():N}");
        Directory.CreateDirectory(fixturePath);
        try
        {
            string documentPath = Path.Join(fixturePath, "WindowsWidget.cs");
            await File.WriteAllTextAsync(
                Path.Join(fixturePath, "WindowsTargetingFixture.csproj"),
                ProjectText,
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                documentPath,
                DocumentText,
                TestContext.CancellationToken).ConfigureAwait(false);

            string repositoryRoot = EditorToolResolver.FindRepositoryRoot();
            string workerPath = Path.Join(
                EditorToolResolver.ResolveArtifactsRoot(repositoryRoot),
                "bin",
                "Csls.Worker",
                "debug",
                "csls-worker.dll");
            Assert.IsTrue(File.Exists(workerPath), $"Worker not found at {workerPath}.");
            LspProcessSession lsp = await LspProcessSession.StartAsync(
                "csls-windows-targeting-worker",
                EditorToolResolver.ResolveDotNetHost(),
                [workerPath],
                fixturePath).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable lspCleanup = lsp.ConfigureAwait(false);
            await lsp.InitializeAsync(
                fixturePath,
                TestContext.CancellationToken).ConfigureAwait(false);
            await lsp.CompleteInitializationAsync().ConfigureAwait(false);

            ControlSessionInfo session = await ControlSessionWaiter.WaitForRunningAsync(
                fixturePath,
                TimeSpan.FromSeconds(60),
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(lsp.ProcessId, session.ProcessId);
            var controlClient = new ControlRpcClient(session.SocketPath);
            await using ConfiguredAsyncDisposable controlCleanup =
                controlClient.ConfigureAwait(false);
            ControlWorkspaceOperationResult restore = await controlClient
                .RestoreWorkspaceAsync(TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual("restore", restore.Operation);
            Assert.AreEqual(1, restore.RestoredEntryPointCount);
            Assert.IsGreaterThan(restore.PreviousGeneration, restore.CurrentGeneration);

            await lsp.OpenDocumentAsync(documentPath, DocumentText).ConfigureAwait(false);
            JsonElement hoverElement = await lsp.RequestHoverAsync(
                documentPath,
                new Position(6, 37),
                TestContext.CancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException(
                    "The Windows desktop reference returned no hover.");
            Hover hover = hoverElement.Deserialize(LspJsonSerializerContext.Default.Hover)
                ?? throw new InvalidDataException(
                    "The Windows desktop reference returned invalid hover.");
            Assert.Contains("string Form.Text { get; set; }", hover.Contents.Value);

            DocumentDiagnosticReport diagnostics = await lsp.RequestDiagnosticsAsync(
                documentPath,
                previousResultId: null,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual("full", diagnostics.Kind);
            Assert.IsNotNull(diagnostics.Items);
            Assert.IsEmpty(diagnostics.Items);

            string shutdownDiagnostics = await lsp.ShutdownAsync(
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.DoesNotContain(
                "Unhandled exception",
                shutdownDiagnostics,
                StringComparison.Ordinal);
        }
        finally
        {
            await DirectoryReleaseWaiter.DeleteAsync(fixturePath, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
    }

    private const string ProjectText = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net10.0-windows</TargetFramework>
            <UseWindowsForms>true</UseWindowsForms>
          </PropertyGroup>
        </Project>
        """;

    private const string DocumentText = """
        using System.Windows.Forms;

        namespace WindowsTargetingFixture;

        public sealed class WindowsWidget : Form
        {
            public string ReadTitle() => Text;
        }
        """;
}
