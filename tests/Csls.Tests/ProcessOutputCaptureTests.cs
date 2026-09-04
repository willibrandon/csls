using Csls.Support;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;

namespace Csls.Tests;

/// <summary>
/// Verifies command output capture and live forwarding across real process and pipe boundaries.
/// </summary>
[TestClass]
public sealed class ProcessOutputCaptureTests
{
    /// <summary>
    /// Gets the framework-managed cancellation token for each real-process test.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Exposes a flushed partial line before the child exits and captures it exactly after release.
    /// </summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task ForwardsPartialLineBeforeChildExits(bool fullUtf8ByteBuffer)
    {
        string expected = fullUtf8ByteBuffer ? new string('\u03A9', 2048) : "ready";
        string releasePath = Path.Join(Path.GetTempPath(), $"csls-output-{Guid.NewGuid():N}.signal");
        string pipeName = $"csls-progress-{Guid.NewGuid():N}";
        using NamedPipeServerStream pipeReader = CreateProgressReader(pipeName);
        using NamedPipeClientStream pipeWriter = CreateProgressWriter(pipeName);
        await ConnectProgressPipeAsync(pipeReader, pipeWriter).ConfigureAwait(false);
        using (var progressReader = new StreamReader(pipeReader, Encoding.UTF8))
        using (var progressWriter = new StreamWriter(pipeWriter, new UTF8Encoding(false)))
        using (Process child = StartChild(
            "--print-utf8-environment-and-wait-for-file", "CSLS_CAPTURE_OUTPUT", expected, releasePath))
        {
            try
            {
                string[] outputs = await Task.WhenAll(
                    ProcessOutputCapture.ReadAsync(
                        child.StandardOutput.BaseStream, child.StandardOutput.CurrentEncoding,
                        progressWriter, TestContext.CancellationToken),
                    ObserveProgressAndReleaseChildAsync(progressReader, child, expected, releasePath),
                    child.StandardError.ReadToEndAsync(TestContext.CancellationToken)).ConfigureAwait(false);
                Assert.AreEqual(expected, outputs[0]);
                await child.WaitForExitAsync(TestContext.CancellationToken).ConfigureAwait(false);
                Assert.AreEqual(0, child.ExitCode, outputs[2]);
                await progressWriter.DisposeAsync().ConfigureAwait(false);
                Assert.AreEqual(string.Empty, await progressReader.ReadToEndAsync(TestContext.CancellationToken)
                    .ConfigureAwait(false), "Forwarding duplicated or appended text after the readiness marker.");
            }
            finally
            {
                await StopChildAsync(child).ConfigureAwait(false);
                File.Delete(releasePath);
            }
        }
    }

