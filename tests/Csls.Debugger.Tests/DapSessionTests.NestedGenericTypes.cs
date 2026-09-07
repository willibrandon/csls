using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies that nested runtime types retain their declaring type's constructed arguments.
/// </summary>
public sealed partial class DapSessionTests
{
    /// <summary>
    /// Preserves generic argument ownership through nested classes, structures, arrays and null values.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task NestedGenericValuesPreserveDeclaringTypeArguments()
    {
        string source = Path.Join(FindRepositoryRoot(), "tests", "Csls.TestProcessHost", "DebuggerDumpArrayFixture.cs");
        string[] lines = await File.ReadAllLinesAsync(source, TestContext.CancellationToken).ConfigureAwait(false);
        int line = FindSourceLine(lines, "DebuggerBlockingWait.Wait(announcement);");
        DapTestClient client = await DapTestClient.CreateAsync(TestContext.CancellationToken).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable cleanup = client.ConfigureAwait(false);
        int thread = await LaunchToSourceBreakpointAsync(client, source, line,
            ["--debugger-dump-arrays", "nested-generic-types"]).ConfigureAwait(false);
        int frame = await AssertStoppedFrameAsync(client, thread, source, line).ConfigureAwait(false);
        JsonElement nested = await ReadEvaluationAsync(client, frame, "nested", true, TestContext.CancellationToken)
            .ConfigureAwait(false);
        Assert.AreEqual("System.Collections.Generic.Dictionary<int, System.Collections.Generic.List<string>>.KeyCollection[]",
            nested.GetProperty("type").GetString());
        JsonElement[] children = await ReadVariablesAsync(client, nested.GetProperty("variablesReference").GetInt32())
            .ConfigureAwait(false);
        Assert.HasCount(2, children);
        foreach (JsonElement child in children)
        {
            Assert.AreEqual("System.Collections.Generic.Dictionary<int, System.Collections.Generic.List<string>>.KeyCollection",
                child.GetProperty("type").GetString());
        }

        Assert.AreEqual("null", children[1].GetProperty("value").GetString());
        JsonElement enumerators = await ReadEvaluationAsync(client, frame, "nestedEnumerators", true,
            TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual("System.Collections.Generic.Dictionary<int, System.Collections.Generic.List<string>>.KeyCollection.Enumerator[]",
            enumerators.GetProperty("type").GetString());
        JsonElement enumerator = Assert.ContainsSingle(await ReadVariablesAsync(client,
            enumerators.GetProperty("variablesReference").GetInt32()).ConfigureAwait(false));
        Assert.AreEqual("System.Collections.Generic.Dictionary<int, System.Collections.Generic.List<string>>.KeyCollection.Enumerator",
            enumerator.GetProperty("type").GetString());
        Assert.AreEqual(frame, await AssertStoppedFrameAsync(client, thread, source, line).ConfigureAwait(false));
        await DisconnectAsync(client).ConfigureAwait(false);
        Assert.AreEqual(string.Empty, client.Diagnostics.ToString());
    }
}
