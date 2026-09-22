using Csls.Debugger.Contracts;
using Csls.Debugger.Control;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;

namespace Csls.Debugger.Tests;

/// <summary>
/// Exercises the terminal launcher through a real worker, local pipe, and managed child.
/// </summary>
[TestClass]
public sealed class DebuggerTerminalLauncherTests : DapTestContext
{
    /// <summary>
    /// Authenticates a launcher and keeps the child's input and output on its terminal handles.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task AuthenticatedLauncherOwnsChildWithInheritedStandardStreams()
    {
        string root = FindRepositoryRoot();
        string program = ResolveTestProcessHost();
        string worker = Path.Join(root, "artifacts", "bin", "Csls.Debugger.Worker", "debug",
            "csls-debugger-worker.dll");
        Assert.IsTrue(File.Exists(worker), $"The debugger worker was not built: {worker}");
        var options = new DebuggeeLaunchOptions
        {
            Program = program,
            WorkingDirectory = Path.GetDirectoryName(program) ?? root,
            Arguments = ["--debugger-terminal-stdio-fixture"],
            Environment = new Dictionary<string, string?>()
        };
        Dictionary<string, string> environment = await DebuggeeLaunchEnvironment
            .CreateAsync(options, TestContext.CancellationToken).ConfigureAwait(false);
        var instruction = new DebuggerTerminalLaunchInstruction
        {
            Program = options.Program,
            WorkingDirectory = options.WorkingDirectory,
            Arguments = options.Arguments,
            Environment = environment,
            RuntimeHostPath = options.RuntimeHostPath,
            SuspendForDebugging = false
        };

        var server = new DebuggerTerminalLaunchServer();
        await using ConfiguredAsyncDisposable serverCleanup = server.ConfigureAwait(false);
        ProcessStartInfo start = CreateLauncherStart(worker, server.PipeName, server.LaunchSecret);
        using Process launcher = Process.Start(start) ??
            throw new InvalidOperationException("The terminal launcher did not start.");
        using var launchCancellation = CancellationTokenSource
            .CreateLinkedTokenSource(TestContext.CancellationToken);
        try
        {
            Task<int> accepted = server.AcceptAsync(instruction, launchCancellation.Token);
            Task launcherExited = launcher.WaitForExitAsync(TestContext.CancellationToken);
            if (await Task.WhenAny(accepted, launcherExited).ConfigureAwait(false) == launcherExited)
            {
                await launchCancellation.CancelAsync().ConfigureAwait(false);
                throw new InvalidOperationException($"Terminal launcher exited with {launcher.ExitCode}: " +
                    await launcher.StandardError.ReadToEndAsync(TestContext.CancellationToken).ConfigureAwait(false));
            }

            int targetId = await accepted.ConfigureAwait(false);
            Assert.AreNotEqual(launcher.Id, targetId);
            Assert.AreEqual(targetId, server.TargetProcessId);
            using (var target = Process.GetProcessById(targetId))
            {
                Assert.IsFalse(target.HasExited);
            }

            Assert.AreEqual("ready", await launcher.StandardOutput
                .ReadLineAsync(TestContext.CancellationToken).ConfigureAwait(false));
            await launcher.StandardInput.WriteLineAsync("hello").ConfigureAwait(false);
            await launcher.StandardInput.FlushAsync(TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual("echo:hello", await launcher.StandardOutput
                .ReadLineAsync(TestContext.CancellationToken).ConfigureAwait(false));
            Assert.AreEqual(0, await server.ReadExitCodeAsync(TestContext.CancellationToken)
                .ConfigureAwait(false));
            await launcher.WaitForExitAsync(TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(0, launcher.ExitCode);
            Assert.AreEqual(string.Empty, await launcher.StandardError
                .ReadToEndAsync(TestContext.CancellationToken).ConfigureAwait(false));
        }
        finally
        {
            if (!launcher.HasExited)
            {
                launcher.Kill(entireProcessTree: true);
            }

            await launcher.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            TestContext.WriteLine($"Terminal launcher exited with {launcher.ExitCode}: " +
                await launcher.StandardError.ReadToEndAsync(CancellationToken.None).ConfigureAwait(false));
        }
    }

    /// <summary>
    /// Rejects a process that knows the pipe name but lacks this launch's secret.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task WrongSecretCannotStartTerminalTarget()
    {
        string root = FindRepositoryRoot();
        string worker = Path.Join(root, "artifacts", "bin", "Csls.Debugger.Worker", "debug",
            "csls-debugger-worker.dll");
        string program = ResolveTestProcessHost();
        var instruction = new DebuggerTerminalLaunchInstruction
        {
            Program = program,
            WorkingDirectory = Path.GetDirectoryName(program) ?? root,
            Arguments = ["--debugger-terminal-stdio-fixture"],
            Environment = []
        };
        var server = new DebuggerTerminalLaunchServer();
        await using ConfiguredAsyncDisposable serverCleanup = server.ConfigureAwait(false);
        string wrongSecret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        ProcessStartInfo start = CreateLauncherStart(worker, server.PipeName, wrongSecret);
        using Process launcher = Process.Start(start) ??
            throw new InvalidOperationException("The terminal launcher did not start.");
        try
        {
            _ = await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(async () =>
                _ = await server.AcceptAsync(instruction, TestContext.CancellationToken)
                    .ConfigureAwait(false)).ConfigureAwait(false);
            Assert.IsNull(server.TargetProcessId);
            await server.DisposeAsync().ConfigureAwait(false);
            await launcher.WaitForExitAsync(TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(1, launcher.ExitCode);
        }
        finally
        {
            if (!launcher.HasExited)
            {
                launcher.Kill(entireProcessTree: true);
            }

            await launcher.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static ProcessStartInfo CreateLauncherStart(string worker, string pipeName, string secret)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add(worker);
        start.ArgumentList.Add("terminal-launch");
        start.Environment[DebuggerTerminalLauncher.PipeEnvironmentVariable] = pipeName;
        start.Environment[DebuggerTerminalLauncher.SecretEnvironmentVariable] = secret;
        return start;
    }
}
