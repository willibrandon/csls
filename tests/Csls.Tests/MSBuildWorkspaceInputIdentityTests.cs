using Csls.Workspaces;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.Logging.Abstractions;

namespace Csls.Tests;

/// <summary>
/// Verifies project cache identity against real restored inputs and design-time builds.
/// </summary>
[TestClass]
public sealed class MSBuildWorkspaceInputIdentityTests
{
    /// <summary>
    /// Gets the test cancellation token and result reporting context.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Reuses unchanged rewritten inputs and rebuilds when an imported property changes.
    /// </summary>
    [TestMethod]
    public async Task ReloadReusesRewrittenInputsAndDetectsChangedProperties()
    {
        string directory = Directory.CreateTempSubdirectory("csls-input-identity-").FullName;
        try
        {
            string projectPath = Path.Join(directory, "Fixture.csproj");
            string propertiesPath = Path.Join(directory, "Fixture.props");
            string buildLog = Path.Join(directory, "obj", "design-time-builds.txt");
            await File.WriteAllTextAsync(projectPath, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                  <Import Project="Fixture.props" />
                  <Target Name="RecordDesignTimeBuild" BeforeTargets="Compile" Condition="'$(DesignTimeBuild)' == 'true'">
                    <WriteLinesToFile File="$(MSBuildProjectDirectory)/obj/design-time-builds.txt"
                                      Lines="$(DefineConstants)" Overwrite="false" />
                  </Target>
                </Project>
                """, TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(propertiesPath, CreateProperties("FIRST"),
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Join(directory, "Program.cs"),
                "public sealed class Fixture;", TestContext.CancellationToken).ConfigureAwait(false);

            var loader = new MSBuildWorkspaceLoader(NullLogger<MSBuildWorkspaceLoader>.Instance);
            await LoadAndAssertSymbolAsync(loader, directory, "FIRST").ConfigureAwait(false);
            string initialBuildLog = await File.ReadAllTextAsync(buildLog, TestContext.CancellationToken)
                .ConfigureAwait(false);
            Assert.Contains("FIRST", initialBuildLog);

            string[] inputs =
            [
                projectPath,
                propertiesPath,
                Path.Join(directory, "obj", "project.assets.json"),
                Path.Join(directory, "obj", "Fixture.csproj.nuget.g.props"),
                Path.Join(directory, "obj", "Fixture.csproj.nuget.g.targets")
            ];
            foreach (string input in inputs)
            {
                byte[] contents = await File.ReadAllBytesAsync(input, TestContext.CancellationToken)
                    .ConfigureAwait(false);
                DateTime modified = File.GetLastWriteTimeUtc(input);
                await File.WriteAllBytesAsync(input, contents, TestContext.CancellationToken).ConfigureAwait(false);
                File.SetLastWriteTimeUtc(input, modified.AddSeconds(1));
                Assert.AreNotEqual(modified, File.GetLastWriteTimeUtc(input));
            }

            await LoadAndAssertSymbolAsync(loader, directory, "FIRST").ConfigureAwait(false);
            Assert.AreEqual(initialBuildLog, await File.ReadAllTextAsync(buildLog, TestContext.CancellationToken)
                .ConfigureAwait(false), "Identical project and restore inputs must preserve the completed design-time build.");

            await File.WriteAllTextAsync(propertiesPath, CreateProperties("OTHER"),
                TestContext.CancellationToken).ConfigureAwait(false);
            File.SetLastWriteTimeUtc(propertiesPath, File.GetLastWriteTimeUtc(propertiesPath).AddSeconds(2));
            await LoadAndAssertSymbolAsync(loader, directory, "OTHER").ConfigureAwait(false);
            string changedBuildLog = await File.ReadAllTextAsync(buildLog, TestContext.CancellationToken)
                .ConfigureAwait(false);
            Assert.StartsWith(initialBuildLog, changedBuildLog);
            Assert.Contains("OTHER", changedBuildLog);
        }
        finally
        {
            await DirectoryReleaseWaiter.DeleteAsync(directory, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
    }

    private async Task LoadAndAssertSymbolAsync(MSBuildWorkspaceLoader loader, string directory, string symbol)
    {
        IReadOnlyList<WorkspaceFolderSnapshot> snapshots = await loader.LoadAsync(
            [directory], "Debug", null, TestContext.CancellationToken).ConfigureAwait(false);
        WorkspaceFolderSnapshot snapshot = Assert.ContainsSingle(snapshots);
        using Workspace workspace = snapshot.Workspace;
        Project project = Assert.ContainsSingle(snapshot.Solution.Projects);
        CSharpParseOptions options = Assert.IsInstanceOfType<CSharpParseOptions>(project.ParseOptions);
        Assert.Contains(symbol, options.PreprocessorSymbolNames);
        Document document = Assert.ContainsSingle(project.Documents.Where(static document => document.Name == "Program.cs"));
        Assert.AreEqual("public sealed class Fixture;", (await document.GetTextAsync(TestContext.CancellationToken)
            .ConfigureAwait(false)).ToString());
    }

    private static string CreateProperties(string symbol) =>
        $"<Project><PropertyGroup><DefineConstants>{symbol}</DefineConstants></PropertyGroup></Project>";
}
