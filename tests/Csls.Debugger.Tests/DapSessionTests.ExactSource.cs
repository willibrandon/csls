using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies source checksum policy and refreshed source descriptors through real DAP sessions.
/// </summary>
public sealed partial class DapSessionTests
{
    /// <summary>
    /// Applies the requested checksum policy, rechecks edited files, and stops at the bound source breakpoint.
    /// </summary>
    /// <param name="requireExactSource">The explicit policy or omission that selects the default.</param>
    [TestMethod]
    [DataRow(null)]
    [DataRow(true)]
    [DataRow(false)]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task SourceChecksumPolicyControlsBindingAndRefresh(bool? requireExactSource)
    {
        DirectoryInfo directory = Directory.CreateTempSubdirectory("csls-exact-source-");
        string mappedSource = Path.Join(directory.FullName, "DebuggerFixture.cs");
        try
        {
            string repository = FindRepositoryRoot();
            string originalPath = Path.Join(repository, "tests", "Csls.TestProcessHost", "DebuggerFixture.cs");
            byte[] original = await File.ReadAllBytesAsync(originalPath, TestContext.CancellationToken).ConfigureAwait(false);
            int line = FindSourceLine(await File.ReadAllLinesAsync(originalPath, TestContext.CancellationToken)
                .ConfigureAwait(false), "Console.Write(announcement);");
            string program = ResolveTestProcessHost();
            using DebugSymbolReader symbols = DebugSymbolReader.TryOpen(program)
                ?? throw new AssertFailedException("The debugger fixture has no symbols.");
            ManagedSymbolDocument document = Assert.ContainsSingle(symbols.GetDocuments().Where(item =>
                item.Path.EndsWith("/DebuggerFixture.cs", StringComparison.Ordinal)));
            Assert.IsNotNull(document.Checksum);
            var mappings = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["/_/"] = repository,
                [document.Path] = mappedSource
            };
            await File.WriteAllBytesAsync(mappedSource, original, TestContext.CancellationToken).ConfigureAwait(false);
            DapTestClient client = await DapTestClient.CreateAsync(TestContext.CancellationToken).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable disposal = client.ConfigureAwait(false);
            (_, int originalProcessId) = await LaunchAtEntryAsync(client, program,
                ["--debugger-fixture", Path.Join(directory.FullName, "continue.signal")],
                sourceFileMap: mappings, requireExactSource: requireExactSource).ConfigureAwait(false);

            int breakpointId = 0;
            foreach (bool matches in new[] { false, true, false })
            {
                await File.WriteAllBytesAsync(mappedSource, original, TestContext.CancellationToken).ConfigureAwait(false);
                if (!matches)
                {
                    await File.AppendAllTextAsync(mappedSource, "\n// Changed after compilation.\n", TestContext.CancellationToken)
                        .ConfigureAwait(false);
                }

                bool binds = matches || requireExactSource == false;
                breakpointId = await AssertSourcePolicyBreakpointAsync(client, mappedSource, line, binds).ConfigureAwait(false);
                JsonElement source = await ReadSourcePolicyDocumentAsync(client).ConfigureAwait(false);
                if (binds)
                {
                    Assert.IsTrue(DebuggerTestPath.AreEquivalent(mappedSource, source.GetProperty("path").GetString()));
                    Assert.AreEqual(matches, source.TryGetProperty("checksums", out _));
                    if (matches)
                    {
                        Assert.IsFalse(source.TryGetProperty("origin", out _));
                    }
                    else
                    {
                        Assert.Contains("unverified local source", source.GetProperty("origin").GetString()!);
                    }
                }
                else
                {
                    Assert.IsFalse(source.TryGetProperty("path", out _));
                }
            }

            if (requireExactSource != false)
            {
                await File.WriteAllBytesAsync(mappedSource, original, TestContext.CancellationToken).ConfigureAwait(false);
                breakpointId = await AssertSourcePolicyBreakpointAsync(client, mappedSource, line, verified: true).ConfigureAwait(false);
            }

            int thread = await ContinueEntryToUserBreakpointAsync(client).ConfigureAwait(false);
            (_, string? actualPath, int actualLine) = await ReadSourceFrameAsync(client, thread, mappedSource,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.IsTrue(DebuggerTestPath.AreEquivalent(mappedSource, actualPath));
            Assert.AreEqual(line, actualLine);
            await AssertStackSourceRefreshAsync(client, thread, mappedSource, original, line, requireExactSource,
                document.SourceLinkUri is not null)
                .ConfigureAwait(false);
            int restart = await client.SendRequestAsync("restart", WriteEmptyObject, TestContext.CancellationToken).ConfigureAwait(false);
            _ = await ReadEntryRestartAsync(client, restart, originalProcessId, breakpointId, "entry").ConfigureAwait(false);
            await AssertProcessExitedAsync(originalProcessId, TestContext.CancellationToken).ConfigureAwait(false);
            thread = await ContinueEntryToUserBreakpointAsync(client).ConfigureAwait(false);
            (_, actualPath, actualLine) = await ReadSourceFrameAsync(client, thread, mappedSource,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.IsTrue(DebuggerTestPath.AreEquivalent(mappedSource, actualPath));
            Assert.AreEqual(line, actualLine);
            await DisconnectStoppedSessionAsync(client).ConfigureAwait(false);
            Assert.AreEqual(0, await client.WaitForExitAsync(TestContext.CancellationToken).ConfigureAwait(false));
        }
        finally
        {
            File.Delete(mappedSource);
            directory.Delete();
        }
    }

    private async Task AssertStackSourceRefreshAsync(DapTestClient client, int threadId,
        string path, byte[] original, int line, bool? requireExactSource, bool hasSourceLink)
    {
        JsonElement baseline = await ReadDeepStackPageAsync(client, threadId, 0, 1).ConfigureAwait(false);
        int frameId = baseline.GetProperty("stackFrames")[0].GetProperty("id").GetInt32();
        foreach (bool matches in new[] { false, true })
        {
            await File.WriteAllBytesAsync(path, original, TestContext.CancellationToken).ConfigureAwait(false);
            if (!matches)
            {
                await File.AppendAllTextAsync(path, "\n// Edited while stopped.\n", TestContext.CancellationToken)
                    .ConfigureAwait(false);
            }

            JsonElement page = await ReadDeepStackPageAsync(client, threadId, 0, 1).ConfigureAwait(false);
            JsonElement frame = page.GetProperty("stackFrames")[0];
            Assert.AreEqual(1, page.GetProperty("stackFrames").GetArrayLength());
            Assert.AreEqual(frameId, frame.GetProperty("id").GetInt32());
            Assert.AreEqual(line, frame.GetProperty("line").GetInt32());
            JsonElement source = frame.GetProperty("source");
            bool readable = matches || requireExactSource == false;
            Assert.AreEqual(readable, source.TryGetProperty("path", out JsonElement sourcePath));
            if (readable)
            {
                Assert.IsTrue(DebuggerTestPath.AreEquivalent(path, sourcePath.GetString()));
            }

            if (matches)
            {
                Assert.IsTrue(source.TryGetProperty("checksums", out _));
                Assert.IsFalse(source.TryGetProperty("origin", out _));
            }
            else
            {
                string expectedOrigin = requireExactSource == false
                    ? "unverified local source (requireExactSource=false)"
                    : hasSourceLink ? "Source Link" : "original source is unavailable or does not match its checksum";
                Assert.AreEqual(expectedOrigin, source.GetProperty("origin").GetString());
                Assert.AreEqual(requireExactSource != false, source.TryGetProperty("checksums", out _));
                if (requireExactSource != false && hasSourceLink)
                {
                    Assert.IsGreaterThan(0, source.GetProperty("sourceReference").GetInt32());
                }
            }
        }
    }

    /// <summary>
    /// Rejects malformed source policy values while retaining a usable initialized adapter.
    /// </summary>
    /// <param name="option">The malformed JSON policy value.</param>
    [TestMethod]
    [DataRow("null")]
    [DataRow("\"false\"")]
    [DataRow("1")]
    [DataRow("{}")]
    [DataRow("[]")]
    [Timeout(30000, CooperativeCancellation = true)]
    public Task LaunchRejectsInvalidRequireExactSource(string option) =>
        AssertInvalidLaunchOptionAsync("requireExactSource", option);

    /// <summary>
    /// Rejects malformed attach source policies before acquiring a target process.
    /// </summary>
    /// <param name="option">The malformed JSON policy value.</param>
    [TestMethod]
    [DataRow("null")]
    [DataRow("\"false\"")]
    [DataRow("1")]
    [DataRow("{}")]
    [DataRow("[]")]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task AttachRejectsInvalidRequireExactSource(string option)
    {
        DapTestClient client = await DapTestClient.CreateAsync(TestContext.CancellationToken).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable disposal = client.ConfigureAwait(false);
        int initializeSequence = await client.SendRequestAsync("initialize", WriteEmptyObject, TestContext.CancellationToken)
            .ConfigureAwait(false);
        using JsonDocument initialize = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false);
        AssertResponse(initialize.RootElement, initializeSequence, "initialize", success: true);
        int sequence = await client.SendRequestAsync("attach", writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("processId", Environment.ProcessId);
            writer.WritePropertyName("requireExactSource");
            writer.WriteRawValue(option);
            writer.WriteEndObject();
        }, TestContext.CancellationToken).ConfigureAwait(false);
        using JsonDocument response = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false);
        AssertResponse(response.RootElement, sequence, "attach", success: false);
        Assert.Contains("requireExactSource", response.RootElement.GetProperty("message").GetString()!);
        await client.CloseProtocolAsync().ConfigureAwait(false);
        Assert.AreEqual(0, await client.WaitForExitAsync(TestContext.CancellationToken).ConfigureAwait(false));
        Assert.AreEqual(string.Empty, client.Diagnostics.ToString());
    }

    private async Task<int> AssertSourcePolicyBreakpointAsync(DapTestClient client, string path, int line, bool verified)
    {
        int sequence = await client.SendRequestAsync("setBreakpoints", writer => WriteSourceBreakpointArguments(writer, path, line),
            TestContext.CancellationToken).ConfigureAwait(false);
        using JsonDocument response = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false);
        AssertResponse(response.RootElement, sequence, "setBreakpoints", success: true);
        JsonElement breakpoint = Assert.ContainsSingle(response.RootElement.GetProperty("body").GetProperty("breakpoints").EnumerateArray());
        Assert.AreEqual(verified, breakpoint.GetProperty("verified").GetBoolean(), breakpoint.GetRawText());
        if (!verified)
        {
            Assert.Contains("source file differs", breakpoint.GetProperty("message").GetString()!);
            Assert.Contains("requireExactSource", breakpoint.GetProperty("message").GetString()!);
        }

        return breakpoint.GetProperty("id").GetInt32();
    }

    private async Task<JsonElement> ReadSourcePolicyDocumentAsync(DapTestClient client)
    {
        int sequence = await client.SendRequestAsync("loadedSources", WriteEmptyObject, TestContext.CancellationToken).ConfigureAwait(false);
        using JsonDocument response = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false);
        AssertResponse(response.RootElement, sequence, "loadedSources", success: true);
        return Assert.ContainsSingle(response.RootElement.GetProperty("body").GetProperty("sources").EnumerateArray()
            .Where(source => source.GetProperty("name").GetString() == "DebuggerFixture.cs")).Clone();
    }
}
