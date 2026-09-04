using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies language-neutral debugger behavior across .NET compiler outputs.
/// </summary>
public sealed partial class DapSessionTests
{
    private static readonly string[] s_projects =
    [
        "Csls.Debugger.Fixtures.CSharp",
        "Csls.Debugger.Fixtures.VisualBasic",
        "Csls.Debugger.Fixtures.FSharp"
    ];

    /// <summary>
    /// Binds, inspects, and invokes Debug C#, Visual Basic, and F# executables.
    /// </summary>
    [TestMethod]
    [Timeout(120000, CooperativeCancellation = true)]
    public Task PortablePdbLanguagesBindInDebug() =>
        AssertPortablePdbLanguagesAsync("Debug");

    /// <summary>
    /// Binds and inspects Release C#, Visual Basic, and F# executables.
    /// </summary>
    [TestMethod]
    [Timeout(120000, CooperativeCancellation = true)]
    public Task PortablePdbLanguagesBindInRelease() =>
        AssertPortablePdbLanguagesAsync("Release");

    private async Task AssertPortablePdbLanguagesAsync(string configuration)
    {
        string artifactsPath = Path.Join(
            Path.GetTempPath(),
            $"csls-debugger-language-{Guid.NewGuid():N}");
        try
        {
            await BuildFixturesAsync(configuration, artifactsPath).ConfigureAwait(false);
            foreach (string project in s_projects)
            {
                string program = GetFixtureProgram(project, configuration, artifactsPath);
                await AssertFixtureStopsAsync(project, configuration, program)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            await DebuggerTestDirectoryReleaseWaiter.DeleteAsync(
                artifactsPath,
                TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
    }

    private async Task BuildFixturesAsync(
        string configuration,
        string artifactsPath)
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
            "Csls.Debugger.LanguageFixtures.slnx"));
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
            $"Fixture build failed for {configuration}:{Environment.NewLine}{diagnostic}");
    }

    private static string GetFixtureProgram(
        string project,
        string configuration,
        string artifactsPath)
    {
        string program = Path.Join(
            artifactsPath,
            "bin",
            project,
            configuration == "Debug" ? "debug" : "release",
            $"{project}.dll");
        Assert.IsTrue(File.Exists(program), $"Fixture output was not found at {program}.");
        return program;
    }

    private async Task AssertFixtureStopsAsync(
        string project,
        string configuration,
        string program)
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
        string waitPath = Path.Join(
            Path.GetTempPath(),
            $"csls-debugger-language-{Guid.NewGuid():N}.signal");
        try
        {
            DapTestClient client = await DapTestClient
                .CreateAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            await using ConfiguredAsyncDisposable disposal = client.ConfigureAwait(false);
            bool runFunctionEvaluation = configuration == "Debug";
            await InitializeAndLaunchAsync(
                client,
                program,
                waitPath,
                suppressJitOptimizations: runFunctionEvaluation).ConfigureAwait(false);
            int stoppedThreadId = await ConfigureBreakpointAsync(
                client,
                sourcePath,
                breakpointLine,
                project.EndsWith("CSharp", StringComparison.Ordinal)
                    ? "answer == 41"
                    : "answer = 41").ConfigureAwait(false);
            int frameId = await AssertStoppedFrameAsync(
                client,
                stoppedThreadId,
                sourcePath,
                breakpointLine).ConfigureAwait(false);
            JsonElement evaluation = await ReadEvaluationAsync(
                client,
                frameId,
                "answer + 1",
                success: true,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(
                "42",
                evaluation.GetProperty("result").GetString(),
                $"Unexpected {project} {configuration} expression result.");
            string conversionExpression = sourceExtension switch
            {
                "cs" => "(long)answer",
                "vb" => "CType(answer, Long)",
                _ => "int64 answer"
            };
            JsonElement convertedEvaluation = await ReadEvaluationAsync(
                client,
                frameId,
                conversionExpression,
                success: true,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(
                "41",
                convertedEvaluation.GetProperty("result").GetString(),
                $"Unexpected {project} {configuration} conversion result.");
            Assert.AreEqual(
                "long",
                convertedEvaluation.GetProperty("type").GetString(),
                $"Unexpected {project} {configuration} conversion type.");
            JsonElement displayedValue = await ReadEvaluationAsync(
                client,
                frameId,
                "value",
                success: true,
                TestContext.CancellationToken).ConfigureAwait(false);
            string expectedDisplay = sourceExtension switch
            {
                "cs" => "csharp=41",
                "vb" => "visual-basic=41",
                _ => "fsharp=41"
            };
            string expectedDisplayType = sourceExtension switch
            {
                "cs" => "csharp-display",
                "vb" => "visual-basic-display",
                _ => "fsharp-display"
            };
            Assert.AreEqual(
                expectedDisplay,
                displayedValue.GetProperty("result").GetString(),
                $"Unexpected {project} {configuration} debugger display value.");
            Assert.AreEqual(
                expectedDisplayType,
                displayedValue.GetProperty("type").GetString(),
                $"Unexpected {project} {configuration} debugger display type.");
            JsonElement[] rootCompletions = await ReadCompletionsAsync(
                client,
                frameId,
                sourceExtension == "vb" ? "ANS" : "ans",
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.Contains(
                "answer",
                rootCompletions.Select(completion =>
                    completion.GetProperty("label").GetString()!),
                $"Missing {project} {configuration} root completion.");
            JsonElement[] memberCompletions = await ReadCompletionsAsync(
                client,
                frameId,
                "value.A",
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.Contains(
                "AddNumber",
                memberCompletions.Select(completion =>
                    completion.GetProperty("label").GetString()!),
                $"Missing {project} {configuration} member completion.");
            if (project.EndsWith("FSharp", StringComparison.Ordinal))
            {
                JsonElement indexedEvaluation = await ReadEvaluationAsync(
                    client,
                    frameId,
                    "numbers.[1]",
                    success: true,
                    TestContext.CancellationToken).ConfigureAwait(false);
                Assert.AreEqual(
                    "42",
                    indexedEvaluation.GetProperty("result").GetString(),
                    $"Unexpected {project} {configuration} indexed expression result.");
            }

            if (runFunctionEvaluation)
            {
                JsonElement invoked = await ReadEvaluationAsync(
                    client,
                    frameId,
                    "value.AddNumber(1)",
                    success: true,
                    TestContext.CancellationToken).ConfigureAwait(false);
                Assert.AreEqual(
                    "42",
                    invoked.GetProperty("result").GetString(),
                    $"Unexpected {project} function-evaluation result.");
                using JsonDocument invalidated = await client
                    .ReadMessageAsync(TestContext.CancellationToken)
                    .ConfigureAwait(false);
                AssertEvent(invalidated.RootElement, "invalidated");

                frameId = await AssertStoppedFrameAsync(
                    client,
                    stoppedThreadId,
                    sourcePath,
                    breakpointLine).ConfigureAwait(false);
                JsonElement stringInvocation = await ReadEvaluationAsync(
                    client,
                    frameId,
                    "value.StringLength(\"dotnet\")",
                    success: true,
                    TestContext.CancellationToken).ConfigureAwait(false);
                Assert.AreEqual(
                    "6",
                    stringInvocation.GetProperty("result").GetString(),
                    $"Unexpected {project} string function-evaluation result.");
                using JsonDocument stringInvalidated = await client
                    .ReadMessageAsync(TestContext.CancellationToken)
                    .ConfigureAwait(false);
                AssertEvent(stringInvalidated.RootElement, "invalidated");

                frameId = await AssertStoppedFrameAsync(
                    client,
                    stoppedThreadId,
                    sourcePath,
                    breakpointLine).ConfigureAwait(false);
                JsonElement staticInvocation = await ReadEvaluationAsync(
                    client,
                    frameId,
                    "System.Math.Abs(-41)",
                    success: true,
                    TestContext.CancellationToken).ConfigureAwait(false);
                Assert.AreEqual(
                    "41",
                    staticInvocation.GetProperty("result").GetString(),
                    $"Unexpected {project} static function-evaluation result.");
                using JsonDocument staticInvalidated = await client
                    .ReadMessageAsync(TestContext.CancellationToken)
                    .ConfigureAwait(false);
                AssertEvent(staticInvalidated.RootElement, "invalidated");

                frameId = await AssertStoppedFrameAsync(
                    client,
                    stoppedThreadId,
                    sourcePath,
                    breakpointLine).ConfigureAwait(false);
                string constructedType = $"Csls.Debugger.Fixtures.{project["Csls.Debugger.Fixtures.".Length..]}.DebuggerFixtureValue";
                string constructionExpression = sourceExtension switch
                {
                    "cs" => $"new {constructedType}(7)",
                    "vb" => $"New {constructedType}(7)",
                    _ => $"new {constructedType}(7)"
                };
                JsonElement constructed = await ReadEvaluationAsync(
                    client,
                    frameId,
                    constructionExpression,
                    success: true,
                    TestContext.CancellationToken).ConfigureAwait(false);
                string expectedConstructedDisplay = sourceExtension switch
                {
                    "cs" => "csharp=7",
                    "vb" => "visual-basic=7",
                    _ => "fsharp=7"
                };
                Assert.AreEqual(
                    expectedConstructedDisplay,
                    constructed.GetProperty("result").GetString(),
                    $"Unexpected {project} constructed value.");
                Assert.AreEqual(
                    expectedDisplayType,
                    constructed.GetProperty("type").GetString(),
                    $"Unexpected {project} constructed type.");
                Assert.IsGreaterThan(
                    0,
                    constructed.GetProperty("variablesReference").GetInt32(),
                    $"The {project} constructed value is not expandable.");
                using JsonDocument constructionInvalidated = await client
                    .ReadMessageAsync(TestContext.CancellationToken)
                    .ConfigureAwait(false);
                AssertEvent(constructionInvalidated.RootElement, "invalidated");

                frameId = await AssertStoppedFrameAsync(
                    client,
                    stoppedThreadId,
                    sourcePath,
                    breakpointLine).ConfigureAwait(false);
                JsonElement assigned = await ReadSetExpressionAsync(
                    client,
                    frameId,
                    "answer",
                    sourceExtension switch
                    {
                        "cs" => "(int)(short)43",
                        "vb" => "CInt(CShort(43))",
                        _ => "int (int16 43)"
                    },
                    success: true,
                    TestContext.CancellationToken).ConfigureAwait(false);
                Assert.AreEqual(
                    "43",
                    assigned.GetProperty("value").GetString(),
                    $"Unexpected {project} assignment result.");

                frameId = await AssertStoppedFrameAsync(
                    client,
                    stoppedThreadId,
                    sourcePath,
                    breakpointLine).ConfigureAwait(false);
                JsonElement afterInvocation = await ReadEvaluationAsync(
                    client,
                    frameId,
                    "answer + 1",
                    success: true,
                    TestContext.CancellationToken).ConfigureAwait(false);
                Assert.AreEqual(
                    "44",
                    afterInvocation.GetProperty("result").GetString(),
                    $"Unexpected {project} result after assignment.");

                JsonElement assignedCallResult = await ReadSetExpressionAsync(
                    client,
                    frameId,
                    "answer",
                    "value.AddNumber(8)",
                    success: true,
                    TestContext.CancellationToken,
                    targetCodeExecuted: true).ConfigureAwait(false);
                Assert.AreEqual(
                    "49",
                    assignedCallResult.GetProperty("value").GetString(),
                    $"Unexpected {project} call-result assignment.");

                frameId = await AssertStoppedFrameAsync(
                    client,
                    stoppedThreadId,
                    sourcePath,
                    breakpointLine).ConfigureAwait(false);
                JsonElement afterCallAssignment = await ReadEvaluationAsync(
                    client,
                    frameId,
                    "answer + 1",
                    success: true,
                    TestContext.CancellationToken).ConfigureAwait(false);
                Assert.AreEqual(
                    "50",
                    afterCallAssignment.GetProperty("result").GetString(),
                    $"Unexpected {project} result after call-result assignment.");
            }

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
        string waitPath,
        bool suppressJitOptimizations = false)
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
                noDebug: false,
                suppressJitOptimizations: suppressJitOptimizations),
            TestContext.CancellationToken).ConfigureAwait(false);
        using JsonDocument initialized = await client
            .ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        AssertEvent(initialized.RootElement, "initialized");
    }

    private async Task<int> ConfigureBreakpointAsync(
        DapTestClient client,
        string sourcePath,
        int breakpointLine,
        string? condition = null)
    {
        _ = await client.SendRequestAsync(
            "setBreakpoints",
            writer =>
            {
                writer.WriteStartObject();
                writer.WriteStartObject("source");
                writer.WriteString("path", sourcePath);
                writer.WriteEndObject();
                writer.WriteStartArray("breakpoints");
                writer.WriteStartObject();
                writer.WriteNumber("line", breakpointLine);
                if (condition is not null)
                {
                    writer.WriteString("condition", condition);
                }

                writer.WriteEndObject();
                writer.WriteEndArray();
                writer.WriteEndObject();
            },
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

    private async Task<int> AssertStoppedFrameAsync(
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
        return frame.GetProperty("id").GetInt32();
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
