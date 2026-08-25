using Csls.Protocol;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Csls.Tests;

/// <summary>
/// Verifies real client-visible work-done progress while Roslyn loads projects.
/// </summary>
[TestClass]
public sealed class WorkspaceLoadProgressLanguageServerTests
{
    /// <summary>
    /// Gets the active MSTest context and its framework-managed cancellation token.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Reports each solution project once with ordered completion and bounded percentages.
    /// </summary>
    [TestMethod]
    public async Task SolutionLoadReportsEachProjectInOrder()
    {
        string fixturePath = await CreateFixtureAsync(
            includeLibraryInSolution: true,
            useClassicSolution: false)
            .ConfigureAwait(false);
        try
        {
            (WorkDoneProgressCreateParams creation, List<WorkDoneProgressParams> progress) =
                await LoadWithProgressAsync(fixturePath).ConfigureAwait(false);

            Assert.StartsWith("workspace-load-", creation.Token, StringComparison.Ordinal);
            Assert.IsInstanceOfType<WorkDoneProgressBegin>(progress[0].Value);
            var begin = (WorkDoneProgressBegin)progress[0].Value;
            Assert.AreEqual("Loading C# workspace", begin.Title);
            Assert.AreEqual(0, begin.Percentage);

            List<WorkDoneProgressReport> reports =
            [
                .. progress
                    .Select(static item => item.Value)
                    .OfType<WorkDoneProgressReport>()
            ];
            Assert.HasCount(2, reports);
            Assert.AreEqual(50, reports[0].Percentage);
            Assert.AreEqual(100, reports[1].Percentage);
            Assert.Contains("(1/2)", GetRequiredMessage(reports[0]), StringComparison.Ordinal);
            Assert.Contains("(2/2)", GetRequiredMessage(reports[1]), StringComparison.Ordinal);
            AssertProjectNames(reports);

            Assert.IsInstanceOfType<WorkDoneProgressEnd>(progress[^1].Value);
            var end = (WorkDoneProgressEnd)progress[^1].Value;
            Assert.AreEqual("Loaded 2 projects.", end.Message);
            Assert.IsTrue(progress.All(item => item.Token == creation.Token));
        }
        finally
        {
            Directory.Delete(fixturePath, recursive: true);
        }
    }

    /// <summary>
    /// Reports the expected project total from a classic solution without loading MSBuild early.
    /// </summary>
    [TestMethod]
    public async Task ClassicSolutionLoadReportsExpectedProjectTotal()
    {
        string fixturePath = await CreateFixtureAsync(
            includeLibraryInSolution: true,
            useClassicSolution: true).ConfigureAwait(false);
        try
        {
            (_, List<WorkDoneProgressParams> progress) = await LoadWithProgressAsync(fixturePath)
                .ConfigureAwait(false);
            WorkDoneProgressReport[] reports =
            [
                .. progress
                    .Select(static item => item.Value)
                    .OfType<WorkDoneProgressReport>()
            ];

            Assert.HasCount(2, reports);
            Assert.AreEqual(50, reports[0].Percentage);
            Assert.AreEqual(100, reports[1].Percentage);
            AssertProjectNames(reports);
        }
        finally
        {
            Directory.Delete(fixturePath, recursive: true);
        }
    }

    /// <summary>
    /// Keeps progress monotonic when Roslyn discovers a referenced project outside the solution.
    /// </summary>
    [TestMethod]
    public async Task SolutionLoadWidensProgressForReferencedProject()
    {
        string fixturePath = await CreateFixtureAsync(
            includeLibraryInSolution: false,
            useClassicSolution: false)
            .ConfigureAwait(false);
        try
        {
            (_, List<WorkDoneProgressParams> progress) = await LoadWithProgressAsync(fixturePath)
                .ConfigureAwait(false);
            List<WorkDoneProgressReport> reports =
            [
                .. progress
                    .Select(static item => item.Value)
                    .OfType<WorkDoneProgressReport>()
            ];

            Assert.HasCount(2, reports);
            int[] percentages =
            [
                .. reports.Select(report => report.Percentage ?? throw new InvalidDataException(
                    "The project progress had no percentage."))
            ];
            Assert.AreSequenceEqual(percentages.Order().ToArray(), percentages);
            Assert.IsTrue(percentages.All(static percentage => percentage is >= 0 and <= 100));
            Assert.AreEqual(50, percentages[0]);
            Assert.AreEqual(100, percentages[^1]);
            Assert.Contains("(1/2)", GetRequiredMessage(reports[0]), StringComparison.Ordinal);
            Assert.Contains("(2/2)", GetRequiredMessage(reports[^1]), StringComparison.Ordinal);
            AssertProjectNames(reports);
        }
        finally
        {
            Directory.Delete(fixturePath, recursive: true);
        }
    }

    /// <summary>
    /// Reports the implicit project created for a folder containing only loose source files.
    /// </summary>
    [TestMethod]
    public async Task LooseFolderLoadReportsImplicitProject()
    {
        string fixturePath = Path.Join(
            Path.GetTempPath(),
            $"csls-workspace-progress-loose-{Guid.NewGuid():N}");
        Directory.CreateDirectory(fixturePath);
        await File.WriteAllTextAsync(
            Path.Join(fixturePath, "Loose.cs"),
            "namespace Loose; public sealed class Document;",
            TestContext.CancellationToken).ConfigureAwait(false);
        try
        {
            (_, List<WorkDoneProgressParams> progress) = await LoadWithProgressAsync(fixturePath)
                .ConfigureAwait(false);
            WorkDoneProgressReport[] reports =
            [
                .. progress
                    .Select(static item => item.Value)
                    .OfType<WorkDoneProgressReport>()
            ];

            Assert.HasCount(1, reports);
            Assert.AreEqual(100, reports[0].Percentage);
            Assert.AreEqual(
                $"{Path.GetFileName(fixturePath)} (1/1)",
                reports[0].Message);
            Assert.IsInstanceOfType<WorkDoneProgressEnd>(progress[^1].Value);
            var end = (WorkDoneProgressEnd)progress[^1].Value;
            Assert.AreEqual("Loaded 1 projects.", end.Message);
        }
        finally
        {
            Directory.Delete(fixturePath, recursive: true);
        }
    }

