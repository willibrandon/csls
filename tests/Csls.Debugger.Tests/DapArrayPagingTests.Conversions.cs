using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Inspects bounded pages of large live arrays through the real adapter and runtime.
/// </summary>
public sealed partial class DapArrayPagingTests
{
    /// <summary>
    /// Selects the nearest loaded reference overload and rejects an unrelated parameter type.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task ArrayReferenceOverloadsUseExactLoadedTypes()
    {
        DapTestClient client = await DapTestClient.CreateAsync(TestContext.CancellationToken).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable cleanup = client.ConfigureAwait(false);
        int frameId = await StopAtInitializedArraysAsync(client).ConfigureAwait(false);
        const string receiver = "Csls.TestProcessHost.DebuggerDumpArrayFixture";
        foreach ((string expression, string result) in new[]
        {
            ($"{receiver}.SelectArrayReferenceForDebugger(vector)", "17"),
            ($"{receiver}.SelectArrayReferenceForDebugger((object)vector)", "23"),
            ($"{receiver}.SelectArrayReferenceForDebugger(null)", "17"),
            ($"{receiver}.ReadBoxedIntegerForDebugger(vector[0])", "141"),
            ($"{receiver}.ReadBoxedIntegerForDebugger(41)", "141"),
            ($"{receiver}.ReadBoxedStructForDebugger(" +
                "(Csls.TestProcessHost.DebuggerOptionalStructFixture)boxedNullableStruct)", "41"),
            ($"{receiver}.ReadBoxedStructForDebugger(" +
                "default(Csls.TestProcessHost.DebuggerOptionalStructFixture))", "0"),
            ($"{receiver}.ReadBoxedNullableForDebugger(nullable[0])", "137"),
            ($"{receiver}.ReadBoxedNullableForDebugger(nullable[1])", "-1"),
            ($"{receiver}.ReadBoxedNullableForDebugger(boxedNullableValue as int?)", "147"),
            ($"{receiver}.ReadBoxedNullableForDebugger(boxedNullableEmpty as int?)", "-1"),
            ($"{receiver}.ReadBoxedNullableForDebugger(boxedNullableMismatch as int?)", "-1"),
            ($"{receiver}.CompilerReadBoxedNullableValueForDebugger()", "147"),
            ($"{receiver}.CompilerReadBoxedNullableEmptyForDebugger()", "-1"),
            ($"{receiver}.ReadBoxedStructForDebugger(nullableStructs[0])", "41"),
            ($"{receiver}.ReadBoxedStructForDebugger(nullableStructs[1])", "-1"),
            ($"{receiver}.SelectBoxedIntegerForDebugger(vector[0])", "37"),
            ($"{receiver}.CompilerSelectBoxedIntegerForDebugger()", "37"),
            ($"{receiver}.SelectBoxedIntegerForDebugger(nullable[0])", "37"),
            ($"{receiver}.CompilerSelectBoxedNullableIntegerForDebugger()", "37"),
            ($"{receiver}.SelectBoxedValueTargetForDebugger(vector[0])", "43"),
            ($"{receiver}.CompilerSelectBoxedValueTargetForDebugger()", "43"),
            ($"{receiver}.SelectBoxedValueTargetForDebugger(nullable[0])", "43"),
            ($"{receiver}.CompilerSelectBoxedNullableValueTargetForDebugger()", "43"),
            ("Csls.TestProcessHost.DebuggerImplicitConversionFixture." +
                "GetConversionCountForDebugger()", "0"),
            ("Csls.TestProcessHost.DebuggerImplicitConversionFixture." +
                "SelectForDebugger(implicitConversion)", "141"),
            ("Csls.TestProcessHost.DebuggerImplicitConversionFixture." +
                "GetConversionCountForDebugger()", "1"),
            ("Csls.TestProcessHost.DebuggerImplicitConversionFixture." +
                "CompilerSelectForDebugger(implicitConversion)", "141"),
            ("Csls.TestProcessHost.DebuggerImplicitConversionFixture." +
                "GetConversionCountForDebugger()", "2"),
            ("Csls.TestProcessHost.DebuggerImplicitConversionFixture." +
                "RequireIntegerForDebugger(implicitConversion)", "241"),
            ("Csls.TestProcessHost.DebuggerImplicitConversionFixture." +
                "RequireStringForDebugger(implicitConversion)", "2"),
            ("Csls.TestProcessHost.DebuggerImplicitConversionFixture." +
                "RequireStringForDebugger(emptyImplicitConversion)", "-1"),
            ("Csls.TestProcessHost.DebuggerImplicitConversionFixture." +
                "GetConversionCountForDebugger()", "5"),
            ("Csls.TestProcessHost.DebuggerImplicitConversionFixture." +
                "SelectForDebugger((byte)vector[0])", "141"),
            ("Csls.TestProcessHost.DebuggerImplicitConversionFixture." +
                "CompilerSelectByteForDebugger()", "141"),
            ("Csls.TestProcessHost.DebuggerImplicitConversionFixture." +
                "RequireLongForDebugger(implicitConversion)", "341"),
            ("Csls.TestProcessHost.DebuggerImplicitConversionFixture." +
                "CompilerRequireLongForDebugger(implicitConversion)", "341"),
            ("Csls.TestProcessHost.DebuggerImplicitConversionFixture." +
                "RequireReferenceBaseForDebugger(implicitReferenceConversion)", "341"),
            ("Csls.TestProcessHost.DebuggerImplicitConversionFixture." +
                "CompilerRequireReferenceBaseForDebugger(implicitReferenceConversion)", "341"),
            ("Csls.TestProcessHost.DebuggerImplicitConversionFixture." +
                "RequireBoxedSourceForDebugger(vector[0])", "441"),
            ("Csls.TestProcessHost.DebuggerImplicitConversionFixture." +
                "CompilerRequireBoxedSourceForDebugger(vector[0])", "441"),
            ("Csls.TestProcessHost.DebuggerImplicitConversionFixture." +
                "RequireNullableDestinationForDebugger(populatedLiftedImplicitConversion)", "541"),
            ("Csls.TestProcessHost.DebuggerImplicitConversionFixture." +
                "CompilerRequireNullableDestinationForDebugger(populatedLiftedImplicitConversion)", "541"),
            ("Csls.TestProcessHost.DebuggerImplicitConversionFixture." +
                "RequireNullableDestinationForDebugger(emptyLiftedImplicitConversion)", "-1"),
            ("Csls.TestProcessHost.DebuggerImplicitConversionFixture." +
                "CompilerRequireNullableDestinationForDebugger(emptyLiftedImplicitConversion)", "-1"),
            ("Csls.TestProcessHost.DebuggerImplicitConversionFixture." +
                "RequireStringForDebugger(populatedLiftedImplicitConversion)", "2"),
            ("Csls.TestProcessHost.DebuggerImplicitConversionFixture." +
                "CompilerRequireNullableStringForDebugger(populatedLiftedImplicitConversion)", "2"),
            ("Csls.TestProcessHost.DebuggerImplicitConversionFixture." +
                "RequireStringForDebugger(emptyLiftedImplicitConversion)", "-1"),
            ("Csls.TestProcessHost.DebuggerImplicitConversionFixture." +
                "CompilerRequireNullableStringForDebugger(emptyLiftedImplicitConversion)", "-1"),
            ("(double)implicitConversion", "41.5"),
            ("Csls.TestProcessHost.DebuggerImplicitConversionFixture." +
                "CompilerExplicitDoubleForDebugger(implicitConversion)", "41.5"),
            ("Csls.TestProcessHost.DebuggerImplicitConversionFixture." +
                "GetConversionCountForDebugger()", "19")
        })
        {
            JsonElement value = await ReadEvaluationAsync(client, frameId, expression, success: true,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(result, value.GetProperty("result").GetString());
            using JsonDocument invalidated = await client.ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            AssertEvent(invalidated.RootElement, "invalidated");
        }

        int assignment = await client.SendRequestAsync("setExpression", writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("frameId", frameId);
            writer.WriteString("expression", "explicitConversionResult");
            writer.WriteString("value", "(double)implicitConversion");
            writer.WriteEndObject();
        }, TestContext.CancellationToken).ConfigureAwait(false);
        using (JsonDocument response = await client.ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false))
        {
            AssertResponse(response.RootElement, assignment, "setExpression", success: true);
            Assert.AreEqual("41.5", response.RootElement.GetProperty("body")
                .GetProperty("value").GetString());
        }
        using (JsonDocument invalidated = await client.ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false))
        {
            AssertEvent(invalidated.RootElement, "invalidated");
        }

        JsonElement assigned = await ReadEvaluationAsync(client, frameId,
            "explicitConversionResult", success: true,
            TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual("41.5", assigned.GetProperty("result").GetString());
        JsonElement assignedCount = await ReadEvaluationAsync(client, frameId,
            "Csls.TestProcessHost.DebuggerImplicitConversionFixture.GetConversionCountForDebugger()",
            success: true, TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual("20", assignedCount.GetProperty("result").GetString());
        using (JsonDocument invalidated = await client.ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false))
        {
            AssertEvent(invalidated.RootElement, "invalidated");
        }

        await AssertExplicitConversionAsync(
            client,
            frameId,
            "(Csls.TestProcessHost.DebuggerExplicitConversionDestination)(byte)vector[0]",
            "Csls.TestProcessHost.DebuggerImplicitConversionFixture." +
                "CompilerExplicitWidenedSourceForDebugger((byte)vector[0])",
            "41").ConfigureAwait(false);
        await AssertExplicitConversionAsync(
            client,
            frameId,
            "(Csls.TestProcessHost.DebuggerExplicitConversionGenericDestination<System.IComparable>)vector[0]",
            "Csls.TestProcessHost.DebuggerImplicitConversionFixture." +
                "CompilerExplicitBoxedSourceForDebugger(vector[0])",
            "41").ConfigureAwait(false);
        await AssertExplicitConversionAsync(
            client,
            frameId,
            "(Csls.TestProcessHost.DebuggerImplicitConversionReferenceResultBase)implicitReferenceConversion",
            "Csls.TestProcessHost.DebuggerImplicitConversionFixture." +
                "CompilerExplicitReferenceResultForDebugger(implicitReferenceConversion)",
            "41").ConfigureAwait(false);
        await AssertExplicitConversionAsync(
            client,
            frameId,
            "(Csls.TestProcessHost.DebuggerExplicitNarrowingDestination)vector[0]",
            "Csls.TestProcessHost.DebuggerImplicitConversionFixture." +
                "CompilerExplicitNarrowedSourceForDebugger(vector[0])",
            "41").ConfigureAwait(false);
        await AssertScalarExplicitConversionAsync(
            client,
            frameId,
            "(long)implicitConversion",
            "Csls.TestProcessHost.DebuggerImplicitConversionFixture." +
                "CompilerExplicitLongForDebugger(implicitConversion)",
            "41",
            "long").ConfigureAwait(false);
        await AssertScalarExplicitConversionAsync(
            client,
            frameId,
            "(int)explicitNumericResultSource",
            "Csls.TestProcessHost.DebuggerImplicitConversionFixture." +
                "CompilerExplicitNarrowedResultForDebugger(explicitNumericResultSource)",
            "41",
            "int").ConfigureAwait(false);
        await AssertExplicitConversionAsync(
            client,
            frameId,
            "(Csls.TestProcessHost.DebuggerExplicitReferenceInputDestination)explicitReferenceInput",
            "Csls.TestProcessHost.DebuggerImplicitConversionFixture." +
                "CompilerExplicitReferenceInputForDebugger(explicitReferenceInput)",
            "41").ConfigureAwait(false);
        await AssertExplicitConversionAsync(
            client,
            frameId,
            "(Csls.TestProcessHost.DebuggerExplicitReferenceDowncastResult)" +
                "explicitReferenceResultSource",
            "Csls.TestProcessHost.DebuggerImplicitConversionFixture." +
                "CompilerExplicitReferenceResultDowncastForDebugger(explicitReferenceResultSource)",
            "41").ConfigureAwait(false);
        await AssertExplicitConversionAsync(
            client,
            frameId,
            "(Csls.TestProcessHost.DebuggerExplicitUnboxedInputDestination)boxedIntegerValue",
            "Csls.TestProcessHost.DebuggerImplicitConversionFixture." +
                "CompilerExplicitUnboxedInputForDebugger(boxedIntegerValue)",
            "47").ConfigureAwait(false);
        await AssertExplicitConversionAsync(
            client,
            frameId,
            "(Csls.TestProcessHost.DebuggerExplicitUnboxedStructInputDestination)boxedStructValue",
            "Csls.TestProcessHost.DebuggerImplicitConversionFixture." +
                "CompilerExplicitUnboxedStructInputForDebugger(boxedStructValue)",
            "41").ConfigureAwait(false);

        JsonElement directObjectCast = await ReadEvaluationAsync(
            client,
            frameId,
            "(Csls.TestProcessHost.DebuggerExplicitUnboxedInputDestination)boxedNullableValue",
            success: false,
            TestContext.CancellationToken).ConfigureAwait(false);
        Assert.Contains("cannot be cast", Assert.IsInstanceOfType<string>(
            directObjectCast.GetProperty("message").GetString()),
            StringComparison.OrdinalIgnoreCase);
        JsonElement compilerDirectObjectCast = await ReadEvaluationAsync(
            client,
            frameId,
            "Csls.TestProcessHost.DebuggerImplicitConversionFixture." +
                "CompilerExplicitObjectInputUsesBuiltInForDebugger(boxedNullableValue)",
            success: true,
            TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual("true", compilerDirectObjectCast.GetProperty("result").GetString());
        using (JsonDocument invalidated = await client.ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false))
        {
            AssertEvent(invalidated.RootElement, "invalidated");
        }

        JsonElement nonStandardNumericBridge = await ReadEvaluationAsync(
            client,
            frameId,
            "(Csls.TestProcessHost.DebuggerExplicitDecimalDestination)41.5",
            success: false,
            TestContext.CancellationToken).ConfigureAwait(false);
        Assert.Contains("conversion", Assert.IsInstanceOfType<string>(
            nonStandardNumericBridge.GetProperty("message").GetString()),
            StringComparison.OrdinalIgnoreCase);

        JsonElement invalidReferenceInput = await ReadEvaluationAsync(
            client,
            frameId,
            "(Csls.TestProcessHost.DebuggerExplicitReferenceInputDestination)" +
                "invalidExplicitReferenceInput",
            success: false,
            TestContext.CancellationToken).ConfigureAwait(false);
        Assert.Contains("cannot be cast", Assert.IsInstanceOfType<string>(
            invalidReferenceInput.GetProperty("message").GetString()),
            StringComparison.OrdinalIgnoreCase);

        JsonElement invalidUnboxedInput = await ReadEvaluationAsync(
            client,
            frameId,
            "(Csls.TestProcessHost.DebuggerExplicitUnboxedInputDestination)" +
                "boxedDecimalValue",
            success: false,
            TestContext.CancellationToken).ConfigureAwait(false);
        Assert.Contains("cannot be unboxed", Assert.IsInstanceOfType<string>(
            invalidUnboxedInput.GetProperty("message").GetString()),
            StringComparison.OrdinalIgnoreCase);

        JsonElement invalidReferenceResult = await ReadEvaluationAsync(
            client,
            frameId,
            "(Csls.TestProcessHost.DebuggerExplicitReferenceDowncastResult)" +
                "invalidExplicitReferenceResultSource",
            success: false,
            TestContext.CancellationToken).ConfigureAwait(false);
        Assert.Contains("cannot be cast", Assert.IsInstanceOfType<string>(
            invalidReferenceResult.GetProperty("message").GetString()),
            StringComparison.OrdinalIgnoreCase);
        using (JsonDocument invalidated = await client.ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false))
        {
            AssertEvent(invalidated.RootElement, "invalidated");
        }

        JsonElement finalCount = await ReadEvaluationAsync(client, frameId,
            "Csls.TestProcessHost.DebuggerImplicitConversionFixture.GetConversionCountForDebugger()",
            success: true, TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual("41", finalCount.GetProperty("result").GetString());
        using (JsonDocument invalidated = await client.ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false))
        {
            AssertEvent(invalidated.RootElement, "invalidated");
        }

        JsonElement failure = await ReadEvaluationAsync(client, frameId,
            $"{receiver}.RequireDisposableForDebugger(vector)", success: false,
            TestContext.CancellationToken).ConfigureAwait(false);
        string message = Assert.IsInstanceOfType<string>(failure.GetProperty("message").GetString());
        Assert.Contains("No static method", message);

        JsonElement conversionFailure = await ReadEvaluationAsync(client, frameId,
            "Csls.TestProcessHost.DebuggerImplicitConversionFixture." +
            "RequireStringForDebugger(throwingImplicitConversion)", success: false,
            TestContext.CancellationToken).ConfigureAwait(false);
        Assert.Contains("System.InvalidOperationException", Assert.IsInstanceOfType<string>(
            conversionFailure.GetProperty("message").GetString()));
        using (JsonDocument invalidated = await client.ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false))
        {
            AssertEvent(invalidated.RootElement, "invalidated");
        }
        JsonElement stillStopped = await ReadEvaluationAsync(client, frameId, "vector[0]", success: true,
            TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual("41", stillStopped.GetProperty("result").GetString());
        await DisconnectAsync(client).ConfigureAwait(false);
    }

    private async Task AssertExplicitConversionAsync(
        DapTestClient client,
        int frameId,
        string expression,
        string compilerExpression,
        string expected)
    {
        JsonElement converted = await ReadEvaluationAsync(client, frameId, expression, success: true,
            TestContext.CancellationToken).ConfigureAwait(false);
        int reference = converted.GetProperty("variablesReference").GetInt32();
        Assert.IsGreaterThan(0, reference);
        using (JsonDocument invalidated = await client.ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false))
        {
            AssertEvent(invalidated.RootElement, "invalidated");
        }

        JsonElement[] fields = await ReadPageAsync(
            client, reference, 0, 0, "named").ConfigureAwait(false);
        JsonElement[] numbers = [.. fields.Where(value =>
            string.Equals(value.GetProperty("name").GetString(), "_number", StringComparison.Ordinal))];
        if (numbers.Length == 0)
        {
            JsonElement raw = Assert.ContainsSingle(fields.Where(value =>
                string.Equals(value.GetProperty("name").GetString(), "Raw View", StringComparison.Ordinal)));
            fields = await ReadPageAsync(
                client, raw.GetProperty("variablesReference").GetInt32(), 0, 0, "named").ConfigureAwait(false);
            numbers = [.. fields.Where(value =>
                string.Equals(value.GetProperty("name").GetString(), "_number", StringComparison.Ordinal))];
        }

        JsonElement number = Assert.ContainsSingle(numbers);
        Assert.AreEqual(expected, number.GetProperty("value").GetString());

        JsonElement compiler = await ReadEvaluationAsync(client, frameId, compilerExpression, success: true,
            TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(expected, compiler.GetProperty("result").GetString());
        using JsonDocument compilerInvalidated = await client.ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        AssertEvent(compilerInvalidated.RootElement, "invalidated");
    }

    private async Task AssertScalarExplicitConversionAsync(
        DapTestClient client,
        int frameId,
        string expression,
        string compilerExpression,
        string expected,
        string expectedType)
    {
        JsonElement converted = await ReadEvaluationAsync(client, frameId, expression, success: true,
            TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(expected, converted.GetProperty("result").GetString());
        Assert.AreEqual(expectedType, converted.GetProperty("type").GetString());
        Assert.AreEqual(0, converted.GetProperty("variablesReference").GetInt32());
        using (JsonDocument invalidated = await client.ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false))
        {
            AssertEvent(invalidated.RootElement, "invalidated");
        }

        JsonElement compiler = await ReadEvaluationAsync(client, frameId, compilerExpression, success: true,
            TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(expected, compiler.GetProperty("result").GetString());
        using JsonDocument compilerInvalidated = await client.ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        AssertEvent(compilerInvalidated.RootElement, "invalidated");
    }
}
