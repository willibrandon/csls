using System.Runtime.CompilerServices;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies entry stopping through real managed launches and DAP streams.
/// </summary>
public sealed partial class DapSessionTests
{
    /// <summary>
    /// Stops before the first authored statement in C#, Visual Basic, and F# entry methods.
    /// </summary>
    [TestMethod]
    [DataRow("CSharp", "cs", "internal static int Main(string[] arguments)", 1, "Debug")]
    [DataRow("VisualBasic", "vb", "Friend Function Main(arguments As String()) As Integer", 0, "Debug")]
    [DataRow("FSharp", "fs", "let mutable answer = Int32.Parse", 0, "Debug")]
    [DataRow("CSharp", "cs", "if (arguments is", 0, "Release")]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task StopAtEntryUsesAuthoredLanguageEntry(
        string language,
        string extension,
        string marker,
        int lineOffset,
        string configuration)
    {
        string project = $"Csls.Debugger.Fixtures.{language}";
        string program = DebuggerLanguageFixtures.GetProgramPath(project, configuration);
        string source = Path.Join(FindRepositoryRoot(), "test-assets", project, $"Program.{extension}");
        int expectedLine = FindSourceLine(await File.ReadAllLinesAsync(source, TestContext.CancellationToken)
            .ConfigureAwait(false), marker) + lineOffset;
        string signal = Path.Join(Path.GetTempPath(), $"csls-entry-{Guid.NewGuid():N}.signal");
        try
        {
            await File.WriteAllTextAsync(signal, "release", TestContext.CancellationToken).ConfigureAwait(false);
            DapTestClient client = await DapTestClient.CreateAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            await using ConfiguredAsyncDisposable disposal = client.ConfigureAwait(false);
            (int threadId, _) = await LaunchAtEntryAsync(client, program, [signal, "41", "entry-result"])
                .ConfigureAwait(false);
            (string name, string? path, int line) = await ReadSourceFrameAsync(
                client, threadId, source, TestContext.CancellationToken).ConfigureAwait(false);
            Assert.IsTrue(DebuggerTestPath.AreEquivalent(source, path), $"Expected source '{source}', received '{path}'.");
            Assert.AreEqual(expectedLine, line);
            Assert.Contains("main", name, StringComparison.OrdinalIgnoreCase);
            await ContinueEntryToExitAsync(client, threadId, "entry-result").ConfigureAwait(false);
        }
        finally
        {
            File.Delete(signal);
        }
    }
}
