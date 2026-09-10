using System.Diagnostics;
using System.Text.Json;

namespace Csls.Tests;

/// <summary>
/// Verifies the analyzers selected by real repository projects and their SDK imports.
/// </summary>
[TestClass]
public sealed class RepositoryAnalyzerTests
{
    /// <summary>
    /// Gets framework-managed cancellation for the isolated MSBuild evaluation.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Resolves each SDK code-quality analyzer once while preserving strict analysis settings.
    /// </summary>
    [TestMethod]
    [DataRow("src/Csls.Debugger.Contracts/Csls.Debugger.Contracts.csproj", "CSharp")]
    [DataRow("test-assets/Csls.Debugger.Fixtures.VisualBasic/Csls.Debugger.Fixtures.VisualBasic.vbproj", "VisualBasic")]
    [Timeout(90000, CooperativeCancellation = true)]
    public async Task RepositoryProjectsResolveOneSdkAnalyzerSet(string projectPath, string language)
    {
        string repositoryRoot = EditorToolResolver.FindRepositoryRoot();
        DirectoryInfo fixture = Directory.CreateTempSubdirectory("csls-analyzer-resolution-");
        try
        {
            string output = await ResolveAnalyzersAsync(repositoryRoot, projectPath, fixture.FullName)
                .ConfigureAwait(false);
            using var result = JsonDocument.Parse(output);
            JsonElement properties = result.RootElement.GetProperty("Properties");
            foreach (string name in new[]
                {
                    "EnableNETAnalyzers", "EnforceCodeStyleInBuild", "TreatWarningsAsErrors",
                    "CodeAnalysisTreatWarningsAsErrors", "MSBuildTreatWarningsAsErrors"
                })
            {
                Assert.AreEqual("true", properties.GetProperty(name).GetString(), name);
            }

            Assert.AreEqual("latest", properties.GetProperty("AnalysisLevel").GetString());
            Assert.AreEqual("AllEnabledByDefault", properties.GetProperty("AnalysisMode").GetString());
            string sdkPath = properties.GetProperty("MSBuildSDKsPath").GetString()
                ?? throw new InvalidDataException("MSBuild did not report its SDK directory.");
            string[] analyzerPaths =
            [
                .. result.RootElement.GetProperty("Items").GetProperty("Analyzer")
                    .EnumerateArray()
                    .Select(static analyzer => analyzer.GetProperty("FullPath").GetString()
                        ?? throw new InvalidDataException("An analyzer has no absolute path."))
            ];
            foreach (string fileName in new[]
                {
                    "Microsoft.CodeAnalysis.NetAnalyzers.dll",
                    $"Microsoft.CodeAnalysis.{language}.NetAnalyzers.dll"
                })
            {
                string actual = Assert.ContainsSingle(analyzerPaths.Where(path =>
                    string.Equals(Path.GetFileName(path), fileName, StringComparison.Ordinal)),
                    $"The compiler must receive one {fileName}: {string.Join(Environment.NewLine, analyzerPaths)}");
                string expected = Path.GetFullPath(Path.Join(sdkPath, "Microsoft.NET.Sdk", "analyzers", fileName));
                Assert.AreEqual(expected, actual);
                Assert.IsTrue(File.Exists(actual), actual);
            }
        }
        finally
        {
            await DirectoryReleaseWaiter.DeleteAsync(fixture.FullName, TimeSpan.FromSeconds(10))
                .ConfigureAwait(false);
        }
    }

    private async Task<string> ResolveAnalyzersAsync(
        string repositoryRoot, string projectPath, string artifactsPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = EditorToolResolver.ResolveDotNetHost(),
            WorkingDirectory = repositoryRoot,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string argument in new[]
            {
                "msbuild", Path.Join(repositoryRoot, projectPath), "-restore", "-nologo", "-nodeReuse:false",
                "-target:ResolveLockFileAnalyzers", $"-property:ArtifactsPath={artifactsPath}",
                "-getItem:Analyzer",
                "-getProperty:EnableNETAnalyzers,EnforceCodeStyleInBuild,TreatWarningsAsErrors," +
                    "CodeAnalysisTreatWarningsAsErrors,MSBuildTreatWarningsAsErrors,AnalysisLevel,AnalysisMode,MSBuildSDKsPath",
                "/bl:" + Path.Join(EditorToolResolver.ResolveArtifactsRoot(repositoryRoot),
                    "binlogs", "repository-analyzer-resolution-{}.binlog")
            })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The real MSBuild process did not start.");
        Task<string> outputTask = process.StandardOutput.ReadToEndAsync(TestContext.CancellationToken);
        Task<string> errorTask = process.StandardError.ReadToEndAsync(TestContext.CancellationToken);
        try
        {
            await process.WaitForExitAsync(TestContext.CancellationToken).ConfigureAwait(false);
            string output = await outputTask.ConfigureAwait(false);
            string error = await errorTask.ConfigureAwait(false);
            Assert.AreEqual(0, process.ExitCode, output + error);
            return output;
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
    }
}
