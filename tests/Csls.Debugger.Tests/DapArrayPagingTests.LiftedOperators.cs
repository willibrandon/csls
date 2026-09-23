using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Inspects bounded pages of large live arrays through the real adapter and runtime.
/// </summary>
public sealed partial class DapArrayPagingTests
{
    private async Task AssertLiftedOperatorsAsync(
        DapTestClient client,
        int frameId,
        string nullableOperatorType)
    {
        foreach ((string expression, string compilerExpression, string expected, string type) in new[]
        {
            ("nullableOperators[0] > nullableOperators[2]",
                $"{nullableOperatorType}.CompilerGreaterThan(" +
                    "nullableOperators[0], nullableOperators[2])", "false", "bool"),
            ("nullableOperators[0] == nullableOperators[2]",
                $"{nullableOperatorType}.CompilerEquals(" +
                    "nullableOperators[0], nullableOperators[2])", "true", "bool"),
            ("nullableOperators[0] != nullableOperators[2]",
                $"{nullableOperatorType}.CompilerNotEquals(" +
                    "nullableOperators[0], nullableOperators[2])", "false", "bool"),
            ("nullableOperators[0] * nullableOperators[2]",
                $"{nullableOperatorType}.CompilerMultiply(" +
                    "nullableOperators[0], nullableOperators[2])", "4109", "int?"),
            ("nullableOperators[0] * nonNullableOperator",
                $"{nullableOperatorType}.CompilerMixedMultiply(" +
                    "nullableOperators[0], nonNullableOperator)", "4109", "int?"),
            ("~nullableOperators[0]",
                $"{nullableOperatorType}.CompilerOnesComplement(nullableOperators[0])",
                "13041", "int?"),
            ("nullableOperators[0] % nullable[0]",
                $"{nullableOperatorType}.CompilerNumericRemainder(" +
                    "nullableOperators[0], nullable[0])", "4137", "int?"),
            ("nullableOperators[0] + null",
                $"{nullableOperatorType}.CompilerAdd(" +
                    "nullableOperators[0], null)", "11241", "int"),
            ("null + nullableOperators[0]",
                $"{nullableOperatorType}.CompilerAdd(" +
                    "null, nullableOperators[0])", "11141", "int")
        })
        {
            foreach (string selectedExpression in new[] { expression, compilerExpression })
            {
                JsonElement selected = await ReadEvaluationAsync(
                    client,
                    frameId,
                    selectedExpression,
                    success: true,
                    TestContext.CancellationToken).ConfigureAwait(false);
                Assert.AreEqual(expected, selected.GetProperty("result").GetString());
                Assert.AreEqual(type, selected.GetProperty("type").GetString());
                if (type.EndsWith('?'))
                {
                    Assert.IsGreaterThan(
                        0, selected.GetProperty("variablesReference").GetInt32());
                }
                using JsonDocument invalidated = await client.ReadMessageAsync(
                    TestContext.CancellationToken).ConfigureAwait(false);
                AssertEvent(invalidated.RootElement, "invalidated");
            }
        }

        foreach ((string expression, string compilerExpression, string expected, string type) in new[]
        {
            ("nullableOperators[1] > nullableOperators[2]",
                $"{nullableOperatorType}.CompilerGreaterThan(" +
                    "nullableOperators[1], nullableOperators[2])", "false", "bool"),
            ("nullableOperators[1] == nullableOperators[2]",
                $"{nullableOperatorType}.CompilerEquals(" +
                    "nullableOperators[1], nullableOperators[2])", "false", "bool"),
            ("nullableOperators[1] != nullableOperators[2]",
                $"{nullableOperatorType}.CompilerNotEquals(" +
                    "nullableOperators[1], nullableOperators[2])", "true", "bool"),
            ("nullableOperators[1] == null",
                $"{nullableOperatorType}.CompilerEquals(" +
                    "nullableOperators[1], null)", "true", "bool"),
            ("nullableOperators[0] * nullableOperators[1]",
                $"{nullableOperatorType}.CompilerMultiply(" +
                    "nullableOperators[0], nullableOperators[1])", "null", "int?"),
            ("nullableOperators[1] * nonNullableOperator",
                $"{nullableOperatorType}.CompilerMixedMultiply(" +
                    "nullableOperators[1], nonNullableOperator)", "null", "int?"),
            ("~nullableOperators[1]",
                $"{nullableOperatorType}.CompilerOnesComplement(nullableOperators[1])",
                "null", "int?"),
            ("nullableOperators[0] % nullable[1]",
                $"{nullableOperatorType}.CompilerNumericRemainder(" +
                    "nullableOperators[0], nullable[1])", "null", "int?"),
            ("nullableOperators[0] * null",
                $"{nullableOperatorType}.CompilerMultiply(" +
                    "nullableOperators[0], null)", "null", "int?"),
            ("null * nullableOperators[0]",
                $"{nullableOperatorType}.CompilerMultiply(" +
                    "null, nullableOperators[0])", "null", "int?")
        })
        {
            JsonElement direct = await ReadEvaluationAsync(
                client,
                frameId,
                expression,
                success: true,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(expected, direct.GetProperty("result").GetString());
            Assert.AreEqual(type, direct.GetProperty("type").GetString());
            JsonElement compiler = await ReadEvaluationAsync(
                client,
                frameId,
                compilerExpression,
                success: true,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(expected, compiler.GetProperty("result").GetString());
            Assert.AreEqual(type, compiler.GetProperty("type").GetString());
            using JsonDocument invalidated = await client.ReadMessageAsync(
                TestContext.CancellationToken).ConfigureAwait(false);
            AssertEvent(invalidated.RootElement, "invalidated");
        }

        int assignment = await client.SendRequestAsync("setExpression", writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("frameId", frameId);
            writer.WriteString("expression", "liftedOperatorResult");
            writer.WriteString("value", "nullableOperators[0] * nullableOperators[2]");
            writer.WriteEndObject();
        }, TestContext.CancellationToken).ConfigureAwait(false);
        using (JsonDocument response = await client.ReadMessageAsync(
            TestContext.CancellationToken).ConfigureAwait(false))
        {
            AssertResponse(response.RootElement, assignment, "setExpression", success: true);
            Assert.AreEqual("4109", response.RootElement.GetProperty("body")
                .GetProperty("value").GetString());
        }
        using (JsonDocument invalidated = await client.ReadMessageAsync(
            TestContext.CancellationToken).ConfigureAwait(false))
        {
            AssertEvent(invalidated.RootElement, "invalidated");
        }
        JsonElement assigned = await ReadEvaluationAsync(
            client,
            frameId,
            "liftedOperatorResult",
            success: true,
            TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual("4109", assigned.GetProperty("result").GetString());
        Assert.AreEqual("int?", assigned.GetProperty("type").GetString());
    }
}
