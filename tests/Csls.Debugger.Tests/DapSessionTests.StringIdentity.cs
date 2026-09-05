using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies physical string references independently of debugger presentation metadata.
/// </summary>
public sealed partial class DapSessionTests
{
    /// <summary>
    /// Binds colliding string field names to their physical values rather than display rows.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task DebuggerDisplayNamesDoNotChangeStringExpressionIdentity()
    {
        string waitPath = Path.Join(
            Path.GetTempPath(), $"csls-string-identity-{Guid.NewGuid():N}.signal");
        try
        {
            DapTestClient client = await StartStoppedFixtureAsync(waitPath).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable disposal = client.ConfigureAwait(false);
            JsonElement frame = await GetFixtureFrameAsync(client).ConfigureAwait(false);
            int frameId = frame.GetProperty("id").GetInt32();
            int scopesSequence = await client.SendRequestAsync(
                "scopes", writer => WriteFrameArguments(writer, frameId),
                TestContext.CancellationToken).ConfigureAwait(false);
            using JsonDocument scopes = await client.ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            AssertResponse(scopes.RootElement, scopesSequence, "scopes", success: true);
            JsonElement localScope = scopes.RootElement.GetProperty("body").GetProperty("scopes")
                .EnumerateArray().Single(scope => scope.GetProperty("name").GetString() == "Locals");
            JsonElement[] locals = await ReadVariablesAsync(
                client, localScope.GetProperty("variablesReference").GetInt32()).ConfigureAwait(false);
            JsonElement instance = Assert.ContainsSingle(locals.Where(local =>
                local.GetProperty("name").GetString() == "localStringIdentity"));
            Assert.AreEqual("123", instance.GetProperty("value").GetString());
            Assert.AreEqual("int", instance.GetProperty("type").GetString());
            Assert.IsGreaterThan(0, instance.GetProperty("variablesReference").GetInt32());
            JsonElement[] fields = await ReadUnproxiedLocalAsync(client, "localStringIdentity")
                .ConfigureAwait(false);
            JsonElement first = FindByEvaluateName(fields, "localStringIdentity._first");
            Assert.AreEqual("_second", first.GetProperty("name").GetString());
            Assert.AreEqual("display-only", first.GetProperty("value").GetString());
            Assert.AreEqual("display-string", first.GetProperty("type").GetString());
            Assert.AreEqual(0, first.GetProperty("variablesReference").GetInt32());
            JsonElement second = FindByEvaluateName(fields, "localStringIdentity._second");
            Assert.AreEqual("_second", second.GetProperty("name").GetString());
            AssertStringIdentityValue(second, "value", "\"second\\tvalue\"");

            await AssertStringIdentityExpressionAsync(
                client, frameId, "localStringIdentity._first", "\"first\\nvalue\"")
                .ConfigureAwait(false);
            await AssertStringIdentityExpressionAsync(
                client, frameId, "localStringIdentity._second", "\"second\\tvalue\"")
                .ConfigureAwait(false);
            await AssertStringIdentityExpressionAsync(
                client, frameId, "localStringIdentity._items[1]", "\"array\\\\value\"")
                .ConfigureAwait(false);
            await AssertStringIdentityExpressionAsync(client, frameId, "localText", "\"answer!\"")
                .ConfigureAwait(false);
            JsonElement invalidArithmetic = await ReadEvaluationAsync(
                client, frameId, "localStringIdentity + 1", success: false,
                TestContext.CancellationToken).ConfigureAwait(false);
            string? message = invalidArithmetic.GetProperty("message").GetString();
            Assert.IsNotNull(message);
            Assert.Contains("Csls.TestProcessHost.StringIdentityFixture", message, StringComparison.Ordinal);
            Assert.Contains("cannot participate in safe primitive evaluation", message, StringComparison.Ordinal);

            await DisconnectStoppedSessionAsync(client).ConfigureAwait(false);
            Assert.AreEqual(0, await client.WaitForExitAsync(TestContext.CancellationToken)
                .ConfigureAwait(false));
            Assert.AreEqual(string.Empty, client.Diagnostics.ToString());
        }
        finally
        {
            File.Delete(waitPath);
        }
    }

