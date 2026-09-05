using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Compares shared debugger type operations with actual C#, Visual Basic, and F# compiler output.
/// </summary>
public sealed partial class DapSessionTests
{
    /// <summary>
    /// Preserves runtime identity and matches compiled type-test results through each language's cast syntax.
    /// </summary>
    /// <param name="language">The checked-in compiler fixture language.</param>
    [TestMethod]
    [DataRow("CSharp")]
    [DataRow("VisualBasic")]
    [DataRow("FSharp")]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task ReferenceTypeExpressionsMatchLanguageCompilers(string language)
    {
        string project = $"Csls.Debugger.Fixtures.{language}";
        (string extension, string marker) = language switch
        {
            "CSharp" => ("cs", "answer++;"),
            "VisualBasic" => ("vb", "answer += 1"),
            _ => ("fs", "answer <- answer + 1")
        };
        string sourcePath = Path.Join(FindRepositoryRoot(), "test-assets", project, $"Program.{extension}");
        int line = (await File.ReadAllLinesAsync(sourcePath, TestContext.CancellationToken).ConfigureAwait(false))
            .Select(static (text, index) => (Text: text, Line: index + 1))
            .Single(candidate => candidate.Text.Contains(marker, StringComparison.Ordinal)).Line;
        string waitPath = CreateResultsViewSignalPath();
        try
        {
            DapTestClient client = await DapTestClient.CreateAsync(TestContext.CancellationToken).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable disposal = client.ConfigureAwait(false);
            await InitializeAndLaunchAsync(client, DebuggerLanguageFixtures.GetProgramPath(project, "Debug"), waitPath,
                suppressJitOptimizations: true).ConfigureAwait(false);
            int threadId = await ConfigureBreakpointAsync(client, sourcePath, line).ConfigureAwait(false);
            int frameId = await AssertStoppedFrameAsync(client, threadId, sourcePath, line).ConfigureAwait(false);
            string fixtureType = $"{project}.DebuggerFixtureValue";
            string[] typeTests = language switch
            {
                "CSharp" => [$"referenceValue is {fixtureType}", "referenceValue is string", "nullReference is object", "boxedNumber is int"],
                "VisualBasic" => [$"TypeOf referenceValue Is {fixtureType}", "TypeOf referenceValue Is String",
                    "TypeOf nullReference Is Object", "TypeOf boxedNumber Is Integer"],
                _ => [$"referenceValue :? {fixtureType}", "referenceValue :? string", "nullReference :? string", "boxedNumber :? int"]
            };
            for (int index = 0; index < typeTests.Length; index++)
            {
                string oracleExpression = language switch
                {
                    "VisualBasic" => $"typeOracle({index})",
                    "FSharp" => $"typeOracle.[{index}]",
                    _ => $"typeOracle[{index}]"
                };
                JsonElement oracle = await ReadEvaluationAsync(
                    client, frameId, oracleExpression, success: true, TestContext.CancellationToken).ConfigureAwait(false);
                string expected = index is 0 or 3 ? "true" : "false";
                Assert.AreEqual(expected, oracle.GetProperty("result").GetString());
                await AssertStructAssignmentEvaluationAsync(client, frameId, typeTests[index], expected, "bool")
                    .ConfigureAwait(false);
            }

            (string Expression, string Value, string Type)[] casts = language switch
            {
                "CSharp" => [($"(({fixtureType})referenceValue)._number", "41", "int"),
                    ($"(referenceValue as {fixtureType})._number", "41", "int"),
                    ("referenceValue as string", "null", "string"), ("(string)nullReference", "null", "string"),
                    ("(float)answer", "41", "float"), ("(double)(float)answer", "41", "double")],
                "VisualBasic" => [($"DirectCast(referenceValue, {fixtureType})._number", "41", "int"),
                    ($"TryCast(referenceValue, {fixtureType})._number", "41", "int"),
                    ("TryCast(referenceValue, String)", "null", "string"), ("DirectCast(nullReference, String)", "null", "string"),
                    ("TypeOf referenceValue IsNot String", "true", "bool"),
                    ("CSng(answer)", "41", "float"), ("CDbl(CSng(answer))", "41", "double"),
                    ("CStr(\"text\")", "\"text\"", "string")],
                _ => [($"(referenceValue :?> {fixtureType}).storedNumber", "41", "int"),
                    ($"(value :> obj) :? {fixtureType}", "true", "bool"),
                    ("nullReference :?> string", "null", "string"),
                    ("float32 answer", "41", "float"), ("float (float32 answer)", "41", "double")]
            };
            foreach ((string expression, string expected, string type) in casts)
            {
                await AssertStructAssignmentEvaluationAsync(client, frameId, expression, expected, type).ConfigureAwait(false);
            }

            Assert.AreEqual(frameId, await AssertStoppedFrameAsync(client, threadId, sourcePath, line).ConfigureAwait(false));
            await FinishResultsViewSessionAsync(client).ConfigureAwait(false);
        }
        finally
        {
            File.Delete(waitPath);
        }
    }
}
