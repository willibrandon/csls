using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies DAP source retrieval through real Source Link metadata and HTTP.
/// </summary>
public sealed partial class DapSessionTests
{
    /// <summary>
    /// Downloads checksum-valid Source Link content once and reuses the session cache.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task SourceLinkProvidesVerifiedSourceContent()
    {
        string repositoryRoot = FindRepositoryRoot();
        string sourcePath = Path.Join(
            repositoryRoot,
            "test-assets",
            "Csls.Debugger.Fixtures.CSharp",
            "Program.cs");
        byte[] source = await File.ReadAllBytesAsync(
            sourcePath,
            TestContext.CancellationToken).ConfigureAwait(false);
        var server = new SourceLinkTestServer(source);
        await using ConfiguredAsyncDisposable serverDisposal = server.ConfigureAwait(false);
        server.Start();
        string testDirectory = Path.Join(
            Path.GetTempPath(),
            $"csls-debugger-sourcelink-{Guid.NewGuid():N}");
        Directory.CreateDirectory(testDirectory);
        try
        {
            string programPath = await BuildSourceLinkFixtureAsync(
                sourcePath,
                testDirectory,
                server.SourceLinkPattern).ConfigureAwait(false);
            await ExerciseSourceLinkAsync(programPath, testDirectory, server)
                .ConfigureAwait(false);
        }
        finally
        {
            if (Directory.Exists(testDirectory))
            {
                Directory.Delete(testDirectory, recursive: true);
            }
        }
    }

    private async Task ExerciseSourceLinkAsync(
        string programPath,
        string testDirectory,
        SourceLinkTestServer server)
    {
        const string documentPath = "/_/SourceLink/Program.cs";
        DapTestClient client = await DapTestClient
            .CreateAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable clientDisposal = client.ConfigureAwait(false);
        int initializeSequence = await client.SendRequestAsync(
            "initialize",
            WriteEmptyObject,
            TestContext.CancellationToken).ConfigureAwait(false);
        using JsonDocument initialize = await client
            .ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        AssertResponse(initialize.RootElement, initializeSequence, "initialize", success: true);
        int launchSequence = await client.SendRequestAsync(
            "launch",
            writer => WriteSourceLinkLaunchArguments(
                writer,
                programPath,
                [Path.Join(testDirectory, "continue.signal"), "41", "source-link"],
                server.SourceLinkPattern),
            TestContext.CancellationToken).ConfigureAwait(false);
        using JsonDocument initialized = await client
            .ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        AssertEvent(initialized.RootElement, "initialized");
        int breakpointSequence = await client.SendRequestAsync(
            "setBreakpoints",
            writer => WriteSourceBreakpointArguments(writer, documentPath, 23),
            TestContext.CancellationToken).ConfigureAwait(false);
        using JsonDocument breakpoint = await client
            .ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        AssertResponse(breakpoint.RootElement, breakpointSequence, "setBreakpoints", success: true);
        int configurationSequence = await client.SendRequestAsync(
            "configurationDone",
            WriteEmptyObject,
            TestContext.CancellationToken).ConfigureAwait(false);
        int threadId = await ReadInitialBreakpointStopAsync(
            client,
            configurationSequence,
            launchSequence,
            TestContext.CancellationToken).ConfigureAwait(false);
        int sourceReference = await ReadSourceLinkReferenceAsync(client, threadId)
            .ConfigureAwait(false);
        await AssertSourceLinkContentAsync(client, sourceReference).ConfigureAwait(false);
        await AssertSourceLinkContentAsync(client, sourceReference).ConfigureAwait(false);
        Assert.AreEqual(1, server.RequestCount);
        await DisconnectStoppedSessionAsync(client).ConfigureAwait(false);
        Assert.AreEqual(
            0,
            await client.WaitForExitAsync(TestContext.CancellationToken).ConfigureAwait(false));
        Assert.AreEqual(string.Empty, client.Diagnostics.ToString());
    }

