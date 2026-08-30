using System.Diagnostics;
using System.Text.Json.Nodes;

namespace Csls.Tests;

/// <summary>
/// Verifies clean-machine SDK bootstrap instructions through the real .NET host.
/// </summary>
[TestClass]
public sealed class RepositoryBootstrapTests
{
    private const string BootstrapDocumentationUrl =
        "https://willibrandon.github.io/csls/development/#install-the-sdk";

    /// <summary>
    /// Gets the active MSTest context and its framework-managed cancellation token.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Directs a host missing the pinned SDK to an executable bootstrap before build commands.
    /// </summary>
    [TestMethod]
    public async Task MissingPinnedSdkReportsExecutableBootstrapBeforeBuildInstructions()
    {
        string repositoryRoot = EditorToolResolver.FindRepositoryRoot();
        string fixturePath = Path.Join(
            Path.GetTempPath(),
            $"csls-missing-sdk-{Guid.NewGuid():N}");
        Directory.CreateDirectory(fixturePath);
        try
        {
            JsonNode globalJson = JsonNode.Parse(await File.ReadAllTextAsync(
                Path.Join(repositoryRoot, "global.json"),
                TestContext.CancellationToken).ConfigureAwait(false))
                ?? throw new InvalidDataException("global.json did not contain JSON.");
            globalJson["sdk"]!["version"] = "99.0.100";
            await File.WriteAllTextAsync(
                Path.Join(fixturePath, "global.json"),
                globalJson.ToJsonString(),
                TestContext.CancellationToken).ConfigureAwait(false);

            ProcessStartInfo startInfo = new(
                EditorToolResolver.ResolveDotNetHost(),
                "build")
            {
                WorkingDirectory = fixturePath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using Process process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("The real .NET host did not start.");
            Task<string> standardOutput = process.StandardOutput.ReadToEndAsync(
                TestContext.CancellationToken);
            Task<string> standardError = process.StandardError.ReadToEndAsync(
                TestContext.CancellationToken);
            await process.WaitForExitAsync(TestContext.CancellationToken).ConfigureAwait(false);
            string output = string.Concat(
                await standardOutput.ConfigureAwait(false),
                await standardError.ConfigureAwait(false));

            Assert.AreNotEqual(0, process.ExitCode, output);
            Assert.Contains(BootstrapDocumentationUrl, output, StringComparison.Ordinal);
            Assert.DoesNotContain(
                "dotnet run --file scripts/InstallDotNet.cs",
                output,
                StringComparison.Ordinal);

            AssertInstallPrecedesBuild(
                await File.ReadAllTextAsync(
                    Path.Join(repositoryRoot, "README.md"),
                    TestContext.CancellationToken).ConfigureAwait(false));
            AssertInstallPrecedesBuild(
                await File.ReadAllTextAsync(
                    Path.Join(repositoryRoot, "CONTRIBUTING.md"),
                    TestContext.CancellationToken).ConfigureAwait(false));
            string developmentDocumentation = await File.ReadAllTextAsync(
                Path.Join(
                    repositoryRoot,
                    "docs-site",
                    "src",
                    "content",
                    "docs",
                    "development.md"),
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.Contains("## Install the SDK", developmentDocumentation, StringComparison.Ordinal);
            Assert.Contains("Windows", developmentDocumentation, StringComparison.Ordinal);
            Assert.Contains("Linux", developmentDocumentation, StringComparison.Ordinal);
            Assert.Contains("macOS", developmentDocumentation, StringComparison.Ordinal);
        }
        finally
        {
            await DirectoryReleaseWaiter.DeleteAsync(
                fixturePath,
                TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
    }

    private static void AssertInstallPrecedesBuild(string documentation)
    {
        int installIndex = documentation.IndexOf(
            BootstrapDocumentationUrl,
            StringComparison.Ordinal);
        int buildIndex = documentation.IndexOf("dotnet build", StringComparison.Ordinal);
        Assert.IsGreaterThanOrEqualTo(0, installIndex, documentation);
        Assert.IsGreaterThan(installIndex, buildIndex, documentation);
    }
}
