using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies operation-owned expression values through a stopped real managed process.
/// </summary>
public sealed partial class DapSessionTests
{
    /// <summary>
    /// Keeps returned structs and Raw View bound to current physical storage after intermediate reads and edits.
    /// </summary>
    /// <param name="expression">The local or array element whose physical storage is inspected.</param>
    /// <param name="untouchedExpression">The separate tuple whose fields must remain unchanged.</param>
    [TestMethod]
    [DataRow("localTupleArray[0]", "localTuple")]
    [DataRow("localTuple", "localTupleArray[0]")]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task ExpressionResultsPreserveValueTypeStorage(string expression, string untouchedExpression)
    {
        string waitPath = CreateResultsViewSignalPath();
        try
        {
            DapTestClient client = await StartStoppedFixtureAsync(waitPath).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable disposal = client.ConfigureAwait(false);
            JsonElement frame = await GetFixtureFrameAsync(client).ConfigureAwait(false);
            int frameId = frame.GetProperty("id").GetInt32();
            for (int iteration = 0; iteration < 8; iteration++)
            {
                JsonElement scalar = await ReadEvaluationAsync(client, frameId, $"{expression}.Number",
                    success: true, TestContext.CancellationToken).ConfigureAwait(false);
                Assert.AreEqual("42", scalar.GetProperty("result").GetString());
                JsonElement rejected = await ReadEvaluationAsync(client, frameId, $"{expression}._missing",
                    success: false, TestContext.CancellationToken).ConfigureAwait(false);
                Assert.Contains("_missing", Assert.IsInstanceOfType<string>(rejected.GetProperty("message").GetString()));
            }

            JsonElement tuple = await ReadEvaluationAsync(client, frameId, expression,
                success: true, TestContext.CancellationToken).ConfigureAwait(false);
            int reference = tuple.GetProperty("variablesReference").GetInt32();
            Assert.IsGreaterThan(0, reference);
            JsonElement[] fields = await ReadVariablesAsync(client, reference).ConfigureAwait(false);
            Assert.AreSequenceEqual(["Number", "Text", "Raw View"],
                fields.Select(field => field.GetProperty("name").GetString()).ToArray());
            Assert.AreSequenceEqual(["42", "\"answer\""],
                fields.Take(2).Select(field => field.GetProperty("value").GetString()).ToArray());
            int rawReference = fields[2].GetProperty("variablesReference").GetInt32();
            Assert.IsGreaterThan(0, rawReference);
            JsonElement assigned = await ReadSetVariableAsync(client, reference, "Number", "99",
                success: true, TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual("99", assigned.GetProperty("value").GetString());
            JsonElement actual = await ReadEvaluationAsync(client, frameId, $"{expression}.Number",
                success: true, TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual("99", actual.GetProperty("result").GetString());
            JsonElement untouched = await ReadEvaluationAsync(client, frameId, $"{untouchedExpression}.Number",
                success: true, TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual("42", untouched.GetProperty("result").GetString());
            fields = await ReadVariablesAsync(client, reference).ConfigureAwait(false);
            Assert.AreSequenceEqual(["Number", "Text", "Raw View"],
                fields.Select(field => field.GetProperty("name").GetString()).ToArray());
            Assert.AreSequenceEqual(["99", "\"answer\""],
                fields.Take(2).Select(field => field.GetProperty("value").GetString()).ToArray());
            JsonElement[] rawFields = await ReadVariablesAsync(client, rawReference).ConfigureAwait(false);
            Assert.AreSequenceEqual(["Item1", "Item2"],
                rawFields.Select(field => field.GetProperty("name").GetString()).ToArray());
            Assert.AreSequenceEqual(["99", "\"answer\""],
                rawFields.Select(field => field.GetProperty("value").GetString()).ToArray());
            AssertSameLogicalFrame(frame, await GetFixtureFrameAsync(client).ConfigureAwait(false));
            await DisconnectStoppedSessionAsync(client).ConfigureAwait(false);
            Assert.AreEqual(0, await client.WaitForExitAsync(TestContext.CancellationToken).ConfigureAwait(false));
            Assert.AreEqual(string.Empty, client.Diagnostics.ToString());
        }
        finally
        {
            File.Delete(waitPath);
        }
    }

    /// <summary>
    /// Releases successful and rejected inspection intermediates while preserving published array and frame handles.
    /// </summary>
    /// <param name="rejected">Whether each inspected expression fails after binding its runtime operands.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [TestCategory("DebuggerStress")]
    [Timeout(180000, CooperativeCancellation = true)]
    public async Task RepeatedReadOnlyExpressionsReleaseUnpublishedValues(bool rejected)
    {
        long started = Stopwatch.GetTimestamp();
        string waitPath = CreateResultsViewSignalPath();
        try
        {
            DapTestClient client = await StartStoppedFixtureAsync(waitPath).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable disposal = client.ConfigureAwait(false);
            using DapTestCancellationCapture capture = CaptureProtocolOnCancellation(client);
            JsonElement frame = await GetFixtureFrameAsync(client).ConfigureAwait(false);
            int frameId = frame.GetProperty("id").GetInt32();
            JsonElement array = await ReadEvaluationAsync(client, frameId, "localStringIdentity._items",
                success: true, TestContext.CancellationToken).ConfigureAwait(false);
            int reference = array.GetProperty("variablesReference").GetInt32();
            Assert.IsGreaterThan(0, reference);
            string[] terms = [.. Enumerable.Repeat("localText == \"answer!\"", 128)];
            while (terms.Length > 1)
            {
                terms = [.. terms.Chunk(2).Select(pair => $"({pair[0]} && {pair[1]})")];
            }

            string expression = terms[0];
            string rejectedExpression = $"({expression}) ? localStringIdentity._missing : 0";
            // Cross the generation-wide value budget with reads that publish no runtime value handles.
            for (int iteration = 0; iteration < 520; iteration++)
            {
                if (iteration % 64 == 0)
                {
                    TestContext.WriteLine($"Read-only expression {iteration + 1}/520, rejected={rejected}, " +
                        $"elapsed={Stopwatch.GetElapsedTime(started).TotalMilliseconds:F1} ms.");
                }
                JsonElement result = await ReadEvaluationAsync(client, frameId,
                    rejected ? rejectedExpression : expression, success: !rejected,
                    TestContext.CancellationToken).ConfigureAwait(false);
                if (rejected)
                {
                    string message = Assert.IsInstanceOfType<string>(result.GetProperty("message").GetString());
                    Assert.Contains("_missing", message);
                }
                else
                {
                    Assert.AreEqual("true", result.GetProperty("result").GetString());
                    Assert.AreEqual("bool", result.GetProperty("type").GetString());
                    Assert.AreEqual(0, result.GetProperty("variablesReference").GetInt32());
                }
            }

            TestContext.WriteLine($"Completed read-only expression requests in {Stopwatch.GetElapsedTime(started).TotalMilliseconds:F1} ms.");
            JsonElement[] elements = await ReadVariablesAsync(client, reference).ConfigureAwait(false);
            Assert.AreSequenceEqual(["\"unused\"", "\"array\\\\value\""],
                elements.Select(element => element.GetProperty("value").GetString()).ToArray());
            JsonElement refreshed = await GetFixtureFrameAsync(client).ConfigureAwait(false);
            AssertSameLogicalFrame(frame, refreshed);
            await AssertStringIdentityExpressionAsync(client, frameId, "localText", "\"answer!\"")
                .ConfigureAwait(false);
            await DisconnectStoppedSessionAsync(client).ConfigureAwait(false);
            Assert.AreEqual(0, await client.WaitForExitAsync(TestContext.CancellationToken).ConfigureAwait(false));
            Assert.AreEqual(string.Empty, client.Diagnostics.ToString());
        }
        finally
        {
            File.Delete(waitPath);
        }
    }
}
