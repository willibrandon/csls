using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies optional method arguments through a stopped real .NET process.
/// </summary>
[TestClass]
public sealed class DapOptionalArgumentTests : DapTestContext
{
    /// <summary>
    /// Applies loaded declaration defaults and prefers an exact-arity overload.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task OptionalArgumentsUseLoadedMetadataDefaults()
    {
        DapTestClient client = await DapTestClient.CreateAsync(TestContext.CancellationToken).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable cleanup = client.ConfigureAwait(false);
        using DapTestCancellationCapture capture = CaptureProtocolOnCancellation(client);
        int frameId = await StopAtInitializedArraysAsync(client).ConfigureAwait(false);
        const string staticReceiver = "Csls.TestProcessHost.DebuggerDumpArrayFixture";
        foreach ((string expression, string expected) in new[]
        {
            ($"{staticReceiver}.CombineOptionalForDebugger(vector[0])", "414243"),
            ($"{staticReceiver}.CombineOptionalForDebugger(third: vector[2], first: vector[0])", "414243"),
            ($"{staticReceiver}.PreferExactForDebugger(vector[0])", "141"),
            ($"{staticReceiver}.PreferExactForDebugger(vector[0], vector[1])", "4142"),
            ($"{staticReceiver}.OptionalStringForDebugger()", "\"loaded default\""),
            ($"{staticReceiver}.OptionalNullForDebugger()", "1"),
            ($"{staticReceiver}.OptionalEnumForDebugger()", "-1"),
            ($"{staticReceiver}.OptionalFlagsForDebugger()", "3"),
            ($"{staticReceiver}.OptionalWideEnumForDebugger()", "1"),
            ($"{staticReceiver}.OptionalExternalEnumForDebugger()", "3"),
            ($"{staticReceiver}.OptionalDecimalForDebugger()", "1"),
            ($"{staticReceiver}.OptionalDecimalForDebugger(-12.50m)", "1"),
            ($"{staticReceiver}.OptionalDecimalWithoutConstantForDebugger(0)", "1"),
            ($"{staticReceiver}.DecimalFromIntegerForDebugger(vector[0])", "1"),
            ($"{staticReceiver}.OptionalDateTimeForDebugger()", "1"),
            ($"{staticReceiver}.OptionalIntegerWithoutConstantForDebugger()", "1"),
            ($"{staticReceiver}.OptionalBooleanWithoutConstantForDebugger()", "1"),
            ($"{staticReceiver}.OptionalEnumWithoutConstantForDebugger()", "1"),
            ($"{staticReceiver}.OptionalStringWithoutConstantForDebugger()", "1"),
            ($"{staticReceiver}.OptionalDecimalWithoutConstantForDebugger()", "1"),
            ($"{staticReceiver}.OptionalDateTimeWithoutConstantForDebugger()", "1"),
            ($"{staticReceiver}.OptionalGuidWithoutConstantForDebugger()", "1"),
            ($"{staticReceiver}.CompilerOptionalGuidWithoutConstantForDebugger()", "1"),
            ($"{staticReceiver}.OptionalPairWithoutConstantForDebugger()", "1"),
            ($"{staticReceiver}.CompilerOptionalPairWithoutConstantForDebugger()", "1"),
            ($"{staticReceiver}.OptionalNullableWithoutConstantForDebugger()", "1"),
            ($"{staticReceiver}.CompilerOptionalNullableWithoutConstantForDebugger()", "1"),
            ($"{staticReceiver}.OptionalStructWithoutConstantForDebugger()", "1"),
            ($"{staticReceiver}.CompilerOptionalStructWithoutConstantForDebugger()", "1"),
            ("capturedObject.CombineOptionalForDebugger(vector[0])", "4184")
        })
        {
            JsonElement value = await ReadEvaluationAsync(client, frameId, expression, success: true,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(expected, value.GetProperty("result").GetString(), expression);
            using JsonDocument invalidated = await client.ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            AssertEvent(invalidated.RootElement, "invalidated");
        }

        JsonElement created = await ReadEvaluationAsync(client, frameId,
            "new Csls.TestProcessHost.DebuggerFixtureValue(number: vector[0])", success: true,
            TestContext.CancellationToken).ConfigureAwait(false);
        using (JsonDocument invalidated = await client.ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false))
        {
            AssertEvent(invalidated.RootElement, "invalidated");
        }

        JsonElement[] fields = await ReadPageAsync(client,
            created.GetProperty("variablesReference").GetInt32(), 0, 0, "named").ConfigureAwait(false);
        JsonElement text = Assert.ContainsSingle(fields.Where(field =>
            field.GetProperty("name").GetString() == "Text"));
        Assert.AreEqual("\"loaded constructor\"", text.GetProperty("value").GetString());

        JsonElement missingRequired = await ReadEvaluationAsync(client, frameId,
            $"{staticReceiver}.CombineOptionalForDebugger()", success: false,
            TestContext.CancellationToken).ConfigureAwait(false);
        Assert.Contains("No static method", Assert.IsInstanceOfType<string>(
            missingRequired.GetProperty("message").GetString()));
        await DisconnectAsync(client).ConfigureAwait(false);
    }

