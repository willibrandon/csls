using Csls.Protocol;
using Microsoft.CodeAnalysis.Text;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using LspRange = Csls.Protocol.Range;

namespace Csls.Tests;

/// <summary>
/// Verifies pull diagnostics, analyzer execution, caching, and incremental synchronization.
/// </summary>
[TestClass]
public sealed class DiagnosticLanguageServerTests
{
    /// <summary>
    /// Gets the active MSTest context and its framework-managed cancellation token.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Matches Roslyn's editor policy for hidden style and fading diagnostics.
    /// </summary>
    [TestMethod]
    public async Task HiddenStyleDiagnosticsDoNotSurfaceAsVisibleHints()
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
            $"csls-hidden-diagnostics-{Guid.NewGuid():N}");
        Directory.CreateDirectory(fixturePath);
        try
        {
            string hiddenDocumentPath = Path.Join(fixturePath, "Hidden.cs");
            string visibleDocumentPath = Path.Join(fixturePath, "Visible.cs");
            await File.WriteAllTextAsync(
                Path.Join(fixturePath, ".editorconfig"),
                HiddenDiagnosticEditorConfigText,
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                Path.Join(fixturePath, "Fixture.csproj"),
                HiddenDiagnosticProjectText,
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                hiddenDocumentPath,
                HiddenDiagnosticDocumentText,
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                visibleDocumentPath,
                VisibleDiagnosticDocumentText,
                TestContext.CancellationToken).ConfigureAwait(false);
            await RestoreFixtureAsync(fixturePath).ConfigureAwait(false);

            LspProcessSession lsp = await LspProcessSession.StartAsync(
                "csls-hidden-diagnostic-worker",
                EditorToolResolver.ResolveDotNetHost(),
                [workerPath],
                fixturePath).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable lspCleanup = lsp.ConfigureAwait(false);
            await lsp.InitializeAsync(fixturePath, TestContext.CancellationToken)
                .ConfigureAwait(false);
            await lsp.OpenDocumentAsync(hiddenDocumentPath, HiddenDiagnosticDocumentText)
                .ConfigureAwait(false);
            await lsp.OpenDocumentAsync(visibleDocumentPath, VisibleDiagnosticDocumentText)
                .ConfigureAwait(false);

            DocumentDiagnosticReport visibleReport = await lsp.RequestDiagnosticsAsync(
                visibleDocumentPath,
                previousResultId: null,
                TestContext.CancellationToken).ConfigureAwait(false);
            IReadOnlyList<Diagnostic> visibleItems = visibleReport.Items
                ?? throw new InvalidDataException("A full diagnostic report had no items.");
            Assert.Contains("IDE0058", visibleItems.Select(static diagnostic => diagnostic.Code));
            Assert.Contains("IDE0063", visibleItems.Select(static diagnostic => diagnostic.Code));
            Assert.IsTrue(
                visibleItems
                    .Where(static diagnostic => diagnostic.Code is "IDE0058" or "IDE0063")
                    .All(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Hint));
            Diagnostic simpleUsingDiagnostic = Assert.ContainsSingle(
                visibleItems.Where(static diagnostic => diagnostic.Code == "IDE0063"));
            IReadOnlyList<CodeAction> simpleUsingActions = await lsp.RequestCodeActionsAsync(
                visibleDocumentPath,
                simpleUsingDiagnostic.Range,
                ["quickfix"],
                [simpleUsingDiagnostic],
                TestContext.CancellationToken).ConfigureAwait(false);
            CodeAction simpleUsingAction = Assert.ContainsSingle(simpleUsingActions);
            Assert.AreEqual("Use simple 'using' statement", simpleUsingAction.Title);
            Assert.IsNull(simpleUsingAction.IsPreferred);
            TextDocumentEdit simpleUsingEdit = Assert.ContainsSingle(
                simpleUsingAction.Edit?.DocumentChanges.OfType<TextDocumentEdit>() ?? []);
            string fixedVisibleText = ApplyTextEdits(
                VisibleDiagnosticDocumentText,
                simpleUsingEdit.Edits);
            Assert.Contains(
                "using MemoryStream stream = new();",
                fixedVisibleText,
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                "using (MemoryStream stream = new())",
                fixedVisibleText,
                StringComparison.Ordinal);

            using var informationConfiguration = JsonDocument.Parse(
                """
                {
                  "dotnet": {
                    "diagnostics": {
                      "reportInformationAsHint": false
                    }
                  }
                }
                """);
            await lsp.ChangeConfigurationAsync(informationConfiguration.RootElement)
                .ConfigureAwait(false);
            DocumentDiagnosticReport informationReport = await lsp.RequestDiagnosticsAsync(
                visibleDocumentPath,
                previousResultId: visibleReport.ResultId,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual("full", informationReport.Kind);
            IReadOnlyList<Diagnostic> informationItems = informationReport.Items
                ?? throw new InvalidDataException("A full diagnostic report had no items.");
            Assert.Contains(
                "IDE0058",
                informationItems.Select(static diagnostic => diagnostic.Code));
            Assert.Contains(
                "IDE0063",
                informationItems.Select(static diagnostic => diagnostic.Code));
            Assert.IsTrue(
                informationItems
                    .Where(static diagnostic => diagnostic.Code is "IDE0058" or "IDE0063")
                    .All(
                        static diagnostic =>
                            diagnostic.Severity == DiagnosticSeverity.Information));
            await lsp.ChangeDocumentAsync(
                visibleDocumentPath,
                version: 2,
                [new TextDocumentContentChangeEvent { Text = fixedVisibleText }])
                .ConfigureAwait(false);
            DocumentDiagnosticReport fixedVisibleReport = await lsp.RequestDiagnosticsAsync(
                visibleDocumentPath,
                previousResultId: informationReport.ResultId,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.DoesNotContain(
                "IDE0063",
                fixedVisibleReport.Items?.Select(static diagnostic => diagnostic.Code) ?? []);

            DocumentDiagnosticReport report = await lsp.RequestDiagnosticsAsync(
                hiddenDocumentPath,
                previousResultId: null,
                TestContext.CancellationToken).ConfigureAwait(false);
            IReadOnlyList<Diagnostic> items = report.Items
                ?? throw new InvalidDataException("A full diagnostic report had no items.");
            Assert.DoesNotContain("IDE0058", items.Select(static diagnostic => diagnostic.Code));
            Assert.DoesNotContain("IDE0063", items.Select(static diagnostic => diagnostic.Code));
            IReadOnlyList<Diagnostic> unnecessary =
            [
                .. items.Where(
                    static diagnostic =>
                        diagnostic.Tags?.Contains(DiagnosticTag.Unnecessary) == true)
            ];
            Assert.Contains("CS8019", unnecessary.Select(static diagnostic => diagnostic.Code));
            Assert.IsTrue(
                unnecessary.All(
                    static diagnostic => diagnostic.Severity == DiagnosticSeverity.Hint),
                $"Observed diagnostics: {string.Join(", ", items.Select(static diagnostic => $"{diagnostic.Code}:{diagnostic.Severity}"))}");

            string diagnostics = await lsp.ShutdownAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            Assert.DoesNotContain("Unhandled exception", diagnostics, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(fixturePath, recursive: true);
        }
    }

    private async Task RestoreFixtureAsync(string fixturePath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = EditorToolResolver.ResolveDotNetHost(),
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            WorkingDirectory = fixturePath
        };
        startInfo.ArgumentList.Add("restore");
        startInfo.ArgumentList.Add("Fixture.csproj");
        startInfo.ArgumentList.Add("--nologo");
        startInfo.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        startInfo.Environment["DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE"] = "true";
        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The analyzer fixture restore did not start.");
        Task<string> outputTask = process.StandardOutput.ReadToEndAsync(
            TestContext.CancellationToken);
        Task<string> errorTask = process.StandardError.ReadToEndAsync(
            TestContext.CancellationToken);
        await process.WaitForExitAsync(TestContext.CancellationToken).ConfigureAwait(false);
        string output = await outputTask.ConfigureAwait(false);
        string error = await errorTask.ConfigureAwait(false);
        Assert.AreEqual(
            0,
            process.ExitCode,
            $"Analyzer fixture restore failed.{Environment.NewLine}{output}{Environment.NewLine}{error}");
    }

    private static string ApplyTextEdits(string text, IReadOnlyList<TextEdit> edits)
    {
        var sourceText = SourceText.From(text, Encoding.UTF8);
        IEnumerable<TextChange> changes = edits.Select(edit => new TextChange(
            TextSpan.FromBounds(
                GetOffset(sourceText, edit.Range.Start),
                GetOffset(sourceText, edit.Range.End)),
            edit.NewText));
        return sourceText.WithChanges(changes).ToString();
    }

    private static int GetOffset(SourceText text, Position position) =>
        text.Lines[position.Line].Start + position.Character;

    /// <summary>
    /// Invalidates snapshot diagnostics after a real incremental edit and preserves analyzer findings.
    /// </summary>
    [TestMethod]
    public async Task PullDiagnosticsTrackIncrementalDocumentGeneration()
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
            $"csls-diagnostics-{Guid.NewGuid():N}");
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

            LspProcessSession lsp = await LspProcessSession.StartAsync(
                "csls-diagnostic-worker",
                EditorToolResolver.ResolveDotNetHost(),
                [workerPath],
                fixturePath).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable lspCleanup = lsp.ConfigureAwait(false);
            JsonElement initialization = await lsp.InitializeAsync(
                fixturePath,
                TestContext.CancellationToken).ConfigureAwait(false);
            JsonElement diagnosticProvider = initialization
                .GetProperty("capabilities")
                .GetProperty("diagnosticProvider");
            Assert.AreEqual("csls", diagnosticProvider.GetProperty("identifier").GetString());
            Assert.IsTrue(diagnosticProvider.GetProperty("interFileDependencies").GetBoolean());
            Assert.IsTrue(diagnosticProvider.GetProperty("workspaceDiagnostics").GetBoolean());
            await lsp.OpenDocumentAsync(documentPath, DocumentText).ConfigureAwait(false);

            DocumentDiagnosticReport initial = await lsp.RequestDiagnosticsAsync(
                documentPath,
                previousResultId: null,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual("full", initial.Kind);
            Assert.IsNotNull(initial.ResultId);
            IReadOnlyList<Diagnostic> initialItems = initial.Items
                ?? throw new InvalidDataException("A full diagnostic report had no items.");
            Assert.Contains("CS0103", initialItems.Select(static diagnostic => diagnostic.Code));
            Assert.Contains("CA1822", initialItems.Select(static diagnostic => diagnostic.Code));

            DocumentDiagnosticReport unchanged = await lsp.RequestDiagnosticsAsync(
                documentPath,
                initial.ResultId,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual("unchanged", unchanged.Kind);
            Assert.AreEqual(initial.ResultId, unchanged.ResultId);
            Assert.IsNull(unchanged.Items);

            await lsp.ChangeDocumentAsync(
                documentPath,
                version: 2,
                [
                    new TextDocumentContentChangeEvent
                    {
                        Range = new LspRange(
                            new Position(8, 26),
                            new Position(8, 33)),
                        RangeLength = 7,
                        Text = "\"hello\""
                    }
                ]).ConfigureAwait(false);
            await lsp.SaveDocumentAsync(documentPath).ConfigureAwait(false);

            DocumentDiagnosticReport updated = await lsp.RequestDiagnosticsAsync(
                documentPath,
                initial.ResultId,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual("full", updated.Kind);
            Assert.AreNotEqual(initial.ResultId, updated.ResultId);
            IReadOnlyList<Diagnostic> updatedItems = updated.Items
                ?? throw new InvalidDataException("An updated diagnostic report had no items.");
            Assert.DoesNotContain("CS0103", updatedItems.Select(static diagnostic => diagnostic.Code));
            Assert.Contains("CA1822", updatedItems.Select(static diagnostic => diagnostic.Code));

            DocumentDiagnosticReport updatedUnchanged = await lsp.RequestDiagnosticsAsync(
                documentPath,
                updated.ResultId,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual("unchanged", updatedUnchanged.Kind);

            string diagnostics = await lsp.ShutdownAsync(
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.DoesNotContain("Unhandled exception", diagnostics, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(fixturePath, recursive: true);
        }
    }

    private const string ProjectText = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
            <ImplicitUsings>enable</ImplicitUsings>
            <EnableNETAnalyzers>true</EnableNETAnalyzers>
            <EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>
            <AnalysisLevel>latest</AnalysisLevel>
            <AnalysisMode>AllEnabledByDefault</AnalysisMode>
          </PropertyGroup>
        </Project>
        """;

    private const string HiddenDiagnosticProjectText = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
            <ImplicitUsings>disable</ImplicitUsings>
            <EnableNETAnalyzers>true</EnableNETAnalyzers>
            <EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>
            <AnalysisLevel>latest</AnalysisLevel>
          </PropertyGroup>
          <ItemGroup>
            <PackageReference Include="Microsoft.CodeAnalysis.NetAnalyzers"
                              Version="10.0.400"
                              PrivateAssets="All" />
          </ItemGroup>
        </Project>
        """;

    private static string HiddenDiagnosticEditorConfigText => string.Join(
        Environment.NewLine,
        "root = true",
        string.Empty,
        "[Hidden.cs]",
        string.Concat("dotnet_diagnostic.", "IDE0005", ".severity = ", "silent"),
        "csharp_style_unused_value_expression_statement_preference = discard_variable:silent",
        "csharp_prefer_simple_using_statement = true:silent",
        string.Empty,
        "[Visible.cs]",
        "csharp_style_unused_value_expression_statement_preference = discard_variable:suggestion",
        "csharp_prefer_simple_using_statement = true:suggestion",
        string.Empty);

    private const string HiddenDiagnosticDocumentText = """
        using System;
        using System.IO;
        using System.Text;

        namespace Hidden;

        public static class Program
        {
            public static void Run()
            {
                Directory.CreateDirectory("fixture");
                int value = (1);
                using (MemoryStream stream = new())
                {
                    Console.WriteLine(stream.Length + value);
                }
            }
        }
        """;

    private const string VisibleDiagnosticDocumentText = """
        using System.IO;

        namespace Visible;

        public static class Program
        {
            public static void Run()
            {
                Directory.CreateDirectory("fixture");
                using (MemoryStream stream = new())
                {
                    _ = stream.Length;
                }
            }
        }
        """;

    private const string DocumentText = """
        namespace Fixture;

        public sealed class Program
        {
            public int GetValue() => 42;

            public static void Main()
            {
                Console.WriteLine(Missing);
            }
        }
        """;
}
