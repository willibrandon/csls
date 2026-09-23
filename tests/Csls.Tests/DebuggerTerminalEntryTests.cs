using Hex1b;
using Hex1b.Automation;
using Hex1b.Input;
using Hex1b.Layout;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace Csls.Tests;

/// <summary>
/// Verifies interactive entry stops through the real launcher, debugger, and terminal emulator.
/// </summary>
[TestClass]
public sealed class DebuggerTerminalEntryTests
{
    /// <summary>
    /// Gets the framework-managed test cancellation token.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Opens an entry stop, renders its source and arguments, and continues to normal target exit.
    /// </summary>
    /// <param name="useEnvironmentFile">Whether launch loads its value from an environment file relative to the target directory.</param>
    /// <param name="requireExactSource">The explicit policy or omission that selects the default.</param>
    [TestMethod]
    [DataRow(false, null)]
    [DataRow(true, null)]
    [DataRow(false, true)]
    [DataRow(false, false)]
    [OSCondition(ConditionMode.Include, OperatingSystems.Linux)]
    [TestCategory("DebuggerTerminal")]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task TerminalStopAtEntryRendersSourceAndContinues(bool useEnvironmentFile, bool? requireExactSource)
    {
        DirectoryInfo directory = Directory.CreateTempSubdirectory("csls-terminal-entry-");
        string path = Path.Join(directory.FullName, ".env");
        try
        {
            await File.WriteAllTextAsync(path, "CSLS_TERMINAL_ENTRY_RESULT=entry-result", TestContext.CancellationToken)
                .ConfigureAwait(false);
            await AssertTerminalEntryAsync(useEnvironmentFile ? directory.FullName : null,
                requireExactSource: requireExactSource).ConfigureAwait(false);
        }
        finally
        {
            File.Delete(path);
            directory.Delete();
        }
    }