    private async Task<(
        WorkDoneProgressCreateParams Creation,
        List<WorkDoneProgressParams> Progress)> LoadWithProgressAsync(string fixturePath)
    {
        string repositoryRoot = EditorToolResolver.FindRepositoryRoot();
        string workerPath = Path.Join(
            EditorToolResolver.ResolveArtifactsRoot(repositoryRoot),
            "bin",
            "Csls.Worker",
            "debug",
            "csls-worker.dll");
        var client = new LspTestClient(
            legacyConfiguration: null,
            preferredConfiguration: null);
        var lsp = LspProcessSession.Start(
            "csls-workspace-load-progress-worker",
            EditorToolResolver.ResolveDotNetHost(),
            [workerPath],
            fixturePath,
            client);
        await using ConfiguredAsyncDisposable lspCleanup = lsp.ConfigureAwait(false);
        using var capabilities = JsonDocument.Parse(
            """
            {
              "window": {
                "workDoneProgress": true
              }
            }
            """);
        await lsp.InitializeAsync(
            fixturePath,
            capabilities.RootElement,
            TestContext.CancellationToken).ConfigureAwait(false);
        await lsp.CompleteInitializationAsync().ConfigureAwait(false);

        WorkDoneProgressCreateParams creation = await client
            .ReadWorkDoneProgressCreationAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        var progress = new List<WorkDoneProgressParams>();
        while (progress.Count < 10_000)
        {
            WorkDoneProgressParams value = await client
                .ReadWorkDoneProgressAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            progress.Add(value);
            if (value.Value is WorkDoneProgressEnd)
            {
                break;
            }
        }

        Assert.IsNotEmpty(progress);
        Assert.IsInstanceOfType<WorkDoneProgressEnd>(progress[^1].Value);
        string diagnostics = await lsp.ShutdownAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        Assert.DoesNotContain("Unhandled exception", diagnostics, StringComparison.Ordinal);
        return (creation, progress);
    }

    private async Task<string> CreateFixtureAsync(
        bool includeLibraryInSolution,
        bool useClassicSolution)
    {
        string fixturePath = Path.Join(
            Path.GetTempPath(),
            $"csls-workspace-progress-{Guid.NewGuid():N}");
        string appPath = Path.Join(fixturePath, "App");
        string libraryPath = Path.Join(fixturePath, "Lib");
        Directory.CreateDirectory(appPath);
        Directory.CreateDirectory(libraryPath);
        await File.WriteAllTextAsync(
            Path.Join(appPath, "App.csproj"),
            AppProjectText,
            TestContext.CancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(
            Path.Join(appPath, "Program.cs"),
            "namespace App; public static class Program { public static int Value => Lib.Value.Number; }",
            TestContext.CancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(
            Path.Join(libraryPath, "Lib.csproj"),
            LibraryProjectText,
            TestContext.CancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(
            Path.Join(libraryPath, "Value.cs"),
            "namespace Lib; public static class Value { public static int Number => 42; }",
            TestContext.CancellationToken).ConfigureAwait(false);
        string solution = useClassicSolution
            ? ClassicSolutionText
            : includeLibraryInSolution
                ? "<Solution><Project Path=\"App/App.csproj\" />" +
                    "<Project Path=\"Lib/Lib.csproj\" /></Solution>"
                : "<Solution><Project Path=\"App/App.csproj\" /></Solution>";
        await File.WriteAllTextAsync(
            Path.Join(fixturePath, useClassicSolution ? "Fixture.sln" : "Fixture.slnx"),
            solution,
            TestContext.CancellationToken).ConfigureAwait(false);
        return fixturePath;
    }

    private static string GetProjectName(WorkDoneProgressReport report)
    {
        string message = GetRequiredMessage(report);
        int separator = message.LastIndexOf(" (", StringComparison.Ordinal);
        return separator < 0 ? message : message[..separator];
    }

    private static string GetRequiredMessage(WorkDoneProgressReport report) =>
        report.Message ?? throw new InvalidDataException("The project progress had no message.");

    private static void AssertProjectNames(IEnumerable<WorkDoneProgressReport> reports)
    {
        string[] expected = ["App", "Lib"];
        string[] actual = [.. reports.Select(GetProjectName).Order(StringComparer.Ordinal)];
        Assert.AreSequenceEqual(expected, actual);
    }

    private const string AppProjectText = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
          </PropertyGroup>
          <ItemGroup>
            <ProjectReference Include="../Lib/Lib.csproj" />
          </ItemGroup>
        </Project>
        """;

    private const string LibraryProjectText = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
          </PropertyGroup>
        </Project>
        """;

    private const string ClassicSolutionText = """
        Microsoft Visual Studio Solution File, Format Version 12.00
        # Visual Studio Version 17
        VisualStudioVersion = 17.0.31903.59
        MinimumVisualStudioVersion = 10.0.40219.1
        Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "App", "App/App.csproj", "{751E444F-7B2D-4F52-88C0-ECEDE4AB624B}"
        EndProject
        Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Lib", "Lib/Lib.csproj", "{5D0BB4A9-41B2-4C8D-A76E-7EFC05702B31}"
        EndProject
        Global
        EndGlobal
        """;
}
