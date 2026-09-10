using Csls.Debugger.Interop;
using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Xml.Linq;

namespace Csls.Debugger.Tests;

/// <summary>
/// Exercises Linux register acquisition and child ownership through real kernel tracing.
/// </summary>
[TestClass]
public sealed class LinuxThreadContextTests
{
    private const string ProbeVariable = "CSLS_LINUX_CONTEXT_TEST";

    /// <summary>
    /// Gets or sets the framework context for cancellation and diagnostics.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Preserves launched-child status while repeatedly acquiring and releasing native registers.
    /// </summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Linux)]
    [Timeout(60000, CooperativeCancellation = true)]
    public Task LaunchedRegisterReadsPreserveExitOwnership() => RunIsolatedAsync(
        nameof(LaunchedRegisterReadsPreserveExitOwnership), owned: true);

    /// <summary>
    /// Preserves runtime child reaping when direct register inspection releases an untracked target.
    /// </summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Linux)]
    [Timeout(60000, CooperativeCancellation = true)]
    public Task DirectRegisterReadsPreserveRuntimeWaits() => RunIsolatedAsync(
        nameof(DirectRegisterReadsPreserveRuntimeWaits), owned: false);

    private async Task RunIsolatedAsync(string method, bool owned)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("These native context tests require Linux.");
        }

        if (Environment.GetEnvironmentVariable(ProbeVariable) == method)
        {
            await ExerciseCaptureAsync(owned, TestContext.CancellationToken).ConfigureAwait(false);
            return;
        }

        string root = DebuggerTestEnvironment.FindRepositoryRoot();
        string results = Directory.CreateTempSubdirectory("csls-native-context-results-").FullName;
        try
        {
            var startInfo = new ProcessStartInfo("dotnet") { WorkingDirectory = root };
            startInfo.ArgumentList.Add("test");
            startInfo.ArgumentList.Add("--root-directory");
            startInfo.ArgumentList.Add(Path.GetDirectoryName(typeof(LinuxThreadContextTests).Assembly.Location)
                ?? throw new AssertFailedException("The native context test module has no directory."));
            startInfo.ArgumentList.Add("--test-modules");
            startInfo.ArgumentList.Add(Path.GetFileName(typeof(LinuxThreadContextTests).Assembly.Location));
            startInfo.ArgumentList.Add("--filter");
            startInfo.ArgumentList.Add($"FullyQualifiedName={typeof(LinuxThreadContextTests).FullName}.{method}");
            startInfo.ArgumentList.Add("--report-trx");
            startInfo.ArgumentList.Add("--results-directory");
            startInfo.ArgumentList.Add(results);
            startInfo.Environment[ProbeVariable] = method;
            string worker = Environment.GetEnvironmentVariable("CSLS_DEBUGGER_WORKER_TEST_PATH")
                ?? Path.Join(root, "artifacts", "bin", "Csls.Debugger.Worker", "debug", "csls-debugger-worker.dll");
            DebuggerWorkerEnvironment.Configure(startInfo, worker);
            (int ExitCode, string Output, string Error) result = await DebuggerTestProcess.RunAsync(
                startInfo, TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(0, result.ExitCode, result.Output + result.Error);
            string report = Assert.ContainsSingle(Directory.GetFiles(results, "*.trx", SearchOption.AllDirectories));
            XNamespace ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
            XElement test = Assert.ContainsSingle(XDocument.Load(report).Descendants(ns + "UnitTestResult"));
            Assert.AreEqual(method, (string?)test.Attribute("testName"));
            Assert.AreEqual("Passed", (string?)test.Attribute("outcome"));
        }
        finally
        {
            Directory.Delete(results, recursive: true);
        }
    }

    [SupportedOSPlatform("linux")]
    private static async Task ExerciseCaptureAsync(bool owned, CancellationToken cancellationToken)
    {
        DebuggerWorkerEnvironment.InitializeCurrentProcess();
        string root = DebuggerTestEnvironment.FindRepositoryRoot();
        string directory = Directory.CreateTempSubdirectory("csls-native-context-target-").FullName;
        try
        {
            string finish = Path.Join(directory, "finish.signal");
            var startInfo = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = directory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            startInfo.ArgumentList.Add(Path.Join(root, "artifacts", "bin", "Csls.TestProcessHost", "debug",
                "csls-test-process-host.dll"));
            startInfo.ArgumentList.Add("--announce-and-spin-until-file");
            startInfo.ArgumentList.Add(finish);
            var streams = new DbgShimStandardStreams();
            await using ConfiguredAsyncDisposable streamCleanup = streams.ConfigureAwait(false);
            (Process process, UnixChildExitMonitor? monitor) = owned
                ? await LaunchOwnedAsync(startInfo, streams, cancellationToken).ConfigureAwait(false)
                : (Process.Start(startInfo) ?? throw new AssertFailedException("The native context target did not start."), null);
            using Process target = process;
            using var outputReader = new StreamReader(owned ? streams.StandardOutput : target.StandardOutput.BaseStream,
                leaveOpen: true);
            using var errorReader = new StreamReader(owned ? streams.StandardError : target.StandardError.BaseStream,
                leaveOpen: true);
            Task<string> error = errorReader.ReadToEndAsync(CancellationToken.None);
            try
            {
                char[] ready = new char[5];
                Assert.AreEqual(ready.Length, await outputReader.ReadBlockAsync(ready, cancellationToken).ConfigureAwait(false));
                Assert.AreEqual("ready", new string(ready));
                using var canceled = new CancellationTokenSource();
                await canceled.CancelAsync().ConfigureAwait(false);
                OperationCanceledException cancellation = Assert.ThrowsExactly<OperationCanceledException>(
                    () => Read(target.Id, canceled.Token));
                Assert.AreEqual(canceled.Token, cancellation.CancellationToken);
                await AssertReleasedAsync(target.Id, cancellationToken).ConfigureAwait(false);

                Win32Exception missing = Assert.ThrowsExactly<Win32Exception>(() => Read(int.MaxValue, cancellationToken));
                Assert.AreEqual(3, missing.NativeErrorCode);
                for (int capture = 0; capture < 32; capture++)
                {
                    byte[] registers = Read(target.Id, cancellationToken);
                    bool arm64 = RuntimeInformation.ProcessArchitecture == Architecture.Arm64;
                    Assert.HasCount(arm64 ? 272 : 216, registers);
                    Assert.AreNotEqual(0UL, BinaryPrimitives.ReadUInt64LittleEndian(registers.AsSpan(arm64 ? 256 : 128)),
                        "The real native instruction pointer must be captured.");
                    Assert.AreNotEqual(0UL, BinaryPrimitives.ReadUInt64LittleEndian(registers.AsSpan(arm64 ? 248 : 152)),
                        "The real native stack pointer must be captured.");
                    if (arm64)
                    {
                        byte[] context = LinuxThreadContext.CreateArm64Context(registers);
                        Assert.HasCount(912, context);
                        Assert.AreEqual(0x00400003U, BinaryPrimitives.ReadUInt32LittleEndian(context));
                        Assert.AreSequenceEqual(registers[..264], context[8..272]);
                        Assert.AreEqual(BinaryPrimitives.ReadUInt32LittleEndian(registers.AsSpan(264)),
                            BinaryPrimitives.ReadUInt32LittleEndian(context.AsSpan(4)));
                    }

                    await AssertReleasedAsync(target.Id, cancellationToken).ConfigureAwait(false);
                    Assert.IsFalse(target.HasExited, "Inspection must preserve target execution.");
                    if (monitor is not null)
                    {
                        Assert.IsFalse(monitor.IsCompleted, "An inspection stop must not complete the exit monitor.");
                    }
                }

                await File.WriteAllTextAsync(finish, "finish", cancellationToken).ConfigureAwait(false);
                if (monitor is not null)
                {
                    Assert.AreEqual(0, await monitor.WaitAsync(cancellationToken).ConfigureAwait(false));
                }
                await target.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                if (!owned)
                {
                    Assert.AreEqual(0, target.ExitCode);
                }
                Assert.IsEmpty(await error.ConfigureAwait(false));
            }
            finally
            {
                if (!target.HasExited)
                {
                    target.Kill(entireProcessTree: true);
                }
                await target.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                _ = await error.ConfigureAwait(false);
            }

            byte[] Read(int threadId, CancellationToken token) => monitor is null
                ? LinuxThreadContext.ReadRegisters(threadId, token)
                : monitor.ReadRegisters(threadId, token);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [SupportedOSPlatform("linux")]
    private static async Task<(Process Target, UnixChildExitMonitor Monitor)> LaunchOwnedAsync(ProcessStartInfo startInfo,
        DbgShimStandardStreams streams, CancellationToken cancellationToken)
    {
        DbgShimLibrary.VerifyPlatformSupport();
        using var environment = DbgShimEnvironmentBlock.Create(DebuggerWorkerEnvironment.CreateTargetEnvironment());
        string commandLine = string.Join(' ', new[] { startInfo.FileName }.Concat(startInfo.ArgumentList)
            .Select(argument => "\"" + argument.Replace("\"", "\\\"", StringComparison.Ordinal) + "\""));
        (uint processId, nint resumeHandle) = await streams.CreateSuspendedAsync(commandLine,
            environment.Pointer, startInfo.WorkingDirectory, cancellationToken).ConfigureAwait(false);
        using var resume = new DbgShimResumeHandle(resumeHandle);
        var monitor = UnixChildExitMonitor.Start(processId);
        var target = Process.GetProcessById(checked((int)processId));
        try
        {
            CorDebugHResult.ThrowIfFailed(DbgShimNativeMethods.ResumeProcess(resumeHandle), "ResumeProcess");
            return (target, monitor);
        }
        catch
        {
            using (target)
            {
                target.Kill(entireProcessTree: true);
                _ = await monitor.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            }
            throw;
        }
    }

    private static async Task AssertReleasedAsync(int processId, CancellationToken cancellationToken)
    {
        string[] status = await File.ReadAllLinesAsync($"/proc/{processId}/status", cancellationToken).ConfigureAwait(false);
        string tracer = Assert.ContainsSingle(status.Where(line => line.StartsWith("TracerPid:", StringComparison.Ordinal)));
        Assert.AreEqual(0, int.Parse(tracer.AsSpan("TracerPid:".Length).Trim(), CultureInfo.InvariantCulture),
            "Native context inspection must release its tracing relationship.");
    }
}
