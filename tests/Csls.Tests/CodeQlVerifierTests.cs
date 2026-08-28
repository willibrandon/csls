using System.Diagnostics;

namespace Csls.Tests;

/// <summary>
/// Verifies the repository CodeQL result gate through its real file-based application.
/// </summary>
[TestClass]
public sealed class CodeQlVerifierTests
{
    /// <summary>
    /// Gets the active MSTest context and its framework-managed cancellation token.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Ignores the known serializer generator finding without masking the same rule in product code.
    /// </summary>
    [TestMethod]
    public async Task GeneratedSerializerFindingDoesNotMaskProductFinding()
    {
        string repositoryRoot = EditorToolResolver.FindRepositoryRoot();
        string fixturePath = Path.Join(
            Path.GetTempPath(),
            $"csls-codeql-verifier-{Guid.NewGuid():N}");
        Directory.CreateDirectory(fixturePath);
        try
        {
            string sarifPath = Path.Join(fixturePath, "results.sarif");
            await File.WriteAllTextAsync(
                sarifPath,
                CreateSarif(
                    "artifacts/obj/Csls.Protocol/release/generated/" +
                    "System.Text.Json.SourceGeneration/" +
                    "System.Text.Json.SourceGeneration.JsonSourceGenerator/Generated.g.cs"),
                TestContext.CancellationToken).ConfigureAwait(false);
            (int generatedExitCode, string generatedOutput, string generatedError) =
                await RunVerifierAsync(repositoryRoot, fixturePath).ConfigureAwait(false);
            Assert.AreEqual(0, generatedExitCode, generatedError);
            Assert.Contains(
                "ignored 1 known System.Text.Json source-generator finding",
                generatedOutput,
                StringComparison.Ordinal);

            await File.WriteAllTextAsync(
                sarifPath,
                CreateSarif("src/Csls.Protocol/Generated.cs"),
                TestContext.CancellationToken).ConfigureAwait(false);
            (int productExitCode, _, string productError) =
                await RunVerifierAsync(repositoryRoot, fixturePath).ConfigureAwait(false);
            Assert.AreEqual(1, productExitCode);
            Assert.Contains("cs/useless-cast-to-self", productError, StringComparison.Ordinal);
            Assert.Contains("src/Csls.Protocol/Generated.cs:12", productError, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(fixturePath, recursive: true);
        }
    }

    private async Task<(int ExitCode, string Output, string Error)> RunVerifierAsync(
        string repositoryRoot,
        string fixturePath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            WorkingDirectory = repositoryRoot
        };
        startInfo.ArgumentList.Add("run");
        startInfo.ArgumentList.Add("--file");
        startInfo.ArgumentList.Add(Path.Join(repositoryRoot, "scripts", "Verify-CodeQl.cs"));
        startInfo.ArgumentList.Add("--");
        startInfo.ArgumentList.Add(fixturePath);
        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The CodeQL verifier did not start.");
        Task<string> outputTask = process.StandardOutput.ReadToEndAsync(
            TestContext.CancellationToken);
        Task<string> errorTask = process.StandardError.ReadToEndAsync(
            TestContext.CancellationToken);
        await process.WaitForExitAsync(TestContext.CancellationToken).ConfigureAwait(false);
        return (
            process.ExitCode,
            await outputTask.ConfigureAwait(false),
            await errorTask.ConfigureAwait(false));
    }

    private static string CreateSarif(string path) => $$"""
        {
          "version": "2.1.0",
          "runs": [
            {
              "results": [
                {
                  "level": "warning",
                  "ruleId": "cs/useless-cast-to-self",
                  "message": { "text": "The cast is redundant." },
                  "locations": [
                    {
                      "physicalLocation": {
                        "artifactLocation": { "uri": "{{path}}" },
                        "region": { "startLine": 12 }
                      }
                    }
                  ]
                }
              ]
            }
          ]
        }
        """;
}
