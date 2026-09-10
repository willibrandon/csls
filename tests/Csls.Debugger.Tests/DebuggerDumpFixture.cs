using Csls.Debugger.Contracts;
using Microsoft.Diagnostics.NETCore.Client;
using System.Diagnostics;

namespace Csls.Debugger.Tests;

/// <summary>
/// Captures an independently running managed fixture and retires it before offline inspection.
/// </summary>
internal sealed class DebuggerDumpFixture : IAsyncDisposable
{
    private readonly string _directory;

    private DebuggerDumpFixture(string directory, string dumpPath, int processId, string programPath, DumpType captureType)
    {
        _directory = directory;
        DumpPath = dumpPath;
        ProcessId = processId;
        ProgramPath = programPath;
        CaptureType = captureType;
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
    /// Gets the independently selected dump-writer policy used to capture this target.
    /// </summary>
    internal DumpType CaptureType { get; }

    /// <summary>
    /// Gets the real dump and its original application binary directory for offline inspection.
    /// </summary>
    internal DebugDumpOpenRequest OpenRequest => new(DumpPath, BinarySearchPaths:
        [Path.GetDirectoryName(ProgramPath) ?? throw new InvalidOperationException("The fixture has no binary directory.")]);

    /// <summary>
    /// Captures a real CoreCLR dump, observes target exit, and transfers ownership of the dump directory.
    /// </summary>
    /// <param name="program">The compiled process-host fixture.</param>
    /// <param name="cancellationToken">Cancels readiness and dump capture.</param>
    /// <param name="captureFrameValues">Whether to capture the synchronous fixture with retained arguments and locals.</param>
    /// <param name="includeHeap">Whether to include the managed heap in the captured dump.</param>
    /// <param name="isolateModule">Whether to copy the target files into the owned fixture directory.</param>
    /// <param name="captureArrayShapes">Whether to retain the array-layout inspection fixture.</param>
    /// <param name="captureType">An explicit capture type for dump storage-contract tests.</param>
    /// <param name="diagnosticContext">An optional destination for capture timings and dump-writer output.</param>
    /// <param name="blockDumpOutput">Whether to occupy the dump path with a directory for native write-failure coverage.</param>
    /// <param name="arguments">Explicit arguments for a compiler fixture that announces readiness through stdout.</param>
    /// <returns>The owned dump from a terminated target.</returns>
    internal static async Task<DebuggerDumpFixture> CreateAsync(string program, CancellationToken cancellationToken,
        bool captureFrameValues = false, bool includeHeap = false, bool isolateModule = false, bool captureArrayShapes = false,
        DumpType? captureType = null, TestContext? diagnosticContext = null, bool blockDumpOutput = false,
        IReadOnlyList<string>? arguments = null)
    {
        long started = Stopwatch.GetTimestamp();
        string directory = Directory.CreateTempSubdirectory("csls-dap-dump-").FullName;
        try
        {
            Log("Created capture directory.");
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
                Log("Copied isolated module files.");
            }

            string dump = Path.Join(directory, "target.dmp");
            if (blockDumpOutput)
            {
                Directory.CreateDirectory(dump);
            }

            var startInfo = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
            {
                WorkingDirectory = directory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            startInfo.ArgumentList.Add(program);
            foreach (string argument in arguments ??
                [captureArrayShapes ? "--debugger-dump-arrays"
                    : captureFrameValues ? "--debugger-dump-fixture" : "--announce-and-spin-until-file",
                    Path.Join(directory, "finish.signal")])
            {
                startInfo.ArgumentList.Add(argument);
            }
            using Process target = Process.Start(startInfo)
                ?? throw new InvalidOperationException("The dump target did not start.");
            Task<string> error = target.StandardError.ReadToEndAsync(CancellationToken.None);
            Task<string>? output = null;
            Exception? captureFailure = null;
            string collectorOutput = string.Empty;
            string collectorError = string.Empty;
            string errorTail = string.Empty;
            string outputTail = string.Empty;
            try
            {
                Log($"Started target {target.Id}.");
                char[] ready = new char[5];
                int length = await target.StandardOutput.ReadBlockAsync(ready, cancellationToken).ConfigureAwait(false);
                Assert.AreEqual(ready.Length, length);
                Assert.AreEqual("ready", new string(ready));
                Log("Target announced readiness.");
                output = target.StandardOutput.ReadToEndAsync(CancellationToken.None);
                Log($"Requesting {captureType ?? (includeHeap ? DumpType.WithHeap : DumpType.Triage)} dump.");
                if (OperatingSystem.IsWindows())
                {
                    int exitCode;
                    (exitCode, collectorOutput, collectorError) = await WindowsDebuggerProcessCapture.CaptureAsync(
                        target, dump, cancellationToken, captureType ?? (includeHeap ? DumpType.WithHeap : DumpType.Triage),
                        diagnosticContext)
                        .ConfigureAwait(false);
                    if (exitCode != 0)
                    {
                        throw new IOException($"The native snapshot collector exited with code {exitCode}.");
                    }
                }
                else if (OperatingSystem.IsMacOS() && captureType == DumpType.Full)
                {
                    await DebuggerMacCoreCapture.CaptureAsync(target.Id, dump, Log, diagnosticContext, cancellationToken)
                        .ConfigureAwait(false);
                }
                else
                {
                    var diagnostics = new DiagnosticsClient(target.Id);
                    await diagnostics.WriteDumpAsync(captureType ?? (includeHeap ? DumpType.WithHeap : DumpType.Triage),
                        dump, logDumpGeneration: false, cancellationToken)
                        .ConfigureAwait(false);
                }
                Assert.IsGreaterThan(0L, new FileInfo(dump).Length);
                Log($"Dump writer completed: {new FileInfo(dump).Length} bytes.");
            }
            catch (Exception exception) when (exception is DiagnosticsClientException or IOException)
            {
                captureFailure = exception;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                if (diagnosticContext is not null)
                {
                    Log(File.Exists(dump) ? $"Partial dump size: {new FileInfo(dump).Length} bytes."
                        : "Dump writer has not created the output file.");
                    await DebuggerProcessDiagnostics.CaptureAsync(target.Id, diagnosticContext).ConfigureAwait(false);
                }
                throw;
            }
            finally
            {
                Log("Retiring target.");
                if (OperatingSystem.IsWindows())
                {
                    await WindowsDebuggerProcessCapture.RetireTargetAsync(target).ConfigureAwait(false);
                    Log("Observed Windows kernel process termination.");
                }
                else
                {
                    if (!target.HasExited)
                    {
                        target.Kill(entireProcessTree: true);
                    }
                    await target.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                }
                Log($"Observed target exit {target.ExitCode}.");
                errorTail = GetTail(collectorError + await error.ConfigureAwait(false));
                LogOutput("stderr", errorTail);
                if (output is not null)
                {
                    outputTail = GetTail(collectorOutput + await output.ConfigureAwait(false));
                    LogOutput("stdout", outputTail);
                }
                Log("Drained target streams.");
            }

            if (captureFailure is not null)
            {
                throw new DebuggerDumpCaptureException(target.Id, dump, outputTail, errorTail, captureFailure);
            }

            Assert.IsTrue(target.HasExited, "Offline inspection must begin after the actual target exits.");
            return new DebuggerDumpFixture(directory, dump, target.Id, program,
                captureType ?? (includeHeap ? DumpType.WithHeap : DumpType.Triage));
        }
        catch
        {
            await DebuggerTestDirectoryReleaseWaiter.DeleteAsync(directory, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            throw;
        }

        void Log(string message) => diagnosticContext?.WriteLine(
            $"Dump capture {Stopwatch.GetElapsedTime(started).TotalMilliseconds:F1} ms: {message}");

        void LogOutput(string stream, string text)
        {
            if (!string.IsNullOrEmpty(text))
            {
                Log($"{stream} tail: {text}");
            }
        }
    }

    private static string GetTail(string text) => text[Math.Max(0, text.Length - 16 * 1024)..];

    /// <summary>
    /// Preserves the failed capture and its application image in the uploaded test-result directory.
    /// </summary>
    /// <param name="context">The failed test's diagnostic destination.</param>
    internal void PreserveFailure(TestContext context)
    {
        try
        {
            string directory = Directory.CreateDirectory(Path.Join(DebuggerTestEnvironment.FindRepositoryRoot(),
                "artifacts", "test-results", $"dump-failure-{Guid.NewGuid():N}")).FullName;
            string dump = Path.Join(directory, "target.dmp");
            File.Copy(DumpPath, dump);
            context.AddResultFile(dump);
            string image = Path.Join(directory, Path.GetFileName(ProgramPath));
            File.Copy(ProgramPath, image);
            context.AddResultFile(image);
            string symbols = Path.ChangeExtension(ProgramPath, ".pdb");
            if (File.Exists(symbols))
            {
                string copiedSymbols = Path.Join(directory, Path.GetFileName(symbols));
                File.Copy(symbols, copiedSymbols);
                context.AddResultFile(copiedSymbols);
            }
            context.WriteLine($"Captured dump inspection failure: {directory}");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            context.WriteLine($"Preserving the failed dump also failed: {exception}");
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => new(
        DebuggerTestDirectoryReleaseWaiter.DeleteAsync(_directory, TimeSpan.FromSeconds(10)));
}
