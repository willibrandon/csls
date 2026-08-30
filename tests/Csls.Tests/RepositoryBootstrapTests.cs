using System.Diagnostics;
using System.Text.Json.Nodes;

namespace Csls.Tests;

/// <summary>
/// Verifies that repository development accepts the installed .NET 10 SDK.
/// </summary>
[TestClass]
public sealed class RepositoryBootstrapTests
{
    /// <summary>
    /// Gets the active MSTest context and its framework-managed cancellation token.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Accepts the installed .NET 10 SDK without selecting an exact feature band.
    /// </summary>
    [TestMethod]
    public async Task RepositoryAcceptsInstalledDotNet10Sdk()
    {
        string repositoryRoot = EditorToolResolver.FindRepositoryRoot();
        JsonNode globalJson = JsonNode.Parse(await File.ReadAllTextAsync(
            Path.Join(repositoryRoot, "global.json"),
            TestContext.CancellationToken).ConfigureAwait(false))
            ?? throw new InvalidDataException("global.json did not contain JSON.");
        Assert.IsNull(globalJson["sdk"]);

        ProcessStartInfo startInfo = new(
            EditorToolResolver.ResolveDotNetHost(),
            "--version")
        {
            WorkingDirectory = repositoryRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The real .NET host did not start.");
        string standardOutput = await process.StandardOutput.ReadToEndAsync(
            TestContext.CancellationToken).ConfigureAwait(false);
        string standardError = await process.StandardError.ReadToEndAsync(
            TestContext.CancellationToken).ConfigureAwait(false);
        await process.WaitForExitAsync(TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(0, process.ExitCode, standardError);
        Assert.StartsWith("10.", standardOutput.Trim(), StringComparison.Ordinal);

        string readme = await File.ReadAllTextAsync(
            Path.Join(repositoryRoot, "README.md"),
            TestContext.CancellationToken).ConfigureAwait(false);
        Assert.DoesNotContain("SDK prerequisite", readme, StringComparison.OrdinalIgnoreCase);
        string developmentDocumentation = await File.ReadAllTextAsync(
            Path.Join(
                repositoryRoot,
                "docs-site",
                "src",
                "content",
                "docs",
                "development.md"),
            TestContext.CancellationToken).ConfigureAwait(false);
        Assert.Contains("Install any", developmentDocumentation, StringComparison.Ordinal);
        Assert.DoesNotContain("10.0.400", developmentDocumentation, StringComparison.Ordinal);
        Assert.DoesNotContain("PowerShell", developmentDocumentation, StringComparison.Ordinal);
        Assert.DoesNotContain("InstallDotNet.cs", developmentDocumentation, StringComparison.Ordinal);
    }
}
