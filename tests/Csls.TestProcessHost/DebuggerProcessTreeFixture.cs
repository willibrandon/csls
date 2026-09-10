using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;

namespace Csls.TestProcessHost;

/// <summary>
/// Keeps a real two-child process tree alive with one grandchild and independently owned pipes.
/// </summary>
internal static class DebuggerProcessTreeFixture
{
    /// <summary>
    /// Reports every owned identifier before waiting for the test to release the root pipe.
    /// </summary>
    internal static async Task<int> RunRootAsync(string pipeName)
    {
        using var release = new NamedPipeClientStream(".", pipeName, PipeDirection.In);
        await release.ConnectAsync().ConfigureAwait(false);
        using Process branch = StartChild("branch");
        try
        {
            using Process leaf = await Task.Run(() => StartChild("leaf")).ConfigureAwait(false);
            try
            {
                string branchIds = await ReadReadyAsync(branch).ConfigureAwait(false);
                string leafId = await ReadReadyAsync(leaf).ConfigureAwait(false);
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"{Environment.ProcessId},{branchIds},{leafId}"));
                byte[] signal = new byte[1];
                await release.ReadExactlyAsync(signal).ConfigureAwait(false);
                return 0;
            }
            finally
            {
                await EndChildAsync(leaf).ConfigureAwait(false);
            }
        }
        finally
        {
            await EndChildAsync(branch).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Reports live descendants that retain their own anonymous pipe when their parent closes its standard streams.
    /// </summary>
    internal static async Task<int> RunChildAsync(string kind)
    {
        using var lifetime = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.None);
        byte[] signal = new byte[1];
        if (kind == "leaf")
        {
            Console.WriteLine(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
            await lifetime.ReadExactlyAsync(signal).ConfigureAwait(false);
            return 0;
        }
        if (kind != "branch")
        {
            throw new ArgumentException("The process tree member must be branch or leaf.", nameof(kind));
        }
        using Process child = StartChild("leaf");
        try
        {
            string childId = await ReadReadyAsync(child).ConfigureAwait(false);
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{Environment.ProcessId},{childId}"));
            await lifetime.ReadExactlyAsync(signal).ConfigureAwait(false);
            return 0;
        }
        finally
        {
            await EndChildAsync(child).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Transfers a live child's ownership to the test before exiting with the child's inherited output handles open.
    /// </summary>
    internal static async Task<int> RunInheritedOutputRootAsync(string rootPipe, string childPipe)
    {
        using var control = new NamedPipeClientStream(".", rootPipe, PipeDirection.InOut);
        await control.ConnectAsync().ConfigureAwait(false);
        var start = new ProcessStartInfo(Environment.ProcessPath
            ?? throw new InvalidOperationException("The fixture host is unavailable."))
        { UseShellExecute = false };
        start.ArgumentList.Add(typeof(DebuggerProcessTreeFixture).Assembly.Location);
        start.ArgumentList.Add("--debugger-inherited-output-child");
        start.ArgumentList.Add(childPipe);
        using Process child = Process.Start(start) ?? throw new InvalidOperationException("The output owner did not start.");
        bool transferred = false;
        try
        {
            using var identities = new StreamWriter(control, leaveOpen: true) { AutoFlush = true };
            await identities.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
                $"{Environment.ProcessId},{child.Id}")).ConfigureAwait(false);
            byte[] release = new byte[1];
            await control.ReadExactlyAsync(release).ConfigureAwait(false);
            transferred = true;
            await Console.Out.WriteLineAsync(rootPipe).ConfigureAwait(false);
            await Console.Error.WriteLineAsync(rootPipe).ConfigureAwait(false);
            return 0;
        }
        finally
        {
            if (!transferred)
            {
                await EndChildAsync(child).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Retains the inherited standard streams until the owning test releases its independent pipe.
    /// </summary>
    internal static async Task<int> RunInheritedOutputChildAsync(string pipeName)
    {
        using var release = new NamedPipeClientStream(".", pipeName, PipeDirection.In);
        await release.ConnectAsync().ConfigureAwait(false);
        await Console.Out.WriteLineAsync(pipeName).ConfigureAwait(false);
        await Console.Error.WriteLineAsync(pipeName).ConfigureAwait(false);
        byte[] signal = new byte[1];
        await release.ReadExactlyAsync(signal).ConfigureAwait(false);
        return 0;
    }

    private static Process StartChild(string kind)
    {
        var startInfo = new ProcessStartInfo(Environment.ProcessPath
            ?? throw new InvalidOperationException("The test fixture executable is unavailable."))
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add(typeof(DebuggerProcessTreeFixture).Assembly.Location);
        startInfo.ArgumentList.Add("--debugger-process-tree-child");
        startInfo.ArgumentList.Add(kind);
        return Process.Start(startInfo) ?? throw new InvalidOperationException("The process tree child did not start.");
    }

    private static async Task<string> ReadReadyAsync(Process process) =>
        await process.StandardOutput.ReadLineAsync().ConfigureAwait(false)
        ?? throw new InvalidOperationException($"Process {process.Id} ended before reporting readiness.");

    private static async Task EndChildAsync(Process process)
    {
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
        }
        await process.WaitForExitAsync().ConfigureAwait(false);
    }
}
