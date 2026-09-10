using Csls.EndToEndPerformance;
using System.Runtime.CompilerServices;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies measurement-client failures through a framework-dependent launcher and debugger worker.
/// </summary>
[TestClass]
public sealed class DebuggerMeasurementClientTests : DapTestContext
{
    /// <summary>
    /// Reports the rejected request and server error while awaiting the initialization event.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task RejectedLaunchEndsInitializationWait()
    {
        string directory = Directory.CreateTempSubdirectory("csls-debugger-measurement-client-").FullName;
        try
        {
            string binaries = Path.Join(FindRepositoryRoot(), "artifacts", "bin");
            CopyOutput(Path.Join(binaries, "Csls.App", "debug"), directory);
            string workerDirectory = Path.Join(directory, "workers", "debugger");
            CopyOutput(Path.Join(binaries, "Csls.Debugger.Worker", "debug"), workerDirectory);
            Assert.IsTrue(File.Exists(Path.Join(workerDirectory, "csls-debugger-worker.dll")));
            Assert.IsFalse(File.Exists(Path.Join(workerDirectory, "csls-debugger-worker")));
            Assert.IsFalse(File.Exists(Path.Join(workerDirectory, "csls-debugger-worker.exe")));
            DebuggerMeasurementClient client = await DebuggerMeasurementClient.StartAsync(Path.Join(directory, "csls.dll"))
                .ConfigureAwait(false);
            await using ConfiguredAsyncDisposable cleanup = client.ConfigureAwait(false);
            _ = await client.RequestAsync("initialize", null, TestContext.CancellationToken).ConfigureAwait(false);
            string absentProgram = Path.Join(directory, "absent-target.dll");
            int sequence = await client.SendAsync("launch", writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("program", absentProgram);
                writer.WriteEndObject();
            }, TestContext.CancellationToken).ConfigureAwait(false);
            InvalidDataException exception = await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
                _ = await client.EventAsync("initialized", TestContext.CancellationToken).ConfigureAwait(false))
                .ConfigureAwait(false);
            Assert.Contains("Measured DAP request launch failed", exception.Message);
            Assert.Contains("The launch program does not exist", exception.Message);
            Assert.Contains($"\"request_seq\":{sequence}", exception.Message);
            Assert.AreEqual(0, client.TargetProcessId);
        }
        finally
        {
            await DebuggerTestDirectoryReleaseWaiter.DeleteAsync(directory, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
    }

    private static void CopyOutput(string source, string destination)
    {
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories).Where(file =>
            Path.GetRelativePath(source, file) is not ("csls-debugger-worker" or "csls-debugger-worker.exe")))
        {
            string target = Path.Join(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)
                ?? throw new InvalidOperationException("The packaged file requires a directory."));
            File.Copy(file, target, overwrite: false);
        }
    }
}
