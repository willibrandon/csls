using System.Runtime.CompilerServices;
using Csls.Protocol;
using Csls.Workspaces;
using Microsoft.Extensions.Logging.Abstractions;

namespace Csls.Tests;

/// <summary>
/// Verifies real Roslyn workspace behavior over temporary source trees.
/// </summary>
[TestClass]
public sealed class WorkspaceManagerTests
{
    /// <summary>
    /// Gets the active MSTest context and its framework-managed cancellation token.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Resolves framework symbols from a loose C# file without a project.
    /// </summary>
    [TestMethod]
    public async Task LooseFileResolvesFrameworkSymbolHover()
    {
        string workspacePath = Path.Combine(
            Path.GetTempPath(),
            $"csls-loose-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workspacePath);
        try
        {
            string documentPath = Path.Combine(workspacePath, "Program.cs");
            await File.WriteAllTextAsync(
                documentPath,
                DocumentText,
                TestContext.CancellationToken).ConfigureAwait(false);

            var manager = new WorkspaceManager(
                NullLogger<WorkspaceManager>.Instance);
            await using ConfiguredAsyncDisposable managerDisposal =
                manager.ConfigureAwait(false);
            await manager.LoadAsync(
                [workspacePath],
                TestContext.CancellationToken).ConfigureAwait(false);
            var documentUri = DocumentUri.FromFileSystemPath(documentPath);
            await manager.OpenDocumentAsync(
                new TextDocumentItem
                {
                    Uri = documentUri,
                    LanguageId = "csharp",
                    Version = 1,
                    Text = DocumentText
                },
                TestContext.CancellationToken).ConfigureAwait(false);

            Hover? hover = await manager.GetHoverAsync(
                new TextDocumentPositionParams
                {
                    TextDocument = new TextDocumentIdentifier { Uri = documentUri },
                    Position = new Position(6, 9)
                },
                TestContext.CancellationToken).ConfigureAwait(false);

            Assert.IsNotNull(hover);
            Assert.Contains("System.Console", hover.Contents.Value);
        }
        finally
        {
            Directory.Delete(workspacePath, recursive: true);
        }
    }

    private const string DocumentText = """
        namespace Fixture;

        public static class Program
        {
            public static void Main()
            {
                Console.WriteLine("hello");
            }
        }
        """;
}