    /// <summary>
    /// Preserves empty, single-character, and multi-buffer output without a progress destination.
    /// </summary>
    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(12288)]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task CaptureOnlyPreservesExactOutput(int size)
    {
        string expected = CreateOutput(size);
        using Process child = StartOutputChild(expected);
        try
        {
            string[] outputs = await Task.WhenAll(
                ProcessOutputCapture.ReadAsync(
                    child.StandardOutput.BaseStream, child.StandardOutput.CurrentEncoding,
                    cancellationToken: TestContext.CancellationToken),
                child.StandardError.ReadToEndAsync(TestContext.CancellationToken)).ConfigureAwait(false);
            await child.WaitForExitAsync(TestContext.CancellationToken).ConfigureAwait(false);

            Assert.AreEqual(0, child.ExitCode, outputs[1]);
            Assert.AreEqual(expected, outputs[0]);
            char[] remainder = new char[1];
            Assert.AreEqual(0, await child.StandardOutput.ReadAsync(
                remainder.AsMemory(), TestContext.CancellationToken).ConfigureAwait(false));
        }
        finally
        {
            await StopChildAsync(child).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Forwards every character without changing capture or adding line endings across read buffers.
    /// </summary>
    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(12288)]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task ForwardingPreservesExactOutput(int size)
    {
        string expected = CreateOutput(size);
        string pipeName = $"csls-progress-{Guid.NewGuid():N}";
        using NamedPipeServerStream pipeReader = CreateProgressReader(pipeName);
        using NamedPipeClientStream pipeWriter = CreateProgressWriter(pipeName);
        await ConnectProgressPipeAsync(pipeReader, pipeWriter).ConfigureAwait(false);
        using (var progressReader = new StreamReader(pipeReader, Encoding.UTF8))
        using (var progressWriter = new StreamWriter(pipeWriter, new UTF8Encoding(false)))
        using (Process child = StartOutputChild(expected))
        {
            try
            {
                string[] outputs = await Task.WhenAll(
                    CaptureAndCloseProgressAsync(child, progressWriter),
                    progressReader.ReadToEndAsync(TestContext.CancellationToken),
                    child.StandardError.ReadToEndAsync(TestContext.CancellationToken)).ConfigureAwait(false);
                await child.WaitForExitAsync(TestContext.CancellationToken).ConfigureAwait(false);

                Assert.AreEqual(0, child.ExitCode, outputs[2]);
                Assert.AreEqual(expected, outputs[0]);
                Assert.AreEqual(expected, outputs[1]);
            }
            finally
            {
                await StopChildAsync(child).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Drains a multi-buffer child stream before reporting a broken or disposed progress destination.
    /// </summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task FailedProgressDestinationStillDrainsChild(bool disposeWriter)
    {
        string pipeName = $"csls-progress-{Guid.NewGuid():N}";
        using NamedPipeServerStream pipeReader = CreateProgressReader(pipeName);
        using NamedPipeClientStream pipeWriter = CreateProgressWriter(pipeName);
        await ConnectProgressPipeAsync(pipeReader, pipeWriter).ConfigureAwait(false);
        using (var progressWriter = new StreamWriter(pipeWriter, new UTF8Encoding(false)))
        using (Process child = StartOutputChild(CreateOutput(12288)))
        {
            try
            {
                if (disposeWriter)
                {
                    await progressWriter.DisposeAsync().ConfigureAwait(false);
                }
                else
                {
                    await pipeReader.DisposeAsync().ConfigureAwait(false);
                }

                Task<string> diagnostics = child.StandardError.ReadToEndAsync(TestContext.CancellationToken);
                await Task.WhenAll(
                    AssertFailedProgressAsync(child, progressWriter, disposeWriter),
                    diagnostics).ConfigureAwait(false);
                await child.WaitForExitAsync(TestContext.CancellationToken).ConfigureAwait(false);
                Assert.AreEqual(0, child.ExitCode, await diagnostics.ConfigureAwait(false));
                char[] remainder = new char[1];
                Assert.AreEqual(0, await child.StandardOutput.ReadAsync(
                    remainder.AsMemory(), TestContext.CancellationToken).ConfigureAwait(false),
                    "A failed progress destination left unread child output.");
            }
            finally
            {
                await StopChildAsync(child).ConfigureAwait(false);
            }
        }
    }

    private static string CreateOutput(int size) => size switch
    {
        0 => string.Empty,
        1 => "\u03A9",
        _ => new string('a', 4095) + "\U0001F642\r\nmiddle\t" + new string('z', 8193) + "\nend"
    };

    private async Task<string> ObserveProgressAndReleaseChildAsync(
        StreamReader reader, Process child, string expected, string releasePath)
    {
        try
        {
            char[] progress = new char[expected.Length];
            int consumed = 0;
            while (consumed < progress.Length)
            {
                int count = await reader.ReadAsync(
                    progress.AsMemory(consumed), TestContext.CancellationToken).ConfigureAwait(false);
                Assert.IsGreaterThan(0, count, "The progress pipe ended before the readiness marker.");
                consumed += count;
            }

            string observed = new(progress);
            Assert.AreEqual(expected, observed);
            Assert.IsFalse(child.HasExited, "Progress must be visible while the child is still waiting.");
            return observed;
        }
        finally
        {
            await File.WriteAllTextAsync(releasePath, "release", TestContext.CancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task<string> CaptureAndCloseProgressAsync(Process child, StreamWriter writer)
    {
        try
        {
            return await ProcessOutputCapture.ReadAsync(
                child.StandardOutput.BaseStream, child.StandardOutput.CurrentEncoding,
                writer, TestContext.CancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await writer.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task AssertFailedProgressAsync(Process child, StreamWriter writer, bool disposedWriter)
    {
        try
        {
            IOException exception = await Assert.ThrowsExactlyAsync<IOException>(
                async () => await ProcessOutputCapture.ReadAsync(
                    child.StandardOutput.BaseStream, child.StandardOutput.CurrentEncoding,
                    writer, TestContext.CancellationToken).ConfigureAwait(false)).ConfigureAwait(false);
            Assert.AreEqual("Forwarding command progress failed.", exception.Message);
            if (disposedWriter)
            {
                Assert.IsInstanceOfType<ObjectDisposedException>(exception.InnerException);
            }
            else
            {
                Assert.IsInstanceOfType<IOException>(exception.InnerException);
            }
        }
        finally
        {
            if (disposedWriter)
            {
                await writer.DisposeAsync().ConfigureAwait(false);
            }
            else
            {
                // Flush on disposal must report the same already-broken destination.
                _ = await Assert.ThrowsExactlyAsync<IOException>(
                    async () => await writer.DisposeAsync().ConfigureAwait(false)).ConfigureAwait(false);
            }
        }
    }

    private static NamedPipeServerStream CreateProgressReader(string pipeName) =>
        new(
            pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

    private static NamedPipeClientStream CreateProgressWriter(string pipeName) =>
        new(
            ".", pipeName, PipeDirection.Out, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

    private async Task ConnectProgressPipeAsync(NamedPipeServerStream reader, NamedPipeClientStream writer)
    {
        await Task.WhenAll(
            reader.WaitForConnectionAsync(TestContext.CancellationToken),
            writer.ConnectAsync(TestContext.CancellationToken)).ConfigureAwait(false);
    }

    private static Process StartOutputChild(string output) => StartChild(
        "--print-utf8-environment", "CSLS_CAPTURE_OUTPUT", output);

    private static Process StartChild(
        string mode, string argument, string? output = null, string? releasePath = null)
    {
        string repositoryRoot = EditorToolResolver.FindRepositoryRoot();
        var startInfo = new ProcessStartInfo
        {
            FileName = EditorToolResolver.ResolveAbsoluteDotNetHost(),
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            StandardOutputEncoding = Encoding.UTF8,
            UseShellExecute = false,
            WorkingDirectory = repositoryRoot
        };
        startInfo.ArgumentList.Add(EditorToolResolver.ResolveTestProcessHost(repositoryRoot));
        startInfo.ArgumentList.Add(mode);
        startInfo.ArgumentList.Add(argument);
        if (releasePath is not null)
        {
            startInfo.ArgumentList.Add(releasePath);
        }

        if (output is not null)
        {
            startInfo.Environment["CSLS_CAPTURE_OUTPUT"] = output;
        }

        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("The output capture test child did not start.");
    }

    private static async Task StopChildAsync(Process child)
    {
        if (!child.HasExited)
        {
            try
            {
                child.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException) when (child.HasExited)
            {
                await child.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                return;
            }
        }

        await child.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
    }
}
