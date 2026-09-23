using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Inspects bounded pages of large live arrays through the real adapter and runtime.
/// </summary>
public sealed partial class DapArrayPagingTests
{
    private async Task AssertNullPatternsAsync(DapTestClient client, int frameId)
    {
        foreach ((string expression, string oracle) in new[]
        {
            ("boxedNullableEmpty is null", "nullPatternOracle[0]"),
            ("boxedNullableValue is not null", "nullPatternOracle[1]"),
            ("nullable[1] is null", "nullPatternOracle[2]"),
            ("nullable[0] is not null", "nullPatternOracle[3]")
        })
        {
            JsonElement actual = await ReadEvaluationAsync(
                client,
                frameId,
                expression,
                success: true,
                TestContext.CancellationToken).ConfigureAwait(false);
            JsonElement expected = await ReadEvaluationAsync(
                client,
                frameId,
                oracle,
                success: true,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(expected.GetProperty("result").GetString(),
                actual.GetProperty("result").GetString());
            Assert.AreEqual("bool", actual.GetProperty("type").GetString());
        }
    }
}
