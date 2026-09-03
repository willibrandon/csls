using System.Diagnostics;
using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Builds and configures the isolated Release fixture used by JIT-policy tests.
/// </summary>
public sealed partial class DapSessionTests
{
    private async Task<string> BuildJitFixtureAsync(string artifactsPath)
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
        startInfo.ArgumentList.Add("Release");
        startInfo.ArgumentList.Add($"--property:ArtifactsPath={artifactsPath}");
        startInfo.ArgumentList.Add("--nologo");
        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not build the JIT-policy fixture.");
        Task<string> output = process.StandardOutput.ReadToEndAsync(TestContext.CancellationToken);
        Task<string> error = process.StandardError.ReadToEndAsync(TestContext.CancellationToken);
        await process.WaitForExitAsync(TestContext.CancellationToken).ConfigureAwait(false);
        string diagnostic = string.Concat(
            await output.ConfigureAwait(false),
            await error.ConfigureAwait(false));
        Assert.AreEqual(
            0,
            process.ExitCode,
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
        bool suppressJitOptimizations)
    {
        writer.WriteStartObject();
        writer.WriteString("program", programPath);
        writer.WriteStartArray("args");
        writer.WriteStringValue(waitPath);
        writer.WriteStringValue("41");
        writer.WriteStringValue("ready");
        writer.WriteEndArray();
        writer.WriteBoolean("suppressJITOptimizations", suppressJitOptimizations);
        writer.WriteEndObject();
    }
}
