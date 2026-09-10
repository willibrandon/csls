using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies field-backed getter inspection against real stopped managed values.
/// </summary>
public sealed partial class DapSessionTests
{
    /// <summary>
    /// Reads field-backed getters and rejects mutations while preserving the visible frame and object identity.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task FieldBackedGettersPreserveValuesWithoutTargetExecution()
    {
        string signal = Path.Join(Path.GetTempPath(), $"csls-field-getters-{Guid.NewGuid():N}.signal");
        try
        {
            DapTestClient client = await StartStoppedFixtureAsync(signal, allowImplicitFuncEval: false).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable cleanup = client.ConfigureAwait(false);
            JsonElement frame = await GetFixtureFrameAsync(client).ConfigureAwait(false);
            int frameId = frame.GetProperty("id").GetInt32();
            foreach ((string expression, string expected, string type) in new[]
            {
                ("localObject.NumberProperty", "42", "int"),
                ("localObject.BlockNumberProperty", "42", "int"),
                ("localObject.NumberProperty + 1", "43", "int"),
                ("localObject.TextProperty", "\"answer!\"", "string"),
                ("localObject.PairProperty.Code", "42", "int"),
                ("localObject.PairProperty.Label", "\"answer!\"", "string"),
                ("localObject.RenamedPairProperty.Number", "42", "int"),
                ("localObject.RenamedPairProperty.Text", "\"answer!\"", "string"),
                ("localObject.UnnamedPairProperty.Item1", "42", "int")
            })
            {
                JsonElement result = await ReadEvaluationAsync(client, frameId, expression, success: true,
                    TestContext.CancellationToken).ConfigureAwait(false);
                Assert.AreEqual(expected, result.GetProperty("result").GetString(), expression);
                Assert.AreEqual(type, result.GetProperty("type").GetString(), expression);
            }

            foreach (string property in new[] { "MutatingNumberProperty", "FinallyNumberProperty" })
            {
                _ = await ReadEvaluationAsync(client, frameId, $"localObject.{property}", success: false,
                    TestContext.CancellationToken).ConfigureAwait(false);
                JsonElement unchanged = await ReadEvaluationAsync(client, frameId, "localObject.Number", success: true,
                    TestContext.CancellationToken).ConfigureAwait(false);
                Assert.AreEqual("42", unchanged.GetProperty("result").GetString(), property);
            }

            _ = await ReadEvaluationAsync(client, frameId, "localObject.UnnamedPairProperty.Code", success: false,
                TestContext.CancellationToken).ConfigureAwait(false);

            foreach (string expression in new[] { "localObject.NumberProperty", "localObject.PairProperty.Code" })
            {
                _ = await ReadSetExpressionAsync(client, frameId, expression, "99", success: false,
                    TestContext.CancellationToken).ConfigureAwait(false);
            }
            foreach (string expression in new[] { "localObject.Number", "localObject.Pair.Code" })
            {
                JsonElement unchanged = await ReadEvaluationAsync(client, frameId, expression, success: true,
                    TestContext.CancellationToken).ConfigureAwait(false);
                Assert.AreEqual("42", unchanged.GetProperty("result").GetString(), expression);
            }

            JsonElement currentFrame = await GetFixtureFrameAsync(client).ConfigureAwait(false);
            Assert.AreEqual(frameId, currentFrame.GetProperty("id").GetInt32());
            await ReachImplicitEvaluationBreakpointAsync(client).ConfigureAwait(false);
            currentFrame = await GetFixtureFrameAsync(client).ConfigureAwait(false);
            frameId = currentFrame.GetProperty("id").GetInt32();
            JsonElement identity = await ReadEvaluationAsync(client, frameId,
                "localObject.IsOriginalText(localObject.TextProperty)", success: true,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual("true", identity.GetProperty("result").GetString());
            using (JsonDocument invalidated = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false))
            {
                AssertEvent(invalidated.RootElement, "invalidated");
            }
            await ResumeAndReleaseFixtureAsync(client, signal).ConfigureAwait(false);
            Assert.AreEqual(string.Empty, client.Diagnostics.ToString());
        }
        finally
        {
            File.Delete(signal);
        }
    }
}
