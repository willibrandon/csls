using System.Diagnostics;
using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Builds and configures the isolated Release fixture used by JIT-policy tests.
/// </summary>
public sealed partial class DapSessionTests
{
    private async Task<string> BuildJitFixtureAsync(
        string artifactsPath,
        string configuration = "Release")
    {
        string repositoryRoot = FindRepositoryRoot();
        var startInfo = new ProcessStartInfo
        {
            FileName = ResolveDotNetHost(),
            WorkingDirectory = repositoryRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("build");
        startInfo.ArgumentList.Add(Path.Join(
            "test-assets",
            "Csls.Debugger.Fixtures.CSharp",
            "Csls.Debugger.Fixtures.CSharp.csproj"));
        startInfo.ArgumentList.Add("--configuration");
        startInfo.ArgumentList.Add(configuration);
        startInfo.ArgumentList.Add($"--property:ArtifactsPath={artifactsPath}");
        startInfo.ArgumentList.Add("--nologo");
        startInfo.ArgumentList.Add("--disable-build-servers");
        (int exitCode, string output, string error) = await DebuggerTestProcess.RunAsync(
            startInfo,
            TestContext.CancellationToken).ConfigureAwait(false);
        string diagnostic = string.Concat(
            output,
            error);
        Assert.AreEqual(
            0,
            exitCode,
            $"JIT-policy fixture build failed:{Environment.NewLine}{diagnostic}");
        return Directory.EnumerateFiles(
                Path.Join(artifactsPath, "bin"),
                "Csls.Debugger.Fixtures.CSharp.dll",
                SearchOption.AllDirectories)
            .Single();
    }

    private static void WriteJitLaunchArguments(
        Utf8JsonWriter writer,
        string programPath,
        string waitPath,
        bool suppressJitOptimizations,
        bool enableHotReload)
    {
        writer.WriteStartObject();
        writer.WriteString("program", programPath);
        writer.WriteStartArray("args");
        writer.WriteStringValue(waitPath);
        writer.WriteStringValue("41");
        writer.WriteStringValue("ready");
        writer.WriteEndArray();
        writer.WriteBoolean("suppressJITOptimizations", suppressJitOptimizations);
        writer.WriteBoolean("enableHotReload", enableHotReload);
        writer.WriteEndObject();
    }
}
