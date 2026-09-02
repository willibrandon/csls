using Csls.Protocol;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace Csls.Tests;

/// <summary>
/// Verifies real configuration pull and dynamic multi-root workspace behavior.
/// </summary>
[TestClass]
public sealed class WorkspaceConfigurationLanguageServerTests
{
    /// <summary>
    /// Gets the active MSTest context and its framework-managed cancellation token.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Pulls legacy and preferred settings with precedence and refreshes analyzer diagnostics.
    /// </summary>
    [TestMethod]
    public async Task ConfigurationPullUsesPreferredSectionAndRefreshesDiagnostics()
    {
        string repositoryRoot = EditorToolResolver.FindRepositoryRoot();
        string workerPath = ResolveWorkerPath(repositoryRoot);
        string fixturePath = Path.Join(
            Path.GetTempPath(),
            $"csls-configuration-{Guid.NewGuid():N}");
        Directory.CreateDirectory(fixturePath);
        try
        {
            string documentPath = Path.Join(fixturePath, "Program.cs");
            await WriteProjectAsync(
                fixturePath,
                documentPath,
                DiagnosticDocumentText).ConfigureAwait(false);
            var client = new LspTestClient(
                """{"enableAnalyzers":false,"formatOnSave":true}""",
                """{"enableAnalyzers":true,"formatOnSave":false}""");
            LspProcessSession lsp = await LspProcessSession.StartAsync(
                "csls-configuration-worker",
                EditorToolResolver.ResolveDotNetHost(),
                [workerPath],
                fixturePath,
                client).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable lspCleanup = lsp.ConfigureAwait(false);
            using var capabilitiesDocument = JsonDocument.Parse(
                """
                {
                  "workspace": {
                    "configuration": true,
                    "inlayHint": {"refreshSupport": true},
                    "workspaceFolders": true
                  }
                }
                """);
            JsonElement initialization = await lsp.InitializeAsync(
                fixturePath,
                capabilitiesDocument.RootElement,
                TestContext.CancellationToken).ConfigureAwait(false);
            JsonElement workspaceFolders = initialization
                .GetProperty("capabilities")
                .GetProperty("workspace")
                .GetProperty("workspaceFolders");
            Assert.IsTrue(workspaceFolders.GetProperty("supported").GetBoolean());
            Assert.IsTrue(workspaceFolders.GetProperty("changeNotifications").GetBoolean());
            await lsp.OpenDocumentAsync(documentPath, DiagnosticDocumentText)
                .ConfigureAwait(false);
            await client.WaitForConfigurationRequestAsync(
                expectedCount: 1,
                TestContext.CancellationToken).ConfigureAwait(false);
            string unformattedText = DiagnosticDocumentText.Replace(
                "public int GetValue() => 42;",
                "public int GetValue()=>42;",
                StringComparison.Ordinal);
            await lsp.ChangeDocumentAsync(
                documentPath,
                version: 2,
                [new TextDocumentContentChangeEvent { Text = unformattedText }])
                .ConfigureAwait(false);
            IReadOnlyList<TextEdit> preferredSaveFormatting = await lsp
                .RequestSaveFormattingAsync(
                    documentPath,
                    TextDocumentSaveReason.Manual,
                    TestContext.CancellationToken)
                .ConfigureAwait(false);
            Assert.IsEmpty(preferredSaveFormatting);

            DocumentDiagnosticReport enabled = await lsp.RequestDiagnosticsAsync(
                documentPath,
                previousResultId: null,
                TestContext.CancellationToken).ConfigureAwait(false);
            IReadOnlyList<Diagnostic> enabledItems = enabled.Items
                ?? throw new InvalidDataException("The enabled diagnostic report had no items.");
            Assert.Contains("CS0103", enabledItems.Select(static diagnostic => diagnostic.Code));
            Assert.Contains("CA1822", enabledItems.Select(static diagnostic => diagnostic.Code));

            client.SetConfiguration(
                """
                {
                  "enableAnalyzers": false,
                  "formatOnSave": true,
                  "inlayHints": {"enableInlayHintsForParameters": true}
                }
                """,
                preferredConfiguration: null);
            using var emptySettings = JsonDocument.Parse("{}");
            await lsp.ChangeConfigurationAsync(emptySettings.RootElement).ConfigureAwait(false);
            await client.WaitForConfigurationRequestAsync(
                expectedCount: 2,
                TestContext.CancellationToken).ConfigureAwait(false);
            await client.WaitForInlayHintRefreshAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            DocumentDiagnosticReport disabled = await lsp.RequestDiagnosticsAsync(
                documentPath,
                enabled.ResultId,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual("full", disabled.Kind);
            Assert.AreNotEqual(enabled.ResultId, disabled.ResultId);
            IReadOnlyList<Diagnostic> disabledItems = disabled.Items
                ?? throw new InvalidDataException("The disabled diagnostic report had no items.");
            Assert.Contains("CS0103", disabledItems.Select(static diagnostic => diagnostic.Code));
            Assert.DoesNotContain(
                "CA1822",
                disabledItems.Select(static diagnostic => diagnostic.Code));
            IReadOnlyList<TextEdit> legacySaveFormatting = await lsp
                .RequestSaveFormattingAsync(
                    documentPath,
                    TextDocumentSaveReason.Manual,
                    TestContext.CancellationToken)
                .ConfigureAwait(false);
            Assert.IsNotEmpty(legacySaveFormatting);
            Assert.Contains(
                "public int GetValue() => 42;",
                ApplyTextEdits(unformattedText, legacySaveFormatting),
                StringComparison.Ordinal);

            string diagnostics = await lsp.ShutdownAsync(
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.DoesNotContain("Unhandled exception", diagnostics, StringComparison.Ordinal);
        }
        finally
        {
            await DirectoryReleaseWaiter.DeleteAsync(fixturePath, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Applies pushed settings when configuration pull is unavailable and honors section precedence.
    /// </summary>
    [TestMethod]
    public async Task PushedConfigurationUsesPreferredSectionWithoutPullCapability()
    {
        string repositoryRoot = EditorToolResolver.FindRepositoryRoot();
        string workerPath = ResolveWorkerPath(repositoryRoot);
        string fixturePath = Path.Join(
            Path.GetTempPath(),
            $"csls-pushed-configuration-{Guid.NewGuid():N}");
        Directory.CreateDirectory(fixturePath);
        try
        {
            string documentPath = Path.Join(fixturePath, "Program.cs");
            await WriteProjectAsync(
                fixturePath,
                documentPath,
                DiagnosticDocumentText).ConfigureAwait(false);
            LspProcessSession lsp = await LspProcessSession.StartAsync(
                "csls-pushed-configuration-worker",
                EditorToolResolver.ResolveDotNetHost(),
                [workerPath],
                fixturePath).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable lspCleanup = lsp.ConfigureAwait(false);
            using var capabilitiesDocument = JsonDocument.Parse("{}");
            using var initializationOptions = JsonDocument.Parse(
                """{"csharp":{"enableAnalyzers":true}}""");
            await lsp.InitializeAsync(
                [fixturePath],
                capabilitiesDocument.RootElement,
                initializationOptions.RootElement,
                TestContext.CancellationToken).ConfigureAwait(false);
            await lsp.OpenDocumentAsync(documentPath, DiagnosticDocumentText)
                .ConfigureAwait(false);

            DocumentDiagnosticReport enabled = await lsp.RequestDiagnosticsAsync(
                documentPath,
                previousResultId: null,
                TestContext.CancellationToken).ConfigureAwait(false);
            IReadOnlyList<Diagnostic> enabledItems = enabled.Items
                ?? throw new InvalidDataException("The enabled diagnostic report had no items.");
            Assert.Contains("CA1822", enabledItems.Select(static diagnostic => diagnostic.Code));

            using var pushedSettings = JsonDocument.Parse(
                """
                {
                  "csharp": {"enableAnalyzers": true},
                  "csls": {"enableAnalyzers": false}
                }
                """);
            await lsp.ChangeConfigurationAsync(pushedSettings.RootElement).ConfigureAwait(false);
            DocumentDiagnosticReport disabled = await lsp.RequestDiagnosticsAsync(
                documentPath,
                enabled.ResultId,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual("full", disabled.Kind);
            Assert.AreNotEqual(enabled.ResultId, disabled.ResultId);
            IReadOnlyList<Diagnostic> disabledItems = disabled.Items
                ?? throw new InvalidDataException("The disabled diagnostic report had no items.");
            Assert.Contains("CS0103", disabledItems.Select(static diagnostic => diagnostic.Code));
            Assert.DoesNotContain(
                "CA1822",
                disabledItems.Select(static diagnostic => diagnostic.Code));

            string diagnostics = await lsp.ShutdownAsync(
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.DoesNotContain("Unhandled exception", diagnostics, StringComparison.Ordinal);
        }
        finally
        {
            await DirectoryReleaseWaiter.DeleteAsync(fixturePath, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Reloads a real project when the selected MSBuild configuration changes.
    /// </summary>
    [TestMethod]
    public async Task BuildConfigurationReloadsConditionalCompilation()
    {
        string repositoryRoot = EditorToolResolver.FindRepositoryRoot();
        string workerPath = ResolveWorkerPath(repositoryRoot);
        string fixturePath = Path.Join(
            Path.GetTempPath(),
            $"csls-build-configuration-{Guid.NewGuid():N}");
        Directory.CreateDirectory(fixturePath);
        try
        {
            string documentPath = Path.Join(fixturePath, "Configured.cs");
            await File.WriteAllTextAsync(
                Path.Join(fixturePath, "Fixture.csproj"),
                ConditionalProjectText,
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                documentPath,
                ConditionalDocumentText,
                TestContext.CancellationToken).ConfigureAwait(false);
            LspProcessSession lsp = await LspProcessSession.StartAsync(
                "csls-build-configuration-worker",
                EditorToolResolver.ResolveDotNetHost(),
                [workerPath],
                fixturePath).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable lspCleanup = lsp.ConfigureAwait(false);
            using var capabilities = JsonDocument.Parse("{}");
            using var initializationOptions = JsonDocument.Parse(
                """{"csls":{"configuration":"Release"}}""");
            await lsp.InitializeAsync(
                [fixturePath],
                capabilities.RootElement,
                initializationOptions.RootElement,
                TestContext.CancellationToken).ConfigureAwait(false);
            await lsp.CompleteInitializationAsync().ConfigureAwait(false);

            IReadOnlyList<WorkspaceSymbol> releaseSymbols = await lsp
                .RequestWorkspaceSymbolsAsync(
                    "ReleaseConfigurationSymbol",
                    TestContext.CancellationToken).ConfigureAwait(false);
            Assert.ContainsSingle(releaseSymbols);
            IReadOnlyList<WorkspaceSymbol> absentDebugSymbols = await lsp
                .RequestWorkspaceSymbolsAsync(
                    "DebugConfigurationSymbol",
                    TestContext.CancellationToken).ConfigureAwait(false);
            Assert.IsEmpty(absentDebugSymbols);

            using var debugConfiguration = JsonDocument.Parse(
                """{"csls":{"configuration":"Debug"}}""");
            await lsp.ChangeConfigurationAsync(debugConfiguration.RootElement)
                .ConfigureAwait(false);
            IReadOnlyList<WorkspaceSymbol> debugSymbols = await lsp
                .RequestWorkspaceSymbolsAsync(
                    "DebugConfigurationSymbol",
                    TestContext.CancellationToken).ConfigureAwait(false);
            Assert.ContainsSingle(debugSymbols);
            IReadOnlyList<WorkspaceSymbol> absentReleaseSymbols = await lsp
                .RequestWorkspaceSymbolsAsync(
                    "ReleaseConfigurationSymbol",
                    TestContext.CancellationToken).ConfigureAwait(false);
            Assert.IsEmpty(absentReleaseSymbols);

            string diagnostics = await lsp.ShutdownAsync(
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.DoesNotContain("Unhandled exception", diagnostics, StringComparison.Ordinal);
        }
        finally
        {
            await DirectoryReleaseWaiter.DeleteAsync(fixturePath, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Filters real worker diagnostics at initialization and after a dynamic level change.
    /// </summary>
    [TestMethod]
    public async Task LogLevelFiltersWorkerDiagnostics()
    {
        string repositoryRoot = EditorToolResolver.FindRepositoryRoot();
        string workerPath = ResolveWorkerPath(repositoryRoot);
        string fixturePath = Path.Join(
            Path.GetTempPath(),
            $"csls-log-level-{Guid.NewGuid():N}");
        Directory.CreateDirectory(fixturePath);
        try
        {
            string documentPath = Path.Join(fixturePath, "Program.cs");
            await WriteProjectAsync(fixturePath, documentPath, DiagnosticDocumentText)
                .ConfigureAwait(false);
            LspProcessSession lsp = await LspProcessSession.StartAsync(
                "csls-log-level-worker",
                EditorToolResolver.ResolveDotNetHost(),
                [workerPath],
                fixturePath).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable lspCleanup = lsp.ConfigureAwait(false);
            using var capabilities = JsonDocument.Parse("{}");
            using var initializationOptions = JsonDocument.Parse(
                """{"csls":{"logLevel":"Error"}}""");
            await lsp.InitializeAsync(
                [fixturePath],
                capabilities.RootElement,
                initializationOptions.RootElement,
                TestContext.CancellationToken).ConfigureAwait(false);
            await lsp.CompleteInitializationAsync().ConfigureAwait(false);
            IReadOnlyList<WorkspaceSymbol> symbols = await lsp.RequestWorkspaceSymbolsAsync(
                "Program",
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.ContainsSingle(symbols);
            using var traceConfiguration = JsonDocument.Parse(
                """{"csls":{"logLevel":"Trace"}}""");
            await lsp.ChangeConfigurationAsync(traceConfiguration.RootElement)
                .ConfigureAwait(false);
            await WaitForCompletedRequestAsync(
                lsp,
                "workspace/didChangeConfiguration",
                TestContext.CancellationToken).ConfigureAwait(false);

            string diagnostics = await lsp.ShutdownAsync(
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.DoesNotContain(
                "C# workspace ready in ",
                diagnostics,
                StringComparison.Ordinal);
            Assert.Contains(
                "Applied configuration:",
                diagnostics,
                StringComparison.Ordinal);
            Assert.Contains(
                "log level=Trace",
                diagnostics,
                StringComparison.Ordinal);
        }
        finally
        {
            await DirectoryReleaseWaiter.DeleteAsync(fixturePath, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Adds and removes real workspace roots while retaining unsaved documents in unchanged roots.
    /// </summary>
    [TestMethod]
    public async Task WorkspaceFolderChangesPreserveRetainedDocumentOverlays()
    {
        string repositoryRoot = EditorToolResolver.FindRepositoryRoot();
        string workerPath = ResolveWorkerPath(repositoryRoot);
        string fixturePath = Path.Join(
            Path.GetTempPath(),
            $"csls-workspace-folders-{Guid.NewGuid():N}");
        string firstRoot = Path.Join(fixturePath, "first");
        string secondRoot = Path.Join(fixturePath, "second");
        Directory.CreateDirectory(firstRoot);
        Directory.CreateDirectory(secondRoot);
        try
        {
            string firstDocumentPath = Path.Join(firstRoot, "First.cs");
            string secondDocumentPath = Path.Join(secondRoot, "Second.cs");
            await WriteProjectAsync(firstRoot, firstDocumentPath, FirstDocumentText)
                .ConfigureAwait(false);
            await WriteProjectAsync(secondRoot, secondDocumentPath, SecondDocumentText)
                .ConfigureAwait(false);

            LspProcessSession lsp = await LspProcessSession.StartAsync(
                "csls-workspace-folders-worker",
                EditorToolResolver.ResolveDotNetHost(),
                [workerPath],
                firstRoot).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable lspCleanup = lsp.ConfigureAwait(false);
            using var capabilitiesDocument = JsonDocument.Parse(
                """{"workspace":{"workspaceFolders":true}}""");
            await lsp.InitializeAsync(
                firstRoot,
                capabilitiesDocument.RootElement,
                TestContext.CancellationToken).ConfigureAwait(false);
            await lsp.OpenDocumentAsync(firstDocumentPath, FirstDocumentText)
                .ConfigureAwait(false);
            await lsp.ChangeDocumentAsync(
                firstDocumentPath,
                version: 2,
                [new TextDocumentContentChangeEvent { Text = FirstOverlayText }])
                .ConfigureAwait(false);

            await lsp.ChangeWorkspaceFoldersAsync(
                added: [secondRoot],
                removed: []).ConfigureAwait(false);
            IReadOnlyList<WorkspaceSymbol> overlaySymbols =
                await lsp.RequestWorkspaceSymbolsAsync(
                    "OverlayOnlyType",
                    TestContext.CancellationToken).ConfigureAwait(false);
            WorkspaceSymbol overlay = Assert.ContainsSingle(overlaySymbols);
            Assert.AreEqual(
                DocumentUri.FromFileSystemPath(firstDocumentPath),
                overlay.Location.Uri);
            IReadOnlyList<WorkspaceSymbol> secondSymbols =
                await lsp.RequestWorkspaceSymbolsAsync(
                    "SecondFolderType",
                    TestContext.CancellationToken).ConfigureAwait(false);
            WorkspaceSymbol second = Assert.ContainsSingle(secondSymbols);
            Assert.AreEqual(
                DocumentUri.FromFileSystemPath(secondDocumentPath),
                second.Location.Uri);

            await lsp.CloseDocumentAsync(firstDocumentPath).ConfigureAwait(false);
            await lsp.ChangeWorkspaceFoldersAsync(
                added: [],
                removed: [firstRoot]).ConfigureAwait(false);
            IReadOnlyList<WorkspaceSymbol> removedSymbols =
                await lsp.RequestWorkspaceSymbolsAsync(
                    "FirstFolderType",
                    TestContext.CancellationToken).ConfigureAwait(false);
            Assert.IsEmpty(removedSymbols);
            IReadOnlyList<WorkspaceSymbol> retainedSymbols =
                await lsp.RequestWorkspaceSymbolsAsync(
                    "SecondFolderType",
                    TestContext.CancellationToken).ConfigureAwait(false);
            Assert.ContainsSingle(retainedSymbols);

            string diagnostics = await lsp.ShutdownAsync(
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.DoesNotContain("Unhandled exception", diagnostics, StringComparison.Ordinal);
        }
        finally
        {
            await DirectoryReleaseWaiter.DeleteAsync(fixturePath, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
    }

    private static async Task WaitForCompletedRequestAsync(
        LspProcessSession lsp,
        string requestName,
        CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(10));
        while (true)
        {
            CSharpDebugInfo info = await lsp.RequestDebugInfoAsync(cancellationToken)
                .ConfigureAwait(false);
            if (info.RequestQueue.Stats.Any(
                statistic => string.Equals(
                    statistic.Name,
                    requestName,
                    StringComparison.Ordinal)))
            {
                return;
            }

            await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task WriteProjectAsync(
        string rootPath,
        string documentPath,
        string documentText)
    {
        await File.WriteAllTextAsync(
            Path.Join(rootPath, "Fixture.csproj"),
            ProjectText,
            TestContext.CancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(
            documentPath,
            documentText,
            TestContext.CancellationToken).ConfigureAwait(false);
    }

    private static string ResolveWorkerPath(string repositoryRoot)
    {
        string workerPath = Path.Join(
            EditorToolResolver.ResolveArtifactsRoot(repositoryRoot),
            "bin",
            "Csls.Worker",
            "debug",
            "csls-worker.dll");
        return File.Exists(workerPath)
            ? workerPath
            : throw new FileNotFoundException("The language-server worker was not built.", workerPath);
    }

    private static string ApplyTextEdits(string text, IReadOnlyList<TextEdit> edits)
    {
        (int Start, int End, string NewText)[] replacements =
        [
            .. edits
                .Select(edit => (
                    Start: GetOffset(text, edit.Range.Start),
                    End: GetOffset(text, edit.Range.End),
                    edit.NewText))
                .OrderByDescending(static edit => edit.Start)
        ];
        var builder = new StringBuilder(text);
        foreach ((int start, int end, string newText) in replacements)
        {
            builder.Remove(start, end - start);
            builder.Insert(start, newText);
        }

        return builder.ToString();
    }

    private static int GetOffset(string text, Position position)
    {
        int line = 0;
        int offset = 0;
        while (line < position.Line && offset < text.Length)
        {
            if (text[offset++] == '\n')
            {
                line++;
            }
        }

        Assert.AreEqual(position.Line, line);
        int result = offset + position.Character;
        Assert.IsLessThanOrEqualTo(text.Length, result);
        return result;
    }

    private const string ProjectText = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
            <ImplicitUsings>enable</ImplicitUsings>
            <EnableNETAnalyzers>true</EnableNETAnalyzers>
            <AnalysisLevel>latest</AnalysisLevel>
            <AnalysisMode>AllEnabledByDefault</AnalysisMode>
          </PropertyGroup>
        </Project>
        """;

    private const string ConditionalProjectText = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
            <DefineConstants Condition="'$(Configuration)' == 'Debug'">DEBUG_CONFIGURATION</DefineConstants>
            <DefineConstants Condition="'$(Configuration)' == 'Release'">RELEASE_CONFIGURATION</DefineConstants>
          </PropertyGroup>
        </Project>
        """;

    private const string ConditionalDocumentText = """
        namespace Fixture;

        #if RELEASE_CONFIGURATION
        public sealed class ReleaseConfigurationSymbol
        {
        }
        #endif

        #if DEBUG_CONFIGURATION
        public sealed class DebugConfigurationSymbol
        {
        }
        #endif
        """;

    private const string DiagnosticDocumentText = """
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

    private const string FirstDocumentText = """
        namespace First;

        public sealed class FirstFolderType
        {
        }
        """;

    private const string FirstOverlayText = """
        namespace First;

        public sealed class FirstFolderType
        {
        }

        public sealed class OverlayOnlyType
        {
        }
        """;

    private const string SecondDocumentText = """
        namespace Second;

        public sealed class SecondFolderType
        {
        }
        """;
}
