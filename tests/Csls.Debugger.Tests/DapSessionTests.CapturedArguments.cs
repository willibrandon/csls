using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies source-parameter inspection and mutation through real compiled state machines.
/// </summary>
public sealed partial class DapSessionTests
{
    /// <summary>
    /// Preserves generic source parameters and receiver identity after asynchronous resumption.
    /// </summary>
    /// <param name="language">The compiler fixture language.</param>
    /// <param name="extension">The source file extension.</param>
    /// <param name="receiver">The language-specific instance receiver.</param>
    /// <param name="configuration">The compiler optimization configuration.</param>
    [TestMethod]
    [DataRow("CSharp", "cs", "this", "Debug")]
    [DataRow("CSharp", "cs", "this", "Release")]
    [DataRow("VisualBasic", "vb", "Me", "Debug")]
    [DataRow("VisualBasic", "vb", "Me", "Release")]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task CapturedGenericArgumentsRetainSourceIdentityAfterAwait(
        string language, string extension, string receiver, string configuration)
    {
        string project = $"Csls.Debugger.Fixtures.{language}";
        string program = DebuggerLanguageFixtures.GetProgramPath(project, configuration);
        string source = Path.Join(FindRepositoryRoot(), "test-assets", project, $"DebuggerGenericFixture.{extension}");
        int breakpointLine = FindSourceLine(await File.ReadAllLinesAsync(source, TestContext.CancellationToken)
            .ConfigureAwait(false), "Console.Write(argument)");
        DapTestClient client = await DapTestClient.CreateAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable cleanup = client.ConfigureAwait(false);
        _ = await LaunchAtEntryAsync(client, ResolveTestProcessHost(),
            ["--debugger-captured-arguments", program, "original", "replacement", "receiver-value"])
            .ConfigureAwait(false);
        int threadId = await ContinueEntryToCapturedArgumentsAsync(client, source, breakpointLine)
            .ConfigureAwait(false);
        JsonElement frame = await ReadTopSourceFrameAsync(client, threadId).ConfigureAwait(false);
        int frameId = frame.GetProperty("id").GetInt32();
        Assert.AreEqual(breakpointLine, frame.GetProperty("line").GetInt32());
        (int argumentsReference, _) = await ReadFrameScopeReferencesAsync(client, frameId).ConfigureAwait(false);
        JsonElement[] arguments = await ReadVariablesAsync(client, argumentsReference).ConfigureAwait(false);
        Assert.AreSequenceEqual([receiver, "argument", "replacement", "unused"],
            arguments.Select(argument => argument.GetProperty("name").GetString()).ToArray());
        Assert.AreEqual("\"original\"", arguments[1].GetProperty("value").GetString());
        Assert.AreEqual("string", arguments[1].GetProperty("type").GetString());
        Assert.AreEqual("argument", arguments[1].GetProperty("evaluateName").GetString());
        if (language == "CSharp" && configuration == "Release")
        {
            Assert.AreEqual("<optimized out>", arguments[3].GetProperty("value").GetString());
            Assert.AreEqual("int", arguments[3].GetProperty("type").GetString());
            Assert.AreEqual(0, arguments[3].GetProperty("variablesReference").GetInt32());
            Assert.IsFalse(arguments[3].TryGetProperty("evaluateName", out _));
            Assert.AreEqual("readOnly", Assert.ContainsSingle(arguments[3].GetProperty("presentationHint")
                .GetProperty("attributes").EnumerateArray()).GetString());
            JsonElement unavailable = await ReadEvaluationAsync(client, frameId, "unused", success: false,
                TestContext.CancellationToken).ConfigureAwait(false);
            string? message = unavailable.GetProperty("message").GetString();
            Assert.IsNotNull(message);
            Assert.Contains("optimized out", message);
            JsonElement rejected = await ReadSetVariableAsync(client, argumentsReference, "unused", "19",
                success: false, TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual("Variable 'unused' has no valid source expression and cannot be assigned.",
                rejected.GetProperty("message").GetString());
            JsonElement rejectedExpression = await ReadSetExpressionAsync(client, frameId, "unused", "19",
                success: false, TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(message, rejectedExpression.GetProperty("message").GetString());
        }
        else
        {
            Assert.AreEqual("17", arguments[3].GetProperty("value").GetString());
            Assert.AreEqual("int", arguments[3].GetProperty("type").GetString());
        }

        JsonElement[] page = await ReadVariablesAsync(client, argumentsReference, start: 1, count: 2)
            .ConfigureAwait(false);
        Assert.AreSequenceEqual(["argument", "replacement"],
            page.Select(argument => argument.GetProperty("name").GetString()).ToArray());
        Assert.IsEmpty(await ReadVariablesAsync(client, argumentsReference, start: 4, count: 1)
            .ConfigureAwait(false));
        JsonElement completion = Assert.ContainsSingle(await ReadCompletionsAsync(client, frameId,
            language == "VisualBasic" ? "AR" : "ar", TestContext.CancellationToken).ConfigureAwait(false));
        Assert.AreEqual("argument", completion.GetProperty("label").GetString());
        JsonElement owner = await ReadEvaluationAsync(client, frameId, receiver + "._value", success: true,
            TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual("\"receiver-value\"", owner.GetProperty("result").GetString());
        JsonElement replacement = await ReadSetVariableAsync(client, argumentsReference, "argument", "replacement",
            success: true, TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual("\"replacement\"", replacement.GetProperty("value").GetString());
        JsonElement assigned = await ReadEvaluationAsync(client, frameId, "argument", success: true,
            TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual("\"replacement\"", assigned.GetProperty("result").GetString());
        await ContinueEntryToExitAsync(client, threadId, "replacement").ConfigureAwait(false);
    }

    /// <summary>
    /// Formats closed source declarations after the compiler eliminates their captured storage.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task OptimizedOutArgumentsRetainConstructedDeclaredTypes()
    {
        const string project = "Csls.Debugger.Fixtures.CSharp";
        string program = DebuggerLanguageFixtures.GetProgramPath(project, "Release");
        string source = Path.Join(FindRepositoryRoot(), "test-assets", project, "DebuggerGenericFixture.cs");
        int line = FindSourceLine(await File.ReadAllLinesAsync(source, TestContext.CancellationToken)
            .ConfigureAwait(false), "Console.Write(42)");
        DapTestClient client = await DapTestClient.CreateAsync(TestContext.CancellationToken).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable cleanup = client.ConfigureAwait(false);
        _ = await LaunchAtEntryAsync(client, ResolveTestProcessHost(),
            ["--debugger-unused-argument-shapes", program]).ConfigureAwait(false);
        int threadId = await ContinueEntryToCapturedArgumentsAsync(client, source, line).ConfigureAwait(false);
        JsonElement frame = await ReadTopSourceFrameAsync(client, threadId).ConfigureAwait(false);
        Assert.AreEqual(line, frame.GetProperty("line").GetInt32());
        (int argumentsReference, _) = await ReadFrameScopeReferencesAsync(client,
            frame.GetProperty("id").GetInt32()).ConfigureAwait(false);
        JsonElement[] arguments = await ReadVariablesAsync(client, argumentsReference).ConfigureAwait(false);
        Assert.AreSequenceEqual(
            ["typeArgument", "methodArgument", "vector", "rectangle", "nullable", "pair", "nested", "jagged", "amount", "offsetArray"],
            arguments.Select(argument => argument.GetProperty("name").GetString()).ToArray());
        Assert.AreSequenceEqual(
            ["int", "string", "int[]", "string[,]", "int?", "(int Count, string Name)",
                "System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<int?[]>>", "int[][]", "decimal", "int[*]"],
            arguments.Select(argument => argument.GetProperty("type").GetString()).ToArray());
        foreach (JsonElement argument in arguments)
        {
            string? name = argument.GetProperty("name").GetString();
            Assert.AreEqual("<optimized out>", argument.GetProperty("value").GetString(), name);
            Assert.AreEqual(0, argument.GetProperty("variablesReference").GetInt32(), name);
            Assert.IsFalse(argument.TryGetProperty("evaluateName", out _), name);
            Assert.AreEqual("readOnly", Assert.ContainsSingle(argument.GetProperty("presentationHint")
                .GetProperty("attributes").EnumerateArray()).GetString(), name);
        }

        JsonElement[] page = await ReadVariablesAsync(client, argumentsReference, start: 3, count: 3)
            .ConfigureAwait(false);
        Assert.AreSequenceEqual(["string[,]", "int?", "(int Count, string Name)"],
            page.Select(argument => argument.GetProperty("type").GetString()).ToArray());
        await ContinueEntryToExitAsync(client, threadId, "42").ConfigureAwait(false);
    }

    /// <summary>
    /// Reads, completes, and changes an async entry argument before the application consumes it.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task AsyncEntryArgumentsShareInspectionEvaluationAndAssignment()
    {
        DapTestClient client = await DapTestClient.CreateAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable cleanup = client.ConfigureAwait(false);
        (int threadId, _) = await LaunchAtEntryAsync(client, ResolveTestProcessHost(),
            ["--print-environment", "CSLS_DEBUGGER_ENTRY_VALUE"]).ConfigureAwait(false);
        JsonElement frame = await ReadTopSourceFrameAsync(client, threadId).ConfigureAwait(false);
        int frameId = frame.GetProperty("id").GetInt32();
        (int argumentsReference, _) = await ReadFrameScopeReferencesAsync(client, frameId).ConfigureAwait(false);
        JsonElement argument = Assert.ContainsSingle(await ReadVariablesAsync(client, argumentsReference)
            .ConfigureAwait(false));
        Assert.AreEqual("args", argument.GetProperty("name").GetString());
        Assert.AreEqual("args", argument.GetProperty("evaluateName").GetString());
        Assert.AreEqual("string[]", argument.GetProperty("type").GetString());
        int arrayReference = argument.GetProperty("variablesReference").GetInt32();
        JsonElement[] elements = await ReadVariablesAsync(client, arrayReference).ConfigureAwait(false);
        Assert.AreSequenceEqual(["\"--print-environment\"", "\"CSLS_DEBUGGER_ENTRY_VALUE\""],
            elements.Select(element => element.GetProperty("value").GetString()).ToArray());
        Assert.IsEmpty(await ReadVariablesAsync(client, argumentsReference, start: 1, count: 1)
            .ConfigureAwait(false));
        JsonElement completion = Assert.ContainsSingle(await ReadCompletionsAsync(
            client, frameId, "ar", TestContext.CancellationToken).ConfigureAwait(false));
        Assert.AreEqual("args", completion.GetProperty("label").GetString());
        JsonElement original = await ReadEvaluationAsync(client, frameId, "args[1]", success: true,
            TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual("\"CSLS_DEBUGGER_ENTRY_VALUE\"", original.GetProperty("result").GetString());

        JsonElement assigned = await ReadSetExpressionAsync(client, frameId, "args[1]", "args[0]",
            success: true, TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual("\"--print-environment\"", assigned.GetProperty("value").GetString());
        JsonElement updated = await ReadEvaluationAsync(client, frameId, "args[1]", success: true,
            TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual("\"--print-environment\"", updated.GetProperty("result").GetString());
        JsonElement second = Assert.ContainsSingle(await ReadVariablesAsync(client, arrayReference,
            start: 1, count: 1).ConfigureAwait(false));
        Assert.AreEqual("\"--print-environment\"", second.GetProperty("value").GetString());
        await ContinueEntryToExitAsync(client, threadId, "entry-mutated").ConfigureAwait(false);
    }

    private async Task<int> ContinueEntryToCapturedArgumentsAsync(
        DapTestClient client, string source, int line)
    {
        int sequence = await client.SendRequestAsync("setBreakpoints",
            writer => WriteSourceBreakpointArguments(writer, source, line),
            TestContext.CancellationToken).ConfigureAwait(false);
        int breakpointId;
        using (JsonDocument breakpoints = await client.ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false))
        {
            AssertResponse(breakpoints.RootElement, sequence, "setBreakpoints", success: true);
            JsonElement breakpoint = Assert.ContainsSingle(breakpoints.RootElement.GetProperty("body")
                .GetProperty("breakpoints").EnumerateArray());
            Assert.IsFalse(breakpoint.GetProperty("verified").GetBoolean());
            breakpointId = breakpoint.GetProperty("id").GetInt32();
        }

        sequence = await client.SendRequestAsync("continue", WriteEmptyObject,
            TestContext.CancellationToken).ConfigureAwait(false);
        bool responded = false;
        bool continued = false;
        bool bound = false;
        while (true)
        {
            using JsonDocument message = await client.ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            JsonElement root = message.RootElement;
            if (root.GetProperty("type").GetString() == "response")
            {
                Assert.IsFalse(responded);
                AssertResponse(root, sequence, "continue", success: true);
                responded = true;
                continue;
            }

            if (root.GetProperty("event").GetString() == "continued")
            {
                Assert.IsFalse(continued);
                continued = true;
                continue;
            }

            if (root.GetProperty("event").GetString() == "breakpoint")
            {
                Assert.IsFalse(bound);
                JsonElement breakpoint = root.GetProperty("body").GetProperty("breakpoint");
                Assert.AreEqual(breakpointId, breakpoint.GetProperty("id").GetInt32());
                Assert.IsTrue(breakpoint.GetProperty("verified").GetBoolean());
                Assert.AreEqual(line, breakpoint.GetProperty("line").GetInt32());
                bound = true;
                continue;
            }

            AssertEvent(root, "stopped");
            Assert.IsTrue(responded);
            Assert.IsTrue(continued);
            Assert.IsTrue(bound);
            Assert.AreEqual("breakpoint", root.GetProperty("body").GetProperty("reason").GetString());
            return root.GetProperty("body").GetProperty("threadId").GetInt32();
        }
    }
}
