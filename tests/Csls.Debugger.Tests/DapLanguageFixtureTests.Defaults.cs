using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies source-language default assignment through real compiler-produced runtime storage.
/// </summary>
public sealed partial class DapSessionTests
{
    /// <summary>
    /// Applies Visual Basic Nothing to value and reference storage without executing target code.
    /// </summary>
    /// <param name="setVariable">Whether to assign the scalar local through its container.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task DefaultAssignmentVisualBasicNothingUsesDestinationType(bool setVariable)
    {
        const string Project = "Csls.Debugger.Fixtures.VisualBasic";
        string sourcePath = Path.Join(FindRepositoryRoot(), "test-assets", Project, "Program.vb");
        int line = (await File.ReadAllLinesAsync(sourcePath, TestContext.CancellationToken).ConfigureAwait(false))
            .Select(static (text, index) => (Text: text, Line: index + 1))
            .Single(static candidate => candidate.Text.Contains("answer += 1", StringComparison.Ordinal)).Line;
        string waitPath = CreateResultsViewSignalPath();
        try
        {
            DapTestClient client = await DapTestClient.CreateAsync(TestContext.CancellationToken).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable disposal = client.ConfigureAwait(false);
            await InitializeAndLaunchAsync(client, LanguageFixtures.GetProgramPath(Project, "Debug"), waitPath,
                suppressJitOptimizations: true).ConfigureAwait(false);
            int threadId = await ConfigureBreakpointAsync(client, sourcePath, line).ConfigureAwait(false);
            int frameId = await AssertStoppedFrameAsync(client, threadId, sourcePath, line).ConfigureAwait(false);
            (_, int localsReference) = await ReadFrameScopeReferencesAsync(client, frameId).ConfigureAwait(false);
            (string Name, string Before, string After, string Type)[] values =
            [
                ("answer", "41", "0", "int"),
                ("genericValue._value", "41", "0", "int"),
                ("nullableGenericValue._value", "41", "null", "int?"),
                ("pairs(1)", "(151, 152)", "(0, 0)", "(int, int)"),
                ("arrayGenericValue._value", "{int[0]}", "null", "int[]")
            ];
            foreach ((string name, string before, string after, string type) in values)
            {
                await AssertStructAssignmentEvaluationAsync(client, frameId, name, before, type).ConfigureAwait(false);
                JsonElement assigned = setVariable && name == "answer"
                    ? await ReadSetVariableAsync(client, localsReference, name, "Nothing", success: true,
                        TestContext.CancellationToken).ConfigureAwait(false)
                    : await ReadSetExpressionAsync(client, frameId, name, "(Nothing)", success: true,
                        TestContext.CancellationToken).ConfigureAwait(false);
                Assert.AreEqual(after, assigned.GetProperty("value").GetString(), name);
                Assert.AreEqual(type, assigned.GetProperty("type").GetString(), name);
                await AssertStructAssignmentEvaluationAsync(client, frameId, name, after, type).ConfigureAwait(false);
            }

            JsonElement nullable = await ReadEvaluationAsync(client, frameId, "nullableGenericValue._value",
                success: true, TestContext.CancellationToken).ConfigureAwait(false);
            await AssertStructAssignmentNullableChildrenAsync(client, nullable, hasValue: false).ConfigureAwait(false);
            await AssertStructAssignmentEvaluationAsync(client, frameId, "pairs(0).Item2", "142", "int")
                .ConfigureAwait(false);
            Assert.AreEqual(frameId, await AssertStoppedFrameAsync(client, threadId, sourcePath, line)
                .ConfigureAwait(false));
            await FinishResultsViewSessionAsync(client).ConfigureAwait(false);
        }
        finally
        {
            File.Delete(waitPath);
        }
    }
}
