using System.Text.Json;

namespace Csls.Tests;

/// <summary>
/// Verifies the repository's generated Microsoft Testing Platform configuration.
/// </summary>
[TestClass]
public sealed class TestingPlatformConfigurationTests
{
    /// <summary>
    /// Directs test results to repository artifacts without creating the platform default folder.
    /// </summary>
    [TestMethod]
    public void TestResultsUseRepositoryArtifactsDirectory()
    {
        string repositoryRoot = EditorToolResolver.FindRepositoryRoot();
        string assemblyPath = typeof(TestingPlatformConfigurationTests).Assembly.Location;
        string configurationPath = Path.Join(
            Path.GetDirectoryName(assemblyPath)
                ?? throw new InvalidOperationException("The test assembly has no parent directory."),
            "Csls.Tests.testconfig.json");
        using var configuration = JsonDocument.Parse(
            File.ReadAllText(configurationPath));
        string resultDirectory = configuration.RootElement
            .GetProperty("platformOptions")
            .GetProperty("resultDirectory")
            .GetString()
            ?? throw new InvalidDataException(
                "The testing platform result directory is not configured.");

        Assert.AreEqual(
            Path.Join(EditorToolResolver.ResolveArtifactsRoot(repositoryRoot), "test-results"),
            Path.GetFullPath(resultDirectory));
        Assert.IsFalse(Directory.Exists(Path.Join(repositoryRoot, "TestResults")));
    }
}