    private async Task<int> ReadSourceLinkReferenceAsync(DapTestClient client, int threadId)
    {
        int sequence = await client.SendRequestAsync(
            "stackTrace",
            writer =>
            {
                writer.WriteStartObject();
                writer.WriteNumber("threadId", threadId);
                writer.WriteEndObject();
            },
            TestContext.CancellationToken).ConfigureAwait(false);
        using JsonDocument response = await client
            .ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        AssertResponse(response.RootElement, sequence, "stackTrace", success: true);
        JsonElement source = response.RootElement.GetProperty("body")
            .GetProperty("stackFrames")[0]
            .GetProperty("source");
        Assert.AreEqual("Source Link", source.GetProperty("origin").GetString());
        Assert.IsFalse(source.TryGetProperty("path", out _));
        return source.GetProperty("sourceReference").GetInt32();
    }

    private async Task AssertSourceLinkContentAsync(DapTestClient client, int sourceReference)
    {
        int sequence = await client.SendRequestAsync(
            "source",
            writer => WriteSourceArguments(writer, sourceReference),
            TestContext.CancellationToken).ConfigureAwait(false);
        using JsonDocument response = await client
            .ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        AssertResponse(response.RootElement, sequence, "source", success: true);
        Assert.Contains(
            "answer++;",
            response.RootElement.GetProperty("body").GetProperty("content").GetString()!,
            StringComparison.Ordinal);
    }

    private async Task<string> BuildSourceLinkFixtureAsync(
        string sourcePath,
        string testDirectory,
        string sourceLinkPattern)
    {
        string projectPath = Path.Join(testDirectory, "SourceLinkFixture.csproj");
        File.Copy(sourcePath, Path.Join(testDirectory, "Program.cs"));
        await File.WriteAllTextAsync(
            Path.Join(testDirectory, "sourcelink.json"),
            JsonSerializer.Serialize(new
            {
                documents = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["/_/SourceLink/*"] = sourceLinkPattern
                }
            }),
            TestContext.CancellationToken).ConfigureAwait(false);
        string debugType = OperatingSystem.IsWindows() ? "full" : "portable";
        await File.WriteAllTextAsync(
            projectPath,
            $$"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>net10.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
                <DebugType>{{debugType}}</DebugType>
                <DebugSymbols>true</DebugSymbols>
                <PathMap>$(MSBuildProjectDirectory)=/_/SourceLink</PathMap>
                <SourceLink>$(MSBuildProjectDirectory)/sourcelink.json</SourceLink>
              </PropertyGroup>
            </Project>
            """,
            TestContext.CancellationToken).ConfigureAwait(false);
        string dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
        var startInfo = new ProcessStartInfo(dotnet)
        {
            WorkingDirectory = testDirectory,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        startInfo.ArgumentList.Add("build");
        startInfo.ArgumentList.Add(projectPath);
        startInfo.ArgumentList.Add("--nologo");
        using Process process = Process.Start(startInfo)!;
        string output = await process.StandardOutput.ReadToEndAsync(
            TestContext.CancellationToken).ConfigureAwait(false);
        string error = await process.StandardError.ReadToEndAsync(
            TestContext.CancellationToken).ConfigureAwait(false);
        await process.WaitForExitAsync(TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(0, process.ExitCode, $"{output}{Environment.NewLine}{error}");
        return Path.Join(testDirectory, "bin", "Debug", "net10.0", "SourceLinkFixture.dll");
    }

    private static void WriteSourceArguments(Utf8JsonWriter writer, int sourceReference)
    {
        writer.WriteStartObject();
        writer.WriteNumber("sourceReference", sourceReference);
        writer.WriteEndObject();
    }

    private static void WriteSourceLinkLaunchArguments(
        Utf8JsonWriter writer,
        string programPath,
        IReadOnlyList<string> arguments,
        string? sourceLinkPattern)
    {
        writer.WriteStartObject();
        writer.WriteBoolean("noDebug", false);
        writer.WriteString("program", programPath);
        writer.WriteStartArray("args");
        foreach (string argument in arguments)
        {
            writer.WriteStringValue(argument);
        }

        writer.WriteEndArray();
        if (sourceLinkPattern is not null)
        {
            writer.WriteStartObject("sourceLinkOptions");
            writer.WriteStartObject(sourceLinkPattern);
            writer.WriteBoolean("enabled", true);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        writer.WriteEndObject();
    }
}
