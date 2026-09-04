using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Csls.Tests;

/// <summary>
/// Compares csls with the Microsoft C# editor defaults through isolated VS Code profiles.
/// </summary>
[TestClass]
public sealed class VsCodeOracleTests
{
    /// <summary>
    /// Gets the active MSTest context and its framework-managed cancellation token.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Verifies diagnostics, hover, completion, code actions, and inlay-hint defaults.
    /// </summary>
    [TestMethod]
    [TestCategory("VsCodeOracle")]
    public async Task DefaultsMatchMicrosoftCSharpProfiles()
    {
        string repositoryRoot = EditorToolResolver.FindRepositoryRoot();
        string dotNetHostPath = EditorToolResolver.ResolveAbsoluteDotNetHost();
        string runtimeExtensionPath = EditorToolResolver.ResolveVsCodeExtension(
            repositoryRoot,
            "vscode-dotnet-runtime",
            platformSpecific: false);
        string csharpExtensionPath = EditorToolResolver.ResolveVsCodeExtension(
            repositoryRoot,
            "vscode-csharp",
            platformSpecific: true);
        string devKitExtensionPath = EditorToolResolver.ResolveVsCodeExtension(
            repositoryRoot,
            "vscode-csdevkit",
            platformSpecific: true);
        string runId = Guid.NewGuid().ToString("N");
        string fixturePath = Path.Join(
            EditorToolResolver.ResolveArtifactsRoot(repositoryRoot),
            "vo",
            runId);
        string workspacePath = Path.Join(
            Path.GetTempPath(),
            $"csls-vscode-oracle-workspace-{runId}");
        Directory.CreateDirectory(fixturePath);
        Directory.CreateDirectory(workspacePath);
        try
        {
            await File.WriteAllTextAsync(
                Path.Join(workspacePath, "Fixture.csproj"),
                ProjectText,
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                Path.Join(workspacePath, "Program.cs"),
                DocumentText,
                TestContext.CancellationToken).ConfigureAwait(false);
            string cslsExtensionPath = await VsCodeExtensionPackage.GetAsync(
                repositoryRoot,
                TestContext.CancellationToken).ConfigureAwait(false);
            if (OperatingSystem.IsLinux())
            {
                XDisplaySession display = await XDisplaySession.StartAsync(
                    TestContext.CancellationToken).ConfigureAwait(false);
                await using ConfiguredAsyncDisposable displayCleanup =
                    display.ConfigureAwait(false);
                await VerifyProfilesAsync(
                    repositoryRoot,
                    fixturePath,
                    workspacePath,
                    runtimeExtensionPath,
                    csharpExtensionPath,
                    devKitExtensionPath,
                    cslsExtensionPath,
                    dotNetHostPath,
                    display.DisplayName).ConfigureAwait(false);
            }
            else
            {
                await VerifyProfilesAsync(
                    repositoryRoot,
                    fixturePath,
                    workspacePath,
                    runtimeExtensionPath,
                    csharpExtensionPath,
                    devKitExtensionPath,
                    cslsExtensionPath,
                    dotNetHostPath,
                    displayName: null).ConfigureAwait(false);
            }
        }
        finally
        {
            await DirectoryReleaseWaiter.DeleteAsync(
                fixturePath,
                TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            await DirectoryReleaseWaiter.DeleteAsync(
                workspacePath,
                TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
    }

    private const string ProjectText = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <ImplicitUsings>enable</ImplicitUsings>
            <OutputType>Exe</OutputType>
            <TargetFramework>net10.0</TargetFramework>
          </PropertyGroup>
        </Project>
        """;

    private const string DocumentText = """
        using System;
        using System.IO;
        using System.Threading.Tasks;

        internal static class Program
        {
            private static async Task Main()
            {
                Directory.CreateDirectory(".");
                await Task.CompletedTask;
                System.Console.WriteLine("hello");
                MissingSymbol();
            }
        }
        """;

    private async Task VerifyProfilesAsync(
        string repositoryRoot,
        string fixturePath,
        string workspacePath,
        string runtimeExtensionPath,
        string csharpExtensionPath,
        string devKitExtensionPath,
        string cslsExtensionPath,
        string dotNetHostPath,
        string? displayName)
    {
        using JsonDocument csls = await RunProfileAsync(
            repositoryRoot,
            fixturePath,
            workspacePath,
            "csls",
            [runtimeExtensionPath, cslsExtensionPath],
            dotNetHostPath,
            displayName).ConfigureAwait(false);
        using JsonDocument csharp = await RunProfileAsync(
            repositoryRoot,
            fixturePath,
            workspacePath,
            "csharp",
            [runtimeExtensionPath, csharpExtensionPath],
            dotNetHostPath,
            displayName).ConfigureAwait(false);
        using JsonDocument devKit = await RunProfileAsync(
            repositoryRoot,
            fixturePath,
            workspacePath,
            "csdevkit",
            [runtimeExtensionPath, csharpExtensionPath, devKitExtensionPath],
            dotNetHostPath,
            displayName).ConfigureAwait(false);

        AssertProfile(csls.RootElement);
        AssertProfile(csharp.RootElement);
        AssertProfile(devKit.RootElement);
        Assert.AreEqual(
            ReadCompilerDiagnostic(csharp.RootElement),
            ReadCompilerDiagnostic(csls.RootElement));
        Assert.AreEqual(
            ReadCompilerDiagnostic(csharp.RootElement),
            ReadCompilerDiagnostic(devKit.RootElement));
    }

    private async Task<JsonDocument> RunProfileAsync(
        string repositoryRoot,
        string fixturePath,
        string workspacePath,
        string profile,
        IReadOnlyList<string> extensionPaths,
        string dotNetHostPath,
        string? displayName)
    {
        string profilePath = Path.Join(fixturePath, profile);
        string userDataPath = Path.Join(profilePath, "user-data");
        string extensionsPath = Path.Join(profilePath, "extensions");
        string outputPath = Path.Join(profilePath, "observation.json");
        Directory.CreateDirectory(Path.Join(userDataPath, "User"));
        Directory.CreateDirectory(extensionsPath);
        await File.WriteAllTextAsync(
            Path.Join(userDataPath, "User", "settings.json"),
            CreateSettingsText(dotNetHostPath, profile),
            TestContext.CancellationToken).ConfigureAwait(false);
        using Process process = StartRunner(
            repositoryRoot,
            workspacePath,
            userDataPath,
            extensionsPath,
            outputPath,
            profile,
            extensionPaths,
            displayName);
        Task<string> outputTask = process.StandardOutput.ReadToEndAsync(
            TestContext.CancellationToken);
        Task<string> errorTask = process.StandardError.ReadToEndAsync(
            TestContext.CancellationToken);
        await process.WaitForExitAsync(TestContext.CancellationToken)
            .WaitAsync(TimeSpan.FromMinutes(4), TestContext.CancellationToken)
            .ConfigureAwait(false);
        string output = await outputTask.ConfigureAwait(false);
        string error = await errorTask.ConfigureAwait(false);
        TestContext.WriteLine($"{profile}: {output}");
        TestContext.WriteLine($"{profile}: {error}");
        await WriteProfileLogsAsync(userDataPath).ConfigureAwait(false);
        Assert.AreEqual(0, process.ExitCode, $"The {profile} VS Code profile failed.");
        Assert.IsTrue(File.Exists(outputPath), $"The {profile} observation is missing.");
        return JsonDocument.Parse(
            await File.ReadAllTextAsync(outputPath, TestContext.CancellationToken)
                .ConfigureAwait(false));
    }

    private async Task WriteProfileLogsAsync(string userDataPath)
    {
        string logsPath = Path.Join(userDataPath, "logs");
        if (!Directory.Exists(logsPath))
        {
            return;
        }

        foreach (string logPath in Directory
            .EnumerateFiles(logsPath, "*.log", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal))
        {
            string relativePath = Path.GetRelativePath(userDataPath, logPath);
            if (!relativePath.Contains("csls", StringComparison.OrdinalIgnoreCase) &&
                !relativePath.Contains("csharp", StringComparison.OrdinalIgnoreCase) &&
                !relativePath.Contains("csdevkit", StringComparison.OrdinalIgnoreCase) &&
                !relativePath.EndsWith("exthost.log", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var fileInfo = new FileInfo(logPath);
            if (fileInfo.Length > 512 * 1024)
            {
                int remainingLines = 0;
                int writtenLines = 0;
                await foreach (string line in File
                    .ReadLinesAsync(logPath, TestContext.CancellationToken)
                    .ConfigureAwait(false))
                {
                    if (line.Contains("textDocument/diagnostic", StringComparison.Ordinal))
                    {
                        remainingLines = 100;
                    }

                    if (remainingLines > 0 && writtenLines < 200)
                    {
                        TestContext.WriteLine(line);
                        remainingLines--;
                        writtenLines++;
                    }
                }

                continue;
            }

            TestContext.WriteLine($"{relativePath}:");
            TestContext.WriteLine(
                await File.ReadAllTextAsync(logPath, TestContext.CancellationToken)
                    .ConfigureAwait(false));
        }
    }

    private static Process StartRunner(
        string repositoryRoot,
        string workspacePath,
        string userDataPath,
        string extensionsPath,
        string outputPath,
        string profile,
        IReadOnlyList<string> extensionPaths,
        string? displayName)
    {
        string runnerPath = Path.Join(repositoryRoot, "tests", "vscode", "runner.mjs");
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
        startInfo.Environment["CSLS_VSCODE_CACHE_PATH"] = Path.Join(
            toolsRoot,
            "vscode",
            "stable");
        startInfo.Environment["CSLS_VSCODE_EXTENSIONS_PATH"] = extensionsPath;
        startInfo.Environment["CSLS_VSCODE_EXTENSION_PATHS"] =
            JsonSerializer.Serialize(extensionPaths);
        startInfo.Environment["CSLS_VSCODE_ORACLE_OUTPUT_PATH"] = outputPath;
        startInfo.Environment["CSLS_VSCODE_ORACLE_PROFILE"] = profile;
        startInfo.Environment["CSLS_VSCODE_SUITE"] = "oracle-suite.cjs";
        startInfo.Environment["CSLS_VSCODE_USER_DATA_PATH"] = userDataPath;
        startInfo.Environment["CSLS_VSCODE_WORKSPACE_PATH"] = workspacePath;
        startInfo.Environment["VSCODE_PORTABLE"] = Path.GetDirectoryName(userDataPath)!;
        if (displayName is not null)
        {
            startInfo.Environment["DISPLAY"] = displayName;
            startInfo.Environment.Remove("WAYLAND_DISPLAY");
            startInfo.Environment["XDG_SESSION_TYPE"] = "x11";
        }

        return Process.Start(startInfo)
            ?? throw new InvalidOperationException($"The {profile} VS Code profile did not start.");
    }

    private static string CreateSettingsText(string dotNetHostPath, string profile) => $$"""
        {
          "chat.disableAIFeatures": true,
          "csls.trace.server": "verbose",
          "dotnetAcquisitionExtension.allowInvalidPaths": true,
          "dotnetAcquisitionExtension.sharedExistingDotnetPath": {{JsonSerializer.Serialize(dotNetHostPath)}},
          "dotnet.preferCSharpExtension": {{JsonSerializer.Serialize(profile == "csharp")}},
          "telemetry.telemetryLevel": "off",
          "workbench.enableExperiments": false,
          "workbench.startupEditor": "none"
        }
        """;

    private static void AssertProfile(JsonElement profile)
    {
        string name = profile.GetProperty("profile").GetString()!;
        JsonElement diagnostics = profile.GetProperty("diagnostics");
        string[] diagnosticCodes =
        [..
            diagnostics.EnumerateArray()
            .Select(static diagnostic => diagnostic.GetProperty("code").GetString()!)
        ];
        string diagnosticMessages = string.Join(
            '\n',
            diagnostics.EnumerateArray().Select(static diagnostic =>
                diagnostic.GetProperty("message").GetString()));
        Assert.Contains("CS0103", diagnosticCodes, $"{name} must publish the compiler diagnostic.");
        Assert.DoesNotContain(
            "IDE0058",
            diagnosticCodes,
            $"{name} must not surface hidden IDE0058 diagnostics.");
        Assert.DoesNotContain(
            "Expression value is never used",
            diagnosticMessages,
            StringComparison.Ordinal);
        Assert.Contains(
            "Console",
            profile.GetProperty("hoverText").GetString()!,
            StringComparison.Ordinal);
        string[] completionLabels =
        [..
            profile.GetProperty("completionLabels").EnumerateArray()
            .Select(static label => label.GetString()!)
        ];
        Assert.Contains(
            "WriteLine",
            completionLabels,
            $"{name} must complete Console.WriteLine.");
        string[] simplifyActions =
        [..
            profile.GetProperty("codeActionTitles").EnumerateArray()
            .Select(static title => title.GetString()!)
            .Where(static title => title.Contains("Simplify", StringComparison.OrdinalIgnoreCase))
        ];
        Assert.IsNotEmpty(simplifyActions, $"{name} must offer the simple-name code action.");
        Assert.HasCount(0, profile.GetProperty("inlayHintLabels").EnumerateArray());
    }

    private static string ReadCompilerDiagnostic(JsonElement profile)
    {
        JsonElement diagnostic = profile.GetProperty("diagnostics")
            .EnumerateArray()
            .Single(static candidate => candidate.GetProperty("code").GetString() == "CS0103");
        return $"{diagnostic.GetProperty("severity").GetInt32()}|" +
            diagnostic.GetProperty("message").GetString();
    }
}
