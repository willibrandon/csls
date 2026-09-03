using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies language-neutral debugger behavior across .NET compiler outputs.
/// </summary>
public sealed partial class DapSessionTests
{
    private static readonly string[] s_configurations = ["Debug", "Release"];
    private static readonly string[] s_projects =
    [
        "Csls.Debugger.Fixtures.CSharp",
        "Csls.Debugger.Fixtures.VisualBasic",
        "Csls.Debugger.Fixtures.FSharp"
    ];

    /// <summary>
    /// Binds and stops C#, Visual Basic, and F# Debug and Release executables.
    /// </summary>
    [TestMethod]
    [Timeout(120000, CooperativeCancellation = true)]
    public async Task PortablePdbLanguagesBindInDebugAndRelease()
    {
        foreach (string configuration in s_configurations)
        {
            foreach (string project in s_projects)
            {
                await BuildFixtureAsync(project, configuration).ConfigureAwait(false);
                await AssertFixtureStopsAsync(project, configuration).ConfigureAwait(false);
            }
        }
    }

    private async Task BuildFixtureAsync(string project, string configuration)
    {
        string repositoryRoot = FindRepositoryRoot();
        string extension = project.EndsWith("CSharp", StringComparison.Ordinal)
            ? "csproj"
            : project.EndsWith("VisualBasic", StringComparison.Ordinal)
                ? "vbproj"
                : "fsproj";
        var startInfo = new ProcessStartInfo
        {
            FileName = ResolveDotNetHost(),
            WorkingDirectory = repositoryRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("build");
        startInfo.ArgumentList.Add(Path.Join("test-assets", project, $"{project}.{extension}"));
        startInfo.ArgumentList.Add("--configuration");
        startInfo.ArgumentList.Add(configuration);
        startInfo.ArgumentList.Add("--nologo");
        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Could not build debugger fixture {project}.");
        Task<string> output = process.StandardOutput.ReadToEndAsync(TestContext.CancellationToken);
        Task<string> error = process.StandardError.ReadToEndAsync(TestContext.CancellationToken);
        await process.WaitForExitAsync(TestContext.CancellationToken).ConfigureAwait(false);
        string diagnostic = string.Concat(
            await output.ConfigureAwait(false),
            await error.ConfigureAwait(false));
        Assert.AreEqual(
            0,
            process.ExitCode,
            $"Fixture build failed for {project} ({configuration}):{Environment.NewLine}{diagnostic}");
    }

    private async Task AssertFixtureStopsAsync(string project, string configuration)
    {
        string repositoryRoot = FindRepositoryRoot();
        string sourceExtension = project.EndsWith("CSharp", StringComparison.Ordinal)
            ? "cs"
            : project.EndsWith("VisualBasic", StringComparison.Ordinal)
                ? "vb"
                : "fs";
        string sourcePath = Path.Join(
            repositoryRoot,
            "test-assets",
            project,
            $"Program.{sourceExtension}");
        string marker = sourceExtension switch
        {
            "cs" => "answer++;",
            "vb" => "answer += 1",
            _ => "answer <- answer + 1"
        };
        int breakpointLine = (await File.ReadAllLinesAsync(
            sourcePath,
            TestContext.CancellationToken).ConfigureAwait(false))
            .Select(static (line, index) => (Line: line, Number: index + 1))
            .Single(candidate => candidate.Line.Contains(marker, StringComparison.Ordinal))
            .Number;
        string program = Path.Join(
            repositoryRoot,
            "artifacts",
            "bin",
            project,
            configuration == "Debug" ? "debug" : "release",
            $"{project}.dll");
        string waitPath = Path.Join(
            Path.GetTempPath(),
            $"csls-debugger-language-{Guid.NewGuid():N}.signal");
        try
        {
            DapTestClient client = await DapTestClient
                .CreateAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            await using ConfiguredAsyncDisposable disposal = client.ConfigureAwait(false);
            await InitializeAndLaunchAsync(client, program, waitPath).ConfigureAwait(false);
            int stoppedThreadId = await ConfigureBreakpointAsync(
                client,
                sourcePath,
                breakpointLine).ConfigureAwait(false);
            await AssertStoppedFrameAsync(
                client,
                stoppedThreadId,
                sourcePath,
                breakpointLine).ConfigureAwait(false);
            await DisconnectAsync(client).ConfigureAwait(false);
            Assert.AreEqual(string.Empty, client.Diagnostics.ToString());
        }
        finally
        {
            File.Delete(waitPath);
        }
    }

    private async Task InitializeAndLaunchAsync(
        DapTestClient client,
        string program,
        string waitPath)
    {
        int initializeSequence = await client.SendRequestAsync(
            "initialize",
            WriteEmptyObject,
            TestContext.CancellationToken).ConfigureAwait(false);
        using JsonDocument initialize = await client
            .ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        AssertResponse(
            initialize.RootElement,
            initializeSequence,
            "initialize",
            success: true);
        _ = await client.SendRequestAsync(
            "launch",
            writer => WriteLaunchArguments(
                writer,
                program,
                [waitPath, "41", "ready"],
                wait: true,
                noDebug: false),
            TestContext.CancellationToken).ConfigureAwait(false);
        using JsonDocument initialized = await client
            .ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        AssertEvent(initialized.RootElement, "initialized");
    }

    private async Task<int> ConfigureBreakpointAsync(
        DapTestClient client,
        string sourcePath,
        int breakpointLine)
    {
        _ = await client.SendRequestAsync(
            "setBreakpoints",
            writer => WriteSourceBreakpointArguments(
                writer,
                sourcePath,
                breakpointLine),
            TestContext.CancellationToken).ConfigureAwait(false);
        using JsonDocument pending = await client
            .ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        _ = await client.SendRequestAsync(
            "configurationDone",
            WriteEmptyObject,
            TestContext.CancellationToken).ConfigureAwait(false);
        while (true)
        {
            using JsonDocument message = await client
                .ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            JsonElement root = message.RootElement;
            if (root.TryGetProperty("event", out JsonElement eventName) &&
                eventName.GetString() == "stopped")
            {
                Assert.AreEqual(
                    "breakpoint",
                    root.GetProperty("body").GetProperty("reason").GetString());
                return root.GetProperty("body").GetProperty("threadId").GetInt32();
            }
        }
    }

    private async Task AssertStoppedFrameAsync(
        DapTestClient client,
        int threadId,
        string sourcePath,
        int breakpointLine)
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
        using JsonDocument stack = await client
            .ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        AssertResponse(
            stack.RootElement,
            sequence,
            "stackTrace",
            success: true);
        JsonElement frame = stack.RootElement
            .GetProperty("body")
            .GetProperty("stackFrames")
            .EnumerateArray()
            .First(candidate => candidate.TryGetProperty("source", out JsonElement source) &&
                DebuggerTestPath.AreEquivalent(
                    source.GetProperty("path").GetString(),
                    sourcePath));
        Assert.AreEqual(breakpointLine, frame.GetProperty("line").GetInt32());
    }

    private async Task DisconnectAsync(DapTestClient client)
    {
        int sequence = await client.SendRequestAsync(
            "disconnect",
            WriteEmptyObject,
            TestContext.CancellationToken).ConfigureAwait(false);
        while (true)
        {
            using JsonDocument message = await client
                .ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            if (message.RootElement.TryGetProperty("request_seq", out JsonElement requestSequence) &&
                requestSequence.GetInt32() == sequence)
            {
                AssertResponse(
                    message.RootElement,
                    sequence,
                    "disconnect",
                    success: true);
                break;
            }
        }

        Assert.AreEqual(
            0,
            await client.WaitForExitAsync(TestContext.CancellationToken).ConfigureAwait(false));
    }

    private static string ResolveDotNetHost()
    {
        string? configured = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        return string.IsNullOrWhiteSpace(configured) ? "dotnet" : configured;
    }

}