    /// <summary>
    /// Displays the chosen source policy and continues from an entry stop with edited local source.
    /// </summary>
    /// <param name="emptySource">Whether the edited local file is empty.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [OSCondition(ConditionMode.Include, OperatingSystems.Linux)]
    [TestCategory("DebuggerTerminal")]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task TerminalRelaxedSourcePolicyRendersProvenance(bool emptySource)
    {
        DirectoryInfo directory = Directory.CreateTempSubdirectory("csls-terminal-source-");
        string path = Path.Join(directory.FullName, "Program.cs");
        try
        {
            string original = await File.ReadAllTextAsync(Path.Join(EditorToolResolver.FindRepositoryRoot(),
                "tests", "Csls.TestProcessHost", "Program.cs"), TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(path, emptySource ? string.Empty : original + "\n// Edited after build.\n",
                TestContext.CancellationToken).ConfigureAwait(false);
            await AssertTerminalEntryAsync(null, path, requireExactSource: false, emptySource).ConfigureAwait(false);
        }
        finally
        {
            File.Delete(path);
            directory.Delete();
        }
    }

    private async Task AssertTerminalEntryAsync(string? environmentDirectory, string? mappedSource = null,
        bool? requireExactSource = null, bool emptySource = false)
    {
        string repositoryRoot = EditorToolResolver.FindRepositoryRoot();
        string artifactsRoot = EditorToolResolver.ResolveArtifactsRoot(repositoryRoot);
        string originalSource = await File.ReadAllTextAsync(Path.Join(repositoryRoot,
            "tests", "Csls.TestProcessHost", "Program.cs"), TestContext.CancellationToken).ConfigureAwait(false);
        CompilationUnitSyntax syntax = CSharpSyntaxTree.ParseText(originalSource,
            cancellationToken: TestContext.CancellationToken).GetCompilationUnitRoot(TestContext.CancellationToken);
        GlobalStatementSyntax firstStatement = syntax.Members.OfType<GlobalStatementSyntax>().First();
        int entryLine = firstStatement.GetFirstToken().GetLocation().GetLineSpan().StartLinePosition.Line + 1;
        string entryText = (await syntax.SyntaxTree.GetTextAsync(TestContext.CancellationToken).ConfigureAwait(false))
            .Lines[entryLine - 1].ToString().Trim();
        string visibleEntryText = entryText[..Math.Min(entryText.Length, 32)];
        const int width = 140;
        const int height = 35;
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["CSLS_DEBUGGER_WORKER_PATH"] = Path.Join(artifactsRoot, "bin", "Csls.Debugger.Worker",
                "debug", "csls-debugger-worker.dll"),
            ["DOTNET_HOST_PATH"] = EditorToolResolver.ResolveDotNetHost(),
            ["CSLS_TERMINAL_ENTRY_RESULT"] = environmentDirectory is null ? "entry-result" : "inherited-result"
        };
        string[] environmentArguments = environmentDirectory is null ? [] :
            ["--cwd", environmentDirectory, "--env-file", ".env"];
        string[] sourceArguments = mappedSource is null ? [] :
            ["--source-file-map", $"/_/tests/Csls.TestProcessHost/Program.cs={mappedSource}",
                "--source-file-map", $"{Path.Join(repositoryRoot, "tests", "Csls.TestProcessHost", "Program.cs")}={mappedSource}"];
        string[] policyArguments = requireExactSource is bool exactSource
            ? ["--require-exact-source", exactSource ? "true" : "false"] : [];
        var workload = new Hex1bPtyWorkload(EditorToolResolver.ResolveDotNetHost(),
            [
                EditorToolResolver.ResolveLauncher(repositoryRoot), "debugger", "tui", "launch",
                EditorToolResolver.ResolveTestProcessHost(repositoryRoot), "--stop-at-entry",
                "--source-file-map", $"/_/={repositoryRoot}",
                .. environmentArguments,
                .. sourceArguments,
                .. policyArguments,
                "--", "--print-environment", "CSLS_TERMINAL_ENTRY_RESULT"
            ], repositoryRoot, width, height, environment);
        await using ConfiguredAsyncDisposable cleanup = workload.ConfigureAwait(false);
        using Hex1bTerminal terminal = Hex1bTerminal.CreateBuilder()
            .WithWorkload(workload).WithHeadless().WithDimensions(width, height).Build();
        var sourceRegion = new Rect(0, 1, 64, height - 2);
        var outputRegion = new Rect(66, height - 7, width - 66, 4);
        int exitCode = await workload.RunAsync(terminal, async () =>
        {
            var automator = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(30));
            await automator.WaitUntilAsync(
                screen => screen.InAlternateScreen && screen.GetLine(0).Contains("Stopped  entry", StringComparison.Ordinal),
                description: "entry-stop header").ConfigureAwait(false);
            await automator.WaitUntilTextAsync("args =").ConfigureAwait(false);
            using (Hex1bTerminalSnapshot stopped = automator.CreateSnapshot())
            {
                string sourceText = stopped.GetRegion(sourceRegion).GetText();
                Assert.Contains("Source", sourceText);
                if (emptySource)
                {
                    Assert.DoesNotContain(visibleEntryText, sourceText);
                }
                else
                {
                    string selectedLine = Assert.ContainsSingle(sourceText.Split('\n')
                        .Where(line => line.StartsWith("│>", StringComparison.Ordinal)));
                    string expectedPrefix = string.Create(CultureInfo.InvariantCulture,
                        $"│>   {entryLine,5}  {visibleEntryText}");
                    Assert.StartsWith(expectedPrefix, selectedLine);
                }

                if (mappedSource is not null)
                {
                    Assert.Contains("unverified local source", sourceText);
                }
                else
                {
                    Assert.DoesNotContain("unverified local source", sourceText);
                }

                Assert.Contains("Program.cs", stopped.GetScreenText());
                Assert.DoesNotContain("entry-result", stopped.GetRegion(outputRegion).GetText());
                TestContext.WriteLine(stopped.GetScreenText());
            }

            await automator.KeyAsync(Hex1bKey.F5, TestContext.CancellationToken).ConfigureAwait(false);
            await automator.WaitUntilAsync(screen =>
                screen.GetLine(0).Contains("Terminated", StringComparison.Ordinal) &&
                screen.GetRegion(outputRegion).ContainsText("out> entry-result"),
                description: "normal target exit and captured output").ConfigureAwait(false);
            using (Hex1bTerminalSnapshot terminated = automator.CreateSnapshot())
            {
                Assert.DoesNotContain("Stopped", terminated.GetLine(0));
                Assert.DoesNotContain("unverified local source", terminated.GetRegion(sourceRegion).GetText());
                TestContext.WriteLine(terminated.GetScreenText());
            }

            await automator.Ctrl().KeyAsync(Hex1bKey.C, TestContext.CancellationToken).ConfigureAwait(false);
        }, TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(0, exitCode);
    }
}
