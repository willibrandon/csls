using System.Diagnostics;

namespace Csls.Tests;

/// <summary>
/// Verifies the C# highlighting query through the real tree-sitter C# parser.
/// </summary>
[TestClass]
public sealed class ZedHighlightQueryTests
{
    /// <summary>
    /// Gets the active MSTest context and its framework-managed cancellation token.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Highlights the real static service receiver in workspace navigation as a type.
    /// </summary>
    [TestMethod]
    [OSCondition(ConditionMode.Include, OperatingSystems.Linux)]
    public async Task StaticServiceReceiverIsHighlightedAsType()
    {
        string repositoryRoot = EditorToolResolver.FindRepositoryRoot();
        string queryPath = Path.Join(
            repositoryRoot,
            "editors",
            "zed",
            "languages",
            "csharp",
            "highlights.scm");
        string grammarPath = Path.Join(
            EditorToolResolver.ResolveArtifactsRoot(repositoryRoot),
            "zed-extension",
            "grammar-source",
            "c_sharp");
        string sourcePath = Path.Join(
            repositoryRoot,
            "src",
            "Csls.Workspaces",
            "WorkspaceNavigationService.cs");
        var startInfo = new ProcessStartInfo
        {
            FileName = "tree-sitter",
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            WorkingDirectory = grammarPath
        };
        startInfo.ArgumentList.Add("query");
        startInfo.ArgumentList.Add(queryPath);
        startInfo.ArgumentList.Add(sourcePath);
        startInfo.ArgumentList.Add("--captures");

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("tree-sitter did not start.");
        Task<string> standardOutput = process.StandardOutput.ReadToEndAsync(
            TestContext.CancellationToken);
        Task<string> standardError = process.StandardError.ReadToEndAsync(
            TestContext.CancellationToken);
        await process.WaitForExitAsync(TestContext.CancellationToken).ConfigureAwait(false);
        string output = await standardOutput.ConfigureAwait(false);
        string error = await standardError.ConfigureAwait(false);
        Assert.AreEqual(
            0,
            process.ExitCode,
            $"tree-sitter failed:{Environment.NewLine}{output}{error}");

        string[] symbolCaptures =
        [
            .. output
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Where(static line => line.Contains(
                    "text: `WorkspaceVirtualDocumentService`",
                    StringComparison.Ordinal))
        ];
        Assert.Contains(
            " - type,",
            string.Join(Environment.NewLine, symbolCaptures),
            StringComparison.Ordinal);
    }
}