    /// <summary>
    /// Binds explicit default literals to the selected loaded parameter types.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task ContextualDefaultsFollowCompilerInvocationSemantics()
    {
        DapTestClient client = await DapTestClient.CreateAsync(TestContext.CancellationToken).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable cleanup = client.ConfigureAwait(false);
        using DapTestCancellationCapture capture = CaptureProtocolOnCancellation(client);
        int frameId = await StopAtInitializedArraysAsync(client).ConfigureAwait(false);
        const string receiver = "Csls.TestProcessHost.DebuggerDumpArrayFixture";
        foreach ((string expression, string expected) in new[]
        {
            ($"{receiver}.OptionalIntegerWithoutConstantForDebugger(default)", "1"),
            ($"{receiver}.OptionalBooleanWithoutConstantForDebugger(default)", "1"),
            ($"{receiver}.OptionalEnumWithoutConstantForDebugger(default)", "1"),
            ($"{receiver}.OptionalExternalEnumForDebugger(default)", "0"),
            ($"{receiver}.OptionalNullForDebugger(default)", "1"),
            ($"{receiver}.ContextualObjectForDebugger(default)", "1"),
            ($"{receiver}.OptionalDecimalWithoutConstantForDebugger(default)", "1"),
            ($"{receiver}.OptionalDateTimeWithoutConstantForDebugger(default)", "1"),
            ($"{receiver}.OptionalGuidWithoutConstantForDebugger(default)", "1"),
            ($"{receiver}.OptionalPairWithoutConstantForDebugger(default)", "1"),
            ($"{receiver}.OptionalNullableWithoutConstantForDebugger(default)", "1"),
            ($"{receiver}.OptionalStructWithoutConstantForDebugger(default)", "1"),
            ($"{receiver}.CombineOptionalForDebugger(first: default)", "4243"),
            ($"{receiver}.PreferContextualReferenceForDebugger(default)", "1"),
            ($"{receiver}.CompilerContextualReferenceForDebugger()", "1"),
            ($"{receiver}.PreferContextualNumericForDebugger(default)", "1"),
            ($"{receiver}.CompilerContextualNumericForDebugger()", "1")
        })
        {
            JsonElement result = await ReadEvaluationAsync(client, frameId, expression, success: true,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(expected, result.GetProperty("result").GetString(), expression);
            using JsonDocument invalidated = await client.ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            AssertEvent(invalidated.RootElement, "invalidated");
        }

        JsonElement created = await ReadEvaluationAsync(client, frameId,
            "new Csls.TestProcessHost.DebuggerFixtureValue(number: default)", success: true,
            TestContext.CancellationToken).ConfigureAwait(false);
        using (JsonDocument invalidated = await client.ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false))
        {
            AssertEvent(invalidated.RootElement, "invalidated");
        }

        JsonElement[] fields = await ReadPageAsync(client,
            created.GetProperty("variablesReference").GetInt32(), 0, 0, "named").ConfigureAwait(false);
        JsonElement number = Assert.ContainsSingle(fields.Where(field =>
            field.GetProperty("name").GetString() == "Number"));
        Assert.AreEqual("0", number.GetProperty("value").GetString());

        JsonElement ambiguous = await ReadEvaluationAsync(client, frameId,
            $"{receiver}.AmbiguousContextualDefaultForDebugger(default)", success: false,
            TestContext.CancellationToken).ConfigureAwait(false);
        Assert.Contains("ambiguous", Assert.IsInstanceOfType<string>(
            ambiguous.GetProperty("message").GetString()));
        JsonElement nullToValue = await ReadEvaluationAsync(client, frameId,
            $"{receiver}.OptionalIntegerWithoutConstantForDebugger(null)", success: false,
            TestContext.CancellationToken).ConfigureAwait(false);
        Assert.Contains("No static method", Assert.IsInstanceOfType<string>(
            nullToValue.GetProperty("message").GetString()));
        await DisconnectAsync(client).ConfigureAwait(false);
    }
}
