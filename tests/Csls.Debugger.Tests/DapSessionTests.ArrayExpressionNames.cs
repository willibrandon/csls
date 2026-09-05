using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies re-evaluable array expressions through real source-language debugger sessions.
/// </summary>
public sealed partial class DapSessionTests
{
    /// <summary>
    /// Preserves each language's array syntax through expansion, evaluation, and dynamic-index assignment.
    /// </summary>
    /// <param name="language">The compiler fixture language.</param>
    /// <param name="extension">The fixture source extension.</param>
    /// <param name="marker">The stopped source statement.</param>
    /// <param name="destination">The canonical first-element expression.</param>
    /// <param name="source">The second-element expression.</param>
    /// <param name="dynamicDestination">The destination index read from its original first field.</param>
    [TestMethod]
    [DataRow("CSharp", "cs", "answer++;", "pairs[0]", "pairs[1]", "pairs[pairs[0].Item1]")]
    [DataRow("VisualBasic", "vb", "answer += 1", "pairs(0)", "pairs(1)", "pairs(pairs(0).Item1)")]
    [DataRow("FSharp", "fs", "answer <- answer + 1", "pairs.[0]", "pairs.[1]", "pairs.[pairs.[0].Item1]")]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task ArrayExpressionsRetainSourceLanguageAfterAssignment(
        string language,
        string extension,
        string marker,
        string destination,
        string source,
        string dynamicDestination)
    {
        string project = $"Csls.Debugger.Fixtures.{language}";
        string program = LanguageFixtures.GetProgramPath(project, "Debug");
        string sourcePath = Path.Join(FindRepositoryRoot(), "test-assets", project, $"Program.{extension}");
        int breakpointLine = (await File.ReadAllLinesAsync(sourcePath, TestContext.CancellationToken)
            .ConfigureAwait(false))
            .Select(static (line, index) => (Line: line, Number: index + 1))
            .Single(candidate => candidate.Line.Contains(marker, StringComparison.Ordinal)).Number;
        string waitPath = CreateResultsViewSignalPath();
        try
        {
            DapTestClient client = await DapTestClient.CreateAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            await using ConfiguredAsyncDisposable disposal = client.ConfigureAwait(false);
            await InitializeAndLaunchAsync(client, program, waitPath, suppressJitOptimizations: true)
                .ConfigureAwait(false);
            int threadId = await ConfigureBreakpointAsync(client, sourcePath, breakpointLine)
                .ConfigureAwait(false);
            int frameId = await AssertStoppedFrameAsync(client, threadId, sourcePath, breakpointLine)
                .ConfigureAwait(false);

            JsonElement array = await ReadEvaluationAsync(
                client, frameId, "pairs", success: true, TestContext.CancellationToken).ConfigureAwait(false);
            JsonElement[] elements = await ReadVariablesAsync(
                client, array.GetProperty("variablesReference").GetInt32())
                .ConfigureAwait(false);
            Assert.HasCount(2, elements);
            Assert.AreEqual("[0]", elements[0].GetProperty("name").GetString());
            Assert.AreEqual("(0, 142)", elements[0].GetProperty("value").GetString());
            Assert.AreEqual(destination, elements[0].GetProperty("evaluateName").GetString());
            Assert.AreEqual("[1]", elements[1].GetProperty("name").GetString());
            Assert.AreEqual("(151, 152)", elements[1].GetProperty("value").GetString());
            Assert.AreEqual(source, elements[1].GetProperty("evaluateName").GetString());

            string? elementExpression = elements[0].GetProperty("evaluateName").GetString();
            Assert.IsNotNull(elementExpression);
            JsonElement inspected = await ReadEvaluationAsync(
                client, frameId, elementExpression,
                success: true, TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual("(0, 142)", inspected.GetProperty("result").GetString());
            await AssertArrayExpressionChildrenAsync(
                client, frameId, inspected, destination, 0, 142).ConfigureAwait(false);

            JsonElement assigned = await ReadSetExpressionAsync(
                client, frameId, dynamicDestination, source, success: true, TestContext.CancellationToken)
                .ConfigureAwait(false);
            Assert.AreEqual("(151, 152)", assigned.GetProperty("value").GetString());
            await AssertArrayExpressionChildrenAsync(
                client, frameId, assigned, destination, 151, 152).ConfigureAwait(false);

            JsonElement changedField = await ReadSetExpressionAsync(
                client, frameId, $"{destination}.Item1", "161", success: true, TestContext.CancellationToken)
                .ConfigureAwait(false);
            Assert.AreEqual("161", changedField.GetProperty("value").GetString());
            Assert.AreEqual("int", changedField.GetProperty("type").GetString());
            Assert.AreEqual(0, changedField.GetProperty("variablesReference").GetInt32());
            JsonElement changedDestination = await ReadEvaluationAsync(
                client, frameId, destination, success: true, TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual("(161, 152)", changedDestination.GetProperty("result").GetString());
            JsonElement unchangedSource = await ReadEvaluationAsync(
                client, frameId, source, success: true, TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual("(151, 152)", unchangedSource.GetProperty("result").GetString());
            Assert.AreEqual(frameId,
                await AssertStoppedFrameAsync(client, threadId, sourcePath, breakpointLine).ConfigureAwait(false));
            await DisconnectAsync(client).ConfigureAwait(false);
            Assert.AreEqual(string.Empty, client.Diagnostics.ToString());
        }
        finally
        {
            File.Delete(waitPath);
        }
    }

    private async Task AssertArrayExpressionChildrenAsync(
        DapTestClient client,
        int frameId,
        JsonElement value,
        string expression,
        int first,
        int second)
    {
        Assert.AreEqual("(int, int)", value.GetProperty("type").GetString());
        int reference = value.GetProperty("variablesReference").GetInt32();
        Assert.IsGreaterThan(0, reference);
        JsonElement[] children = await ReadVariablesAsync(client, reference)
            .ConfigureAwait(false);
        Assert.HasCount(2, children);
        int[] expectedValues = [first, second];
        foreach ((JsonElement child, int index) in children.Select(static (child, index) => (child, index)))
        {
            string name = $"Item{index + 1}";
            string expectedValue = expectedValues[index].ToString(CultureInfo.InvariantCulture);
            Assert.AreEqual(name, child.GetProperty("name").GetString());
            Assert.AreEqual(expectedValue, child.GetProperty("value").GetString());
            Assert.AreEqual("int", child.GetProperty("type").GetString());
            Assert.AreEqual(0, child.GetProperty("variablesReference").GetInt32());
            string? childExpression = child.GetProperty("evaluateName").GetString();
            Assert.IsNotNull(childExpression);
            Assert.AreEqual($"{expression}.{name}", childExpression);
            JsonElement evaluated = await ReadEvaluationAsync(
                client, frameId, childExpression, success: true, TestContext.CancellationToken)
                .ConfigureAwait(false);
            Assert.AreEqual(expectedValue, evaluated.GetProperty("result").GetString());
            Assert.AreEqual("int", evaluated.GetProperty("type").GetString());
            Assert.AreEqual(0, evaluated.GetProperty("variablesReference").GetInt32());
        }
    }
}
