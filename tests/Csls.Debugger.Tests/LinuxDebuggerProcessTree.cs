using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;

namespace Csls.Debugger.Tests;

/// <summary>
/// Observes an exact debugger worker within the test-owned Linux process tree.
/// </summary>
[SupportedOSPlatform("linux")]
internal static class LinuxDebuggerProcessTree
{
    /// <summary>
    /// Suspends an owned worker and observes every native thread in its stopped state.
    /// </summary>
    /// <param name="worker">The independently observed worker whose lifetime the caller owns.</param>
    /// <param name="cancellationToken">Cancels observation while the caller retains cleanup responsibility.</param>
    /// <returns>Completion after the operating system has stopped the worker's threads.</returns>
    internal static async Task SuspendAsync(Process worker, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo("/bin/kill");
        start.ArgumentList.Add("-STOP");
        start.ArgumentList.Add(worker.Id.ToString(CultureInfo.InvariantCulture));
        (int exitCode, string output, string error) = await DebuggerTestProcess.RunAsync(start, cancellationToken)
            .ConfigureAwait(false);
        Assert.AreEqual(0, exitCode, output + error);
        string tasks = Path.Join("/proc", worker.Id.ToString(CultureInfo.InvariantCulture), "task");
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string[] states = await Task.WhenAll(Directory.EnumerateDirectories(tasks)
                .Select(directory => File.ReadAllTextAsync(Path.Join(directory, "status"), cancellationToken)))
                .ConfigureAwait(false);
            if (states.Length != 0 && states.All(state => state.Contains("State:\tT (stopped)", StringComparison.Ordinal)))
            {
                return;
            }
            await Task.Yield();
        }
    }

    /// <summary>
    /// Opens an existing descendant whose command line contains the selected worker assembly.
    /// </summary>
    /// <param name="rootProcessId">The independently owned adapter launcher.</param>
    /// <param name="workerPath">The exact compiled worker assembly path.</param>
    /// <returns>The observed worker process with its operating-system handle open.</returns>
    internal static Process OpenWorker(int rootProcessId, string workerPath)
    {
        var pending = new Queue<int>();
        var visited = new HashSet<int>();
        pending.Enqueue(rootProcessId);
        while (pending.TryDequeue(out int processId))
        {
            if (!visited.Add(processId))
            {
                continue;
            }
            Assert.IsLessThanOrEqualTo(1024, visited.Count);
            string root = Path.Join("/proc", processId.ToString(CultureInfo.InvariantCulture));
            string[] arguments;
            try
            {
                arguments = File.ReadAllText(Path.Join(root, "cmdline")).Split('\0');
            }
            catch (IOException)
            {
                continue;
            }

            if (processId != rootProcessId && arguments.Contains(workerPath, StringComparer.Ordinal))
            {
                var worker = Process.GetProcessById(processId);
                _ = worker.SafeHandle;
                Assert.IsFalse(worker.HasExited);
                return worker;
            }

            foreach (string task in Directory.EnumerateDirectories(Path.Join(root, "task")))
            {
                string children;
                try
                {
                    children = File.ReadAllText(Path.Join(task, "children"));
                }
                catch (IOException)
                {
                    continue;
                }
                foreach (string child in children.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    pending.Enqueue(int.Parse(child, CultureInfo.InvariantCulture));
                }
            }
        }

        throw new InvalidOperationException("The owned adapter tree contains no matching debugger worker.");
    }
}
