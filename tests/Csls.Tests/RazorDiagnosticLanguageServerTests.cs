using Csls.Control;
using Csls.Control.Contracts;
using Csls.Protocol;
using System.Runtime.CompilerServices;

namespace Csls.Tests;

/// <summary>
/// Verifies Razor syntax diagnostics through a real language-server worker process.
/// </summary>
[TestClass]
public sealed class RazorDiagnosticLanguageServerTests
{
    /// <summary>
    /// Gets the active MSTest context and its framework-managed cancellation token.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Tracks view and component diagnostics across client overlays and persisted text.
    /// </summary>
    [TestMethod]
    public async Task RazorDiagnosticsTrackCurrentDocumentSnapshots()
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
            $"csls-razor-diagnostics-{Guid.NewGuid():N}");
        Directory.CreateDirectory(fixturePath);
        try
        {
            string pagesPath = Path.Join(fixturePath, "Pages");
            string componentsPath = Path.Join(fixturePath, "Components");
            Directory.CreateDirectory(pagesPath);
            Directory.CreateDirectory(componentsPath);
            string viewPath = Path.Join(pagesPath, "Index.cshtml");
            string componentPath = Path.Join(componentsPath, "Panel.razor");
            string semanticPath = Path.Join(componentsPath, "Semantic.razor");
            await File.WriteAllTextAsync(
                Path.Join(fixturePath, "Fixture.csproj"),
                ProjectText,
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                Path.Join(fixturePath, "Known.cs"),
                KnownTypeText,
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                Path.Join(fixturePath, "_Imports.razor"),
                "@using Fixture",
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                viewPath,
                InvalidRazorText,
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                componentPath,
                InvalidRazorText,
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                semanticPath,
                InvalidSemanticRazorText,
                TestContext.CancellationToken).ConfigureAwait(false);

            var lsp = LspProcessSession.Start(
                "csls-razor-diagnostic-worker",
                EditorToolResolver.ResolveDotNetHost(),
                [workerPath],
                fixturePath);
            await using ConfiguredAsyncDisposable lspCleanup = lsp.ConfigureAwait(false);
            await lsp.InitializeAsync(
                fixturePath,
                TestContext.CancellationToken).ConfigureAwait(false);
            await lsp.OpenDocumentAsync(viewPath, InvalidRazorText, "razor").ConfigureAwait(false);
            await lsp.OpenDocumentAsync(componentPath, InvalidRazorText, "razor")
                .ConfigureAwait(false);
            await lsp.OpenDocumentAsync(semanticPath, InvalidSemanticRazorText, "razor")
                .ConfigureAwait(false);

            DocumentDiagnosticReport viewReport = await lsp.RequestDiagnosticsAsync(
                viewPath,
                previousResultId: null,
                TestContext.CancellationToken).ConfigureAwait(false);
            Diagnostic viewDiagnostic = GetRazorCommentDiagnostic(viewReport);
            Assert.AreEqual(DiagnosticSeverity.Error, viewDiagnostic.Severity);
            Assert.AreEqual(new Position(0, 0), viewDiagnostic.Range.Start);
            Assert.AreEqual(new Position(0, 2), viewDiagnostic.Range.End);

            DocumentDiagnosticReport componentReport = await lsp.RequestDiagnosticsAsync(
                componentPath,
                previousResultId: null,
                TestContext.CancellationToken).ConfigureAwait(false);
            GetRazorCommentDiagnostic(componentReport);

            DocumentDiagnosticReport semanticReport = await lsp.RequestDiagnosticsAsync(
                semanticPath,
                previousResultId: null,
                TestContext.CancellationToken).ConfigureAwait(false);
            Diagnostic semanticDiagnostic = GetMissingNameDiagnostic(semanticReport);
            Assert.AreEqual(DiagnosticSeverity.Error, semanticDiagnostic.Severity);
            Assert.AreEqual(new Position(1, 4), semanticDiagnostic.Range.Start);
            Assert.AreEqual(new Position(1, 15), semanticDiagnostic.Range.End);

            await lsp.ChangeDocumentAsync(
                viewPath,
                version: 2,
                [new TextDocumentContentChangeEvent { Text = ValidRazorText }])
                .ConfigureAwait(false);
            await lsp.ChangeDocumentAsync(
                semanticPath,
                version: 2,
                [new TextDocumentContentChangeEvent { Text = ValidSemanticRazorText }])
                .ConfigureAwait(false);
            DocumentDiagnosticReport fixedReport = await lsp.RequestDiagnosticsAsync(
                viewPath,
                viewReport.ResultId,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual("full", fixedReport.Kind);
            Assert.AreNotEqual(viewReport.ResultId, fixedReport.ResultId);
            Assert.DoesNotContain(
                "RZ1028",
                GetFullItems(fixedReport).Select(static diagnostic => diagnostic.Code));
            DocumentDiagnosticReport fixedSemanticReport = await lsp.RequestDiagnosticsAsync(
                semanticPath,
                semanticReport.ResultId,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.DoesNotContain(
                "CS0103",
                GetFullItems(fixedSemanticReport).Select(static diagnostic => diagnostic.Code));

            ControlSessionInfo session = await ControlSessionWaiter.WaitForRunningAsync(
                fixturePath,
                TimeSpan.FromSeconds(60),
                TestContext.CancellationToken).ConfigureAwait(false);
            var control = new ControlRpcClient(session.SocketPath);
            await using ConfiguredAsyncDisposable controlCleanup = control.ConfigureAwait(false);
            ControlWorkspaceOperationResult reload = await control.ReloadWorkspaceAsync(
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(reload.PreviousGeneration + 1, reload.CurrentGeneration);
            DocumentDiagnosticReport reloadedReport = await lsp.RequestDiagnosticsAsync(
                viewPath,
                fixedReport.ResultId,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreNotEqual(fixedReport.ResultId, reloadedReport.ResultId);
            Assert.DoesNotContain(
                "RZ1028",
                GetFullItems(reloadedReport).Select(static diagnostic => diagnostic.Code));
            DocumentDiagnosticReport reloadedSemanticReport = await lsp.RequestDiagnosticsAsync(
                semanticPath,
                fixedSemanticReport.ResultId,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.DoesNotContain(
                "CS0103",
                GetFullItems(reloadedSemanticReport).Select(static diagnostic => diagnostic.Code));

            await lsp.CloseDocumentAsync(viewPath).ConfigureAwait(false);
            DocumentDiagnosticReport persistedReport = await lsp.RequestDiagnosticsAsync(
                viewPath,
                reloadedReport.ResultId,
                TestContext.CancellationToken).ConfigureAwait(false);
            GetRazorCommentDiagnostic(persistedReport);
            await lsp.CloseDocumentAsync(semanticPath).ConfigureAwait(false);
            DocumentDiagnosticReport persistedSemanticReport = await lsp.RequestDiagnosticsAsync(
                semanticPath,
                reloadedSemanticReport.ResultId,
                TestContext.CancellationToken).ConfigureAwait(false);
            GetMissingNameDiagnostic(persistedSemanticReport);

            string diagnostics = await lsp.ShutdownAsync(
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.DoesNotContain("Unhandled exception", diagnostics, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(fixturePath, recursive: true);
        }
    }

    private static Diagnostic GetRazorCommentDiagnostic(DocumentDiagnosticReport report)
    {
        Diagnostic diagnostic = GetFullItems(report)
            .Single(static item => item.Code == "RZ1028");
        Assert.AreEqual("Razor", diagnostic.Source);
        Assert.Contains("terminated", diagnostic.Message, StringComparison.OrdinalIgnoreCase);
        return diagnostic;
    }

    private static Diagnostic GetMissingNameDiagnostic(DocumentDiagnosticReport report)
    {
        Diagnostic diagnostic = GetFullItems(report)
            .Single(static item => item.Code == "CS0103");
        Assert.AreEqual("C#", diagnostic.Source);
        Assert.Contains("does not exist", diagnostic.Message, StringComparison.OrdinalIgnoreCase);
        return diagnostic;
    }

    private static IReadOnlyList<Diagnostic> GetFullItems(DocumentDiagnosticReport report)
    {
        Assert.AreEqual("full", report.Kind);
        Assert.IsNotNull(report.ResultId);
        return report.Items
            ?? throw new InvalidDataException("A full Razor diagnostic report had no items.");
    }

    private const string ProjectText = """
        <Project Sdk="Microsoft.NET.Sdk.Web">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
            <Nullable>enable</Nullable>
          </PropertyGroup>
        </Project>
        """;

    private const string InvalidRazorText = "@* unterminated";
    private const string ValidRazorText = "@* valid *@\n<p>Hello</p>";
    private const string KnownTypeText = """
        namespace Fixture;

        /// <summary>
        /// Supplies a value imported by the Razor component fixture.
        /// </summary>
        public static class Known
        {
            public static string Value => "known";
        }
        """;
    private const string InvalidSemanticRazorText = """
        <p>@Known.Value</p>
        <p>@MissingName</p>
        """;
    private const string ValidSemanticRazorText = "<p>@Known.Value</p>";
}