    /// <summary>
    /// Assigns existing strings at an unsafe stop without materialization or frame retirement.
    /// </summary>
    [TestMethod]
    [DataRow("_first", "localStringIdentity._second", "\"second\\tvalue\"", "_second", "\"second\\tvalue\"")]
    [DataRow("_second", "localStringIdentity._first", "\"first\\nvalue\"", "_first", "\"first\\nvalue\"")]
    [DataRow("_first", "localStringIdentity._items[1]", "\"array\\\\value\"", "_second", "\"second\\tvalue\"")]
    [DataRow("_second", "localText", "\"answer!\"", "_first", "\"first\\nvalue\"")]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task RawStringReferencesAssignAtUnsafeStops(
        string targetMember,
        string sourceExpression,
        string expectedValue,
        string unchangedMember,
        string unchangedValue)
    {
        string waitPath = Path.Join(
            Path.GetTempPath(), $"csls-string-assignment-{Guid.NewGuid():N}.signal");
        try
        {
            DapTestClient client = await StartStoppedFixtureAsync(waitPath, blockForInspection: true).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable disposal = client.ConfigureAwait(false);
            JsonElement frame = await GetFixtureFrameAsync(client).ConfigureAwait(false);
            int frameId = frame.GetProperty("id").GetInt32();
            string targetExpression = $"localStringIdentity.{targetMember}";
            JsonElement materialization = await ReadSetExpressionAsync(
                client, frameId, targetExpression, "\"new string\"", success: false,
                TestContext.CancellationToken).ConfigureAwait(false);
            string? message = materialization.GetProperty("message").GetString();
            Assert.IsNotNull(message);
            Assert.Contains("garbage-collection-unsafe point", message, StringComparison.Ordinal);

            await AssertStringIdentityExpressionAsync(client, frameId, sourceExpression, expectedValue)
                .ConfigureAwait(false);
            JsonElement assignment = await ReadSetExpressionAsync(
                client, frameId, targetExpression, sourceExpression, success: true,
                TestContext.CancellationToken).ConfigureAwait(false);
            AssertStringIdentityValue(assignment, "value", expectedValue);
            // Reusing the original frame and requiring variables-only invalidation prove no target execution.
            await AssertStringIdentityExpressionAsync(client, frameId, targetExpression, expectedValue)
                .ConfigureAwait(false);
            await AssertStringIdentityExpressionAsync(
                client, frameId, $"localStringIdentity.{unchangedMember}", unchangedValue)
                .ConfigureAwait(false);

            await DisconnectStoppedSessionAsync(client).ConfigureAwait(false);
            Assert.AreEqual(0, await client.WaitForExitAsync(TestContext.CancellationToken)
                .ConfigureAwait(false));
            Assert.AreEqual(string.Empty, client.Diagnostics.ToString());
        }
        finally
        {
            File.Delete(waitPath);
        }
    }

    private async Task AssertStringIdentityExpressionAsync(
        DapTestClient client,
        int frameId,
        string expression,
        string expectedValue)
    {
        JsonElement evaluation = await ReadEvaluationAsync(
            client, frameId, expression, success: true, TestContext.CancellationToken)
            .ConfigureAwait(false);
        AssertStringIdentityValue(evaluation, "result", expectedValue);
    }

    private static void AssertStringIdentityValue(
        JsonElement value,
        string propertyName,
        string expectedValue)
    {
        Assert.AreEqual(expectedValue, value.GetProperty(propertyName).GetString());
        Assert.AreEqual("string", value.GetProperty("type").GetString());
        Assert.AreEqual(0, value.GetProperty("variablesReference").GetInt32());
    }
}
