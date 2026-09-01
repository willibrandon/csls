using Csls.Control;
using Csls.Control.Contracts;
using Csls.Protocol;
using StreamJsonRpc;
using System.Runtime.CompilerServices;
using System.Text;

namespace Csls.Tests;

/// <summary>
/// Verifies Razor rename through a real language-server worker process.
/// </summary>
[TestClass]
public sealed class RazorRenameLanguageServerTests
{
    /// <summary>
    /// Gets the active MSTest context and its framework-managed cancellation token.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Renames Razor-local and shared C# symbols from persisted and current Razor snapshots.
    /// </summary>
    /// <param name="documentRelativePath">The Razor document path within the fixture.</param>
    /// <param name="importsRelativePath">The matching Razor imports path within the fixture.</param>
    /// <param name="membersDirective">The Razor directive that declares generated class members.</param>
    [TestMethod]
    [DataRow("Component.razor", "_Imports.razor", "code")]
    [DataRow("Pages/Index.cshtml", "Pages/_ViewImports.cshtml", "functions")]
    public async Task RazorRenameMapsCurrentSourceAndCSharpDocuments(
        string documentRelativePath,
        string importsRelativePath,
        string membersDirective)
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
            $"csls-razor-rename-{Guid.NewGuid():N}");
        Directory.CreateDirectory(fixturePath);
        try
        {
            string documentPath = Path.Join(fixturePath, documentRelativePath);
            string importsPath = Path.Join(fixturePath, importsRelativePath);
            string declarationsPath = Path.Join(fixturePath, "SharedValues.cs");
            string consumerPath = Path.Join(fixturePath, "SharedConsumer.cs");
            Directory.CreateDirectory(
                Path.GetDirectoryName(documentPath)
                    ?? throw new InvalidOperationException("The Razor fixture has no directory."));
            string persistedText = CreateRazorText(membersDirective, "LocalValue");
            string overlayText = CreateRazorText(membersDirective, "OverlayValue");
            await File.WriteAllTextAsync(
                Path.Join(fixturePath, "Fixture.csproj"),
                ProjectText,
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                declarationsPath,
                SharedValuesText,
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                consumerPath,
                SharedConsumerText,
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                importsPath,
                "@using Fixture",
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                documentPath,
                persistedText,
                TestContext.CancellationToken).ConfigureAwait(false);

            LspProcessSession lsp = await LspProcessSession.StartAsync(
                "csls-razor-rename-worker",
                EditorToolResolver.ResolveDotNetHost(),
                [workerPath],
                fixturePath).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable lspCleanup = lsp.ConfigureAwait(false);
            await lsp.InitializeAsync(
                fixturePath,
                TestContext.CancellationToken).ConfigureAwait(false);
            await lsp.OpenDocumentAsync(documentPath, persistedText, "razor")
                .ConfigureAwait(false);

            PrepareRenameResult? localPreparation = await lsp.PrepareRenameAsync(
                documentPath,
                new Position(0, 8),
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.IsNotNull(localPreparation);
            Assert.AreEqual("LocalValue", localPreparation.Placeholder);
            Assert.AreEqual(new Position(0, 4), localPreparation.Range.Start);
            Assert.AreEqual(new Position(0, 14), localPreparation.Range.End);

            WorkspaceEdit localRename = await lsp.RequestRenameAsync(
                documentPath,
                new Position(0, 8),
                "CurrentValue",
                TestContext.CancellationToken).ConfigureAwait(false);
            TextDocumentEdit localDocumentEdit = Assert.ContainsSingle(
                localRename.DocumentChanges.OfType<TextDocumentEdit>());
            Assert.AreEqual(
                DocumentUri.FromFileSystemPath(documentPath),
                localDocumentEdit.TextDocument.Uri);
            Assert.AreEqual(1, localDocumentEdit.TextDocument.Version);
            Assert.HasCount(2, localDocumentEdit.Edits);
            Assert.IsTrue(localDocumentEdit.Edits.All(
                static edit => edit.NewText == "CurrentValue"));

            PrepareRenameResult? sharedPreparation = await lsp.PrepareRenameAsync(
                documentPath,
                new Position(5, 13),
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.IsNotNull(sharedPreparation);
            Assert.AreEqual("SharedValue", sharedPreparation.Placeholder);
            Assert.AreEqual(new Position(5, 10), sharedPreparation.Range.Start);
            Assert.AreEqual(new Position(5, 21), sharedPreparation.Range.End);

            WorkspaceEdit sharedRename = await lsp.RequestRenameAsync(
                documentPath,
                new Position(5, 13),
                "SharedText",
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.HasCount(3, sharedRename.DocumentChanges);
            AssertDocumentEdit(
                sharedRename,
                documentPath,
                expectedVersion: 1,
                expectedEditCount: 2,
                persistedText,
                persistedText.Replace("SharedValue", "SharedText", StringComparison.Ordinal));
            AssertDocumentEdit(
                sharedRename,
                declarationsPath,
                expectedVersion: null,
                expectedEditCount: 1,
                SharedValuesText,
                SharedValuesText.Replace("SharedValue", "SharedText", StringComparison.Ordinal));
            AssertDocumentEdit(
                sharedRename,
                consumerPath,
                expectedVersion: null,
                expectedEditCount: 1,
                SharedConsumerText,
                SharedConsumerText.Replace("SharedValue", "SharedText", StringComparison.Ordinal));

            WorkspaceEdit csharpRename = await lsp.RequestRenameAsync(
                declarationsPath,
                new Position(4, 29),
                "SharedLabel",
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.HasCount(3, csharpRename.DocumentChanges);
            AssertDocumentEdit(
                csharpRename,
                documentPath,
                expectedVersion: 1,
                expectedEditCount: 2,
                persistedText,
                persistedText.Replace("SharedValue", "SharedLabel", StringComparison.Ordinal));
            AssertDocumentEdit(
                csharpRename,
                declarationsPath,
                expectedVersion: null,
                expectedEditCount: 1,
                SharedValuesText,
                SharedValuesText.Replace("SharedValue", "SharedLabel", StringComparison.Ordinal));
            AssertDocumentEdit(
                csharpRename,
                consumerPath,
                expectedVersion: null,
                expectedEditCount: 1,
                SharedConsumerText,
                SharedConsumerText.Replace("SharedValue", "SharedLabel", StringComparison.Ordinal));

            ControlSessionInfo session = await ControlSessionWaiter.WaitForRunningAsync(
                fixturePath,
                TimeSpan.FromSeconds(60),
                TestContext.CancellationToken).ConfigureAwait(false);
            var control = new ControlRpcClient(session.SocketPath);
            await using ConfiguredAsyncDisposable controlCleanup = control.ConfigureAwait(false);
            ControlEditPlan controlRename = await control.PreviewRenameAsync(
                new ControlRenameRequest
                {
                    DocumentPath = documentPath,
                    Position = new Position(5, 13),
                    NewName = "SharedResult"
                },
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual("rename", controlRename.Operation);
            Assert.HasCount(3, controlRename.Edit.DocumentChanges);
            Assert.HasCount(3, controlRename.Preconditions);
            AssertDocumentEdit(
                controlRename.Edit,
                documentPath,
                expectedVersion: 1,
                expectedEditCount: 2,
                persistedText,
                persistedText.Replace("SharedValue", "SharedResult", StringComparison.Ordinal));

            RemoteInvocationException invalidRename =
                await Assert.ThrowsExactlyAsync<RemoteInvocationException>(
                    async () => await lsp.RequestRenameAsync(
                        documentPath,
                        new Position(5, 13),
                        "not valid",
                        TestContext.CancellationToken).ConfigureAwait(false))
                    .ConfigureAwait(false);
            Assert.Contains("valid C# identifier", invalidRename.Message, StringComparison.Ordinal);

            RemoteInvocationException conflictingRename =
                await Assert.ThrowsExactlyAsync<RemoteInvocationException>(
                    async () => await lsp.RequestRenameAsync(
                        documentPath,
                        new Position(0, 8),
                        "ExistingValue",
                        TestContext.CancellationToken).ConfigureAwait(false))
                    .ConfigureAwait(false);
            Assert.Contains("symbol binding", conflictingRename.Message, StringComparison.Ordinal);

            await lsp.ChangeDocumentAsync(
                documentPath,
                version: 2,
                [new TextDocumentContentChangeEvent { Text = overlayText }])
                .ConfigureAwait(false);
            PrepareRenameResult? overlayPreparation = await lsp.PrepareRenameAsync(
                documentPath,
                new Position(0, 8),
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.IsNotNull(overlayPreparation);
            Assert.AreEqual("OverlayValue", overlayPreparation.Placeholder);
            Assert.AreEqual(new Position(0, 4), overlayPreparation.Range.Start);
            Assert.AreEqual(new Position(0, 16), overlayPreparation.Range.End);

            WorkspaceEdit overlayRename = await lsp.RequestRenameAsync(
                documentPath,
                new Position(0, 8),
                "CurrentValue",
                TestContext.CancellationToken).ConfigureAwait(false);
            AssertDocumentEdit(
                overlayRename,
                documentPath,
                expectedVersion: 2,
                expectedEditCount: 2,
                overlayText,
                overlayText.Replace("OverlayValue", "CurrentValue", StringComparison.Ordinal));

            await lsp.CloseDocumentAsync(documentPath).ConfigureAwait(false);
            PrepareRenameResult? restoredPreparation = await lsp.PrepareRenameAsync(
                documentPath,
                new Position(5, 13),
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.IsNotNull(restoredPreparation);
            Assert.AreEqual("SharedValue", restoredPreparation.Placeholder);
            ControlEditPlan closedRename = await control.PreviewRenameAsync(
                new ControlRenameRequest
                {
                    DocumentPath = documentPath,
                    Position = new Position(5, 13),
                    NewName = "SharedFinal"
                },
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.IsTrue(closedRename.Preconditions.All(
                static precondition => precondition.Version is null));
            ControlApplyEditPlanResult applied = await control.ApplyEditPlanAsync(
                new ControlApplyEditPlanRequest { PlanId = closedRename.PlanId },
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.HasCount(3, applied.DocumentPaths);
            Assert.AreEqual(
                persistedText.Replace("SharedValue", "SharedFinal", StringComparison.Ordinal),
                await File.ReadAllTextAsync(
                    documentPath,
                    TestContext.CancellationToken).ConfigureAwait(false));
            Assert.AreEqual(
                SharedValuesText.Replace("SharedValue", "SharedFinal", StringComparison.Ordinal),
                await File.ReadAllTextAsync(
                    declarationsPath,
                    TestContext.CancellationToken).ConfigureAwait(false));
            PrepareRenameResult? appliedPreparation = await lsp.PrepareRenameAsync(
                documentPath,
                new Position(5, 13),
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.IsNotNull(appliedPreparation);
            Assert.AreEqual("SharedFinal", appliedPreparation.Placeholder);

            string diagnostics = await lsp.ShutdownAsync(
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.DoesNotContain("Unhandled exception", diagnostics, StringComparison.Ordinal);
        }
        finally
        {
            await DirectoryReleaseWaiter.DeleteAsync(fixturePath, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
    }

    private static void AssertDocumentEdit(
        WorkspaceEdit workspaceEdit,
        string path,
        int? expectedVersion,
        int expectedEditCount,
        string originalText,
        string expectedText)
    {
        TextDocumentEdit documentEdit = workspaceEdit.DocumentChanges
            .OfType<TextDocumentEdit>()
            .Single(edit =>
                edit.TextDocument.Uri == DocumentUri.FromFileSystemPath(path));
        Assert.AreEqual(expectedVersion, documentEdit.TextDocument.Version);
        Assert.HasCount(expectedEditCount, documentEdit.Edits);
        Assert.AreEqual(expectedText, ApplyTextEdits(originalText, documentEdit.Edits));
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
        int offset = 0;
        for (int line = 0; line < position.Line; line++)
        {
            int newline = text.IndexOf('\n', offset);
            Assert.IsGreaterThanOrEqualTo(0, newline);
            offset = newline + 1;
        }

        return offset + position.Character;
    }

    private static string CreateRazorText(string membersDirective, string localName) =>
        string.Join(
            Environment.NewLine,
            $"<p>@{localName}</p>",
            $"@{membersDirective} {{",
            $"    private string {localName} => Known.SharedValue;",
            "    private string ExistingValue => \"existing\";",
            "}",
            "<p>@Known.SharedValue</p>");

    private const string ProjectText = """
        <Project Sdk="Microsoft.NET.Sdk.Web">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
            <Nullable>enable</Nullable>
          </PropertyGroup>
        </Project>
        """;

    private const string SharedValuesText = """
        namespace Fixture;

        public static class Known
        {
            public static string SharedValue { get; } = "value";
        }
        """;

    private const string SharedConsumerText = """
        namespace Fixture;

        public static class SharedConsumer
        {
            public static string Current => Known.SharedValue;
        }
        """;
}
