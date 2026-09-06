using Microsoft.Diagnostics.NETCore.Client;
using System.Diagnostics;

namespace Csls.Debugger.Tests;

/// <summary>
/// Captures an independently running managed fixture and retires it before offline inspection.
/// </summary>
internal sealed class DebuggerDumpFixture : IAsyncDisposable
{
    private readonly string _directory;

    private DebuggerDumpFixture(string directory, string dumpPath, int processId, string programPath)
    {
        _directory = directory;
        DumpPath = dumpPath;
        ProcessId = processId;
        ProgramPath = programPath;
    }

    /// <summary>
    /// Gets the absolute path of the captured dump.
    /// </summary>
    internal string DumpPath { get; }

    /// <summary>
    /// Gets the historical identifier of the independently observed terminated target.
    /// </summary>
    internal int ProcessId { get; }

    /// <summary>
    /// Gets the exact assembly path used to start the captured target.
    /// </summary>
    internal string ProgramPath { get; }

    /// <summary>
    /// Captures a real CoreCLR dump, observes target exit, and transfers ownership of the dump directory.
    /// </summary>
    /// <param name="program">The compiled process-host fixture.</param>
    /// <param name="cancellationToken">Cancels readiness and dump capture.</param>
    /// <param name="captureFrameValues">Whether to capture the synchronous fixture with retained arguments and locals.</param>
    /// <param name="includeHeap">Whether to include the managed heap in the captured dump.</param>
    /// <param name="isolateModule">Whether to copy the target files into the owned fixture directory.</param>
    /// <param name="captureArrayShapes">Whether to retain the array-layout inspection fixture.</param>
    /// <returns>The owned dump from a terminated target.</returns>
    internal static async Task<DebuggerDumpFixture> CreateAsync(string program, CancellationToken cancellationToken,
        bool captureFrameValues = false, bool includeHeap = false, bool isolateModule = false, bool captureArrayShapes = false)
    {
        string directory = Directory.CreateTempSubdirectory("csls-dap-dump-").FullName;
        try
        {
            if (isolateModule)
            {
                string sourceDirectory = Path.GetDirectoryName(program)
                    ?? throw new InvalidOperationException("The fixture assembly has no directory.");
                string moduleDirectory = Directory.CreateDirectory(Path.Join(directory, "module")).FullName;
                foreach (string file in Directory.EnumerateFiles(sourceDirectory))
                {
                    File.Copy(file, Path.Join(moduleDirectory, Path.GetFileName(file)));
                }
                program = Path.Join(moduleDirectory, Path.GetFileName(program));
            }

            string dump = Path.Join(directory, "target.dmp");
            var startInfo = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
            {
                WorkingDirectory = directory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            startInfo.ArgumentList.Add(program);
            startInfo.ArgumentList.Add(captureArrayShapes ? "--debugger-dump-arrays"
                : captureFrameValues ? "--debugger-fixture" : "--announce-and-spin-until-file");
            startInfo.ArgumentList.Add(Path.Join(directory, "finish.signal"));
            using Process target = Process.Start(startInfo)
                ?? throw new InvalidOperationException("The dump target did not start.");
            Task<string> error = target.StandardError.ReadToEndAsync(CancellationToken.None);
            Task<string>? output = null;
            try
            {
                char[] ready = new char[5];
                int length = await target.StandardOutput.ReadBlockAsync(ready, cancellationToken).ConfigureAwait(false);
                Assert.AreEqual(ready.Length, length);
                Assert.AreEqual("ready", new string(ready));
                output = target.StandardOutput.ReadToEndAsync(CancellationToken.None);
                var diagnostics = new DiagnosticsClient(target.Id);
                await diagnostics.WriteDumpAsync(includeHeap ? DumpType.WithHeap : DumpType.Triage,
                    dump, logDumpGeneration: false, cancellationToken)
                    .ConfigureAwait(false);
                Assert.IsGreaterThan(0L, new FileInfo(dump).Length);
            }
            finally
            {
                if (!target.HasExited)
                {
                    target.Kill(entireProcessTree: true);
                }
                await target.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                _ = await error.ConfigureAwait(false);
                if (output is not null)
                {
                    _ = await output.ConfigureAwait(false);
                }
            }

            Assert.IsTrue(target.HasExited, "Offline inspection must begin after the actual target exits.");
            return new DebuggerDumpFixture(directory, dump, target.Id, program);
        }
        catch
        {
            await DebuggerTestDirectoryReleaseWaiter.DeleteAsync(directory, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => new(
        DebuggerTestDirectoryReleaseWaiter.DeleteAsync(_directory, TimeSpan.FromSeconds(10)));
}
