using System.Diagnostics;

namespace Csls.Tests;

/// <summary>
/// Verifies the complete csls feature contract in real VS Code web hosts.
/// </summary>
[TestClass]
public sealed class VsCodeWebLanguageServerTests
{
    /// <summary>
    /// Gets the active MSTest context and its framework-managed cancellation token.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Runs the browser feature contract in Chromium.
    /// </summary>
    [TestMethod]
    [TestCategory("VsCodeHost")]
    public Task ChromiumProvidesCSharpLanguageFeatures() => RunBrowserAsync("chromium");

    /// <summary>
    /// Runs the browser feature contract in Firefox.
    /// </summary>
    [TestMethod]
    [TestCategory("VsCodeHost")]
    public Task FirefoxProvidesCSharpLanguageFeatures() => RunBrowserAsync("firefox");

    /// <summary>
    /// Runs the browser feature contract in WebKit.
    /// </summary>
    [TestMethod]
    [TestCategory("VsCodeHost")]
    public Task WebKitProvidesCSharpLanguageFeatures() => RunBrowserAsync("webkit");

    private async Task RunBrowserAsync(string browser)
    {
        if (!string.Equals(
            Environment.GetEnvironmentVariable("CSLS_RUN_VSCODE_WEB_TESTS"),
            "true",
            StringComparison.OrdinalIgnoreCase))
        {
            Assert.Inconclusive(
                "Set CSLS_RUN_VSCODE_WEB_TESTS=true to run the VS Code web hosts.");
        }

        using ExternalWorkloadLease workloadLease = await ExternalWorkloadLease.AcquireAsync(
            TestContext.CancellationToken).ConfigureAwait(false);
        string repositoryRoot = EditorToolResolver.FindRepositoryRoot();
        string runnerPath = Path.Join(repositoryRoot, "tests", "vscode", "web-runner.mjs");
        string suitePath = Path.Join(
            repositoryRoot,
            "editors",
            "vscode",
            "dist",
            "test",
            "web-suite.cjs");
        Assert.IsTrue(File.Exists(runnerPath), $"VS Code web runner not found at {runnerPath}.");
        Assert.IsTrue(
            File.Exists(suitePath),
            "Build the VS Code web test suite before running its host tests.");
        string extensionPath = await VsCodeWebExtensionPackage.GetAsync(
            repositoryRoot,
            TestContext.CancellationToken).ConfigureAwait(false);

        string fixturePath = Path.Join(
            EditorToolResolver.ResolveArtifactsRoot(repositoryRoot),
            "vw",
            $"{browser}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(fixturePath);
        try
        {
            string workspacePath = Path.Join(fixturePath, "workspace");
            string sourceProjectPath = Path.Join(workspacePath, "App");
            string toolsPath = Path.Join(workspacePath, "Tools");
            Directory.CreateDirectory(workspacePath);
            Directory.CreateDirectory(sourceProjectPath);
            Directory.CreateDirectory(toolsPath);
            await File.WriteAllTextAsync(
                Path.Join(sourceProjectPath, "Fixture.csproj"),
                ProjectText,
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                Path.Join(workspacePath, "Fixture.slnx"),
                SolutionText,
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                Path.Join(workspacePath, "Program.cs"),
                DocumentText,
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                Path.Join(toolsPath, "Tool.cs"),
                FileBasedAppText,
                TestContext.CancellationToken).ConfigureAwait(false);
            using Process runner = StartRunner(
                repositoryRoot,
                runnerPath,
                extensionPath,
                workspacePath,
                browser);
            Task<string> outputTask = runner.StandardOutput.ReadToEndAsync(
                TestContext.CancellationToken);
            Task<string> errorTask = runner.StandardError.ReadToEndAsync(
                TestContext.CancellationToken);
            try
            {
                await runner.WaitForExitAsync(TestContext.CancellationToken)
                    .WaitAsync(TimeSpan.FromMinutes(3), TestContext.CancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                if (!runner.HasExited)
                {
                    runner.Kill(entireProcessTree: true);
                    await runner.WaitForExitAsync(TestContext.CancellationToken)
                        .ConfigureAwait(false);
                }

                TestContext.WriteLine(await outputTask.ConfigureAwait(false));
                TestContext.WriteLine(await errorTask.ConfigureAwait(false));
            }

            string output = await outputTask.ConfigureAwait(false);
            Assert.AreEqual(0, runner.ExitCode);
            Assert.DoesNotContain(
                "Only file document URIs have filesystem paths.",
                output,
                StringComparison.Ordinal);
        }
        finally
        {
            await DirectoryReleaseWaiter.DeleteAsync(
                fixturePath,
                TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
    }

    private const string ProjectText = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
            <ImplicitUsings>enable</ImplicitUsings>
            <OutputType>Exe</OutputType>
            <TargetFramework>net10.0</TargetFramework>
          </PropertyGroup>
          <ItemGroup>
            <Compile Include="../Program.cs" />
          </ItemGroup>
        </Project>
        """;

    private const string SolutionText = """
        <Solution>
          <Project Path="App/Fixture.csproj" />
        </Solution>
        """;

    private const string DocumentText = """
        Console.WriteLine("hello");
        """;

    private const string FileBasedAppText = """
        #:property TargetFramework=net10.0

        Console.WriteLine("tool");
        """;

    private static Process StartRunner(
        string repositoryRoot,
        string runnerPath,
        string extensionPath,
        string workspacePath,
        string browser)
    {
        string? configuredToolsRoot = Environment.GetEnvironmentVariable("CSLS_TOOLS_ROOT");
        string toolsRoot = string.IsNullOrWhiteSpace(configuredToolsRoot)
            ? Path.Join(repositoryRoot, "artifacts", "tools")
            : Path.GetFullPath(configuredToolsRoot);
        var startInfo = new ProcessStartInfo
        {
            FileName = "node",
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            WorkingDirectory = repositoryRoot
        };
        startInfo.ArgumentList.Add(runnerPath);
        startInfo.Environment["CSLS_VSCODE_WEB_BROWSER"] = browser;
        startInfo.Environment["CSLS_VSCODE_WEB_PORT"] = browser switch
        {
            "chromium" => "3000",
            "firefox" => "3001",
            "webkit" => "3002",
            _ => throw new ArgumentOutOfRangeException(nameof(browser), browser, null)
        };
        startInfo.Environment["CSLS_VSCODE_WEB_CACHE_PATH"] = Path.Join(
            toolsRoot,
            "vscode-web",
            "1.135.0");
        startInfo.Environment["CSLS_VSCODE_WEB_EXTENSION_PATH"] = extensionPath;
        startInfo.Environment["CSLS_VSCODE_WORKSPACE_PATH"] = workspacePath;
        startInfo.Environment["PLAYWRIGHT_BROWSERS_PATH"] = Path.Join(
            toolsRoot,
            "playwright",
            "1.62.1");
        return Process.Start(startInfo)
            ?? throw new InvalidOperationException(
                $"The VS Code {browser} integration runner did not start.");
    }
}
