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
}
