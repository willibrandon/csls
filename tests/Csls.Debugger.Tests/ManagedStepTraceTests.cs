using System.Globalization;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies bounded step diagnostics against actual independently owned files.
/// </summary>
/// <param name="testContext">The framework-owned test context.</param>
[TestClass]
public sealed class ManagedStepTraceTests(TestContext testContext)
{
    private readonly TestContext _testContext = testContext;

    /// <summary>
    /// Retains ordered decisions up to the exact record limit and leaves subsequent writes bounded.
    /// </summary>
    /// <param name="requested">The number of actual diagnostic writes.</param>
    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(255)]
    [DataRow(256)]
    [DataRow(257)]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task RecordsStopAtCapacity(int requested)
    {
        DirectoryInfo directory = Directory.CreateTempSubdirectory("csls-step-trace-");
        string path = Path.Join(directory.FullName, "step.log");
        try
        {
            var trace = new ManagedStepTrace(path);
            for (int index = 0; index < requested; index++)
            {
                trace.Write($"decision={index}");
            }

            string[] lines = await File.ReadAllLinesAsync(path, _testContext.CancellationToken).ConfigureAwait(false);
            Assert.HasCount(Math.Min(256, requested), lines);
            Assert.IsNull(trace.Failure);
            for (int index = 0; index < lines.Length; index++)
            {
                Assert.StartsWith((index + 1).ToString(CultureInfo.InvariantCulture) + ": ", lines[index]);
                Assert.EndsWith(" ms decision=" + index.ToString(CultureInfo.InvariantCulture), lines[index]);
            }
        }
        finally
        {
            File.Delete(path);
            directory.Delete();
        }
    }

    /// <summary>
    /// Bounds an oversized diagnostic message without consuming the following record.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task OversizedMessagePreservesNextRecord()
    {
        DirectoryInfo directory = Directory.CreateTempSubdirectory("csls-step-trace-");
        string path = Path.Join(directory.FullName, "step.log");
        try
        {
            var trace = new ManagedStepTrace(path);
            trace.Write($"{new string('x', 1025)}");
            trace.Write($"next decision");
            string[] lines = await File.ReadAllLinesAsync(path, _testContext.CancellationToken).ConfigureAwait(false);
            Assert.HasCount(2, lines);
            int messageStart = lines[0].IndexOf(" ms ", StringComparison.Ordinal);
            Assert.IsGreaterThan(0, messageStart);
            Assert.AreEqual(new string('x', 1024), lines[0][(messageStart + 4)..]);
            Assert.StartsWith("2: ", lines[1]);
            Assert.EndsWith(" ms next decision", lines[1]);
        }
        finally
        {
            File.Delete(path);
            directory.Delete();
        }
    }

    /// <summary>
    /// Preserves prior evidence when a caller selects an existing trace file.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task ExistingEvidenceIsPreserved()
    {
        DirectoryInfo directory = Directory.CreateTempSubdirectory("csls-step-trace-");
        string path = Path.Join(directory.FullName, "step.log");
        try
        {
            await File.WriteAllTextAsync(path, "existing evidence", _testContext.CancellationToken).ConfigureAwait(false);
            _ = Assert.ThrowsExactly<IOException>(() => new ManagedStepTrace(path));
            Assert.AreEqual("existing evidence",
                await File.ReadAllTextAsync(path, _testContext.CancellationToken).ConfigureAwait(false));
        }
        finally
        {
            File.Delete(path);
            directory.Delete();
        }
    }

    /// <summary>
    /// Retains an actual file failure without throwing through runtime ownership transitions.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public void RemovedDirectoryStopsRecording()
    {
        DirectoryInfo directory = Directory.CreateTempSubdirectory("csls-step-trace-");
        string path = Path.Join(directory.FullName, "step.log");
        try
        {
            var trace = new ManagedStepTrace(path);
            File.Delete(path);
            directory.Delete();
            trace.Write($"unavailable storage");
            _ = Assert.IsInstanceOfType<DirectoryNotFoundException>(trace.Failure);
            directory.Create();
            trace.Write($"later decision");
            Assert.IsFalse(File.Exists(path));
        }
        finally
        {
            File.Delete(path);
            if (Directory.Exists(directory.FullName))
            {
                directory.Delete();
            }
        }
    }
}
