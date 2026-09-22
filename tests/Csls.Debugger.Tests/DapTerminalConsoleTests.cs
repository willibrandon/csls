using Csls.Debugger.Control;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Exercises managed terminal launch through real DAP pipes and an interactive child.
/// </summary>
[TestClass]
public sealed class DapTerminalConsoleTests : DapTestContext
{
    /// <summary>
    /// Settles both launch requests when the client refuses to create a terminal.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task RefusedTerminalLaunchFailsConfigurationAndLaunch()
    {
        DapTestClient client = await DapTestClient.CreateAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable clientDisposal = client.ConfigureAwait(false);
        int initialize = await client.SendInitializeRequestAsync(TestContext.CancellationToken,
            writeProperties: writer => writer.WriteBoolean("supportsRunInTerminalRequest", true))
            .ConfigureAwait(false);
        using (JsonDocument response = await client.ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false))
        {
            AssertResponse(response.RootElement, initialize, "initialize", success: true);
        }

        int launch = await client.SendRequestAsync("launch", writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("program", ResolveTestProcessHost());
            writer.WriteString("console", "integratedTerminal");
            writer.WriteEndObject();
        }, TestContext.CancellationToken).ConfigureAwait(false);
        using (JsonDocument initialized = await client.ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false))
        {
            AssertEvent(initialized.RootElement, "initialized");
        }

        int configuration = await client.SendRequestAsync(
            "configurationDone", WriteEmptyObject, TestContext.CancellationToken)
            .ConfigureAwait(false);
        using (JsonDocument reverseRequest = await client.ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false))
        {
            JsonElement message = reverseRequest.RootElement;
            Assert.AreEqual("runInTerminal", message.GetProperty("command").GetString());
            string refusal = string.Create(CultureInfo.InvariantCulture,
                $"{{\"seq\":100,\"type\":\"response\",\"request_seq\":{message.GetProperty("seq").GetInt32()},\"command\":\"runInTerminal\",\"success\":false,\"message\":\"terminal unavailable\"}}");
            await client.SendFrameAsync(CreateFrame(refusal), fragment: false,
                TestContext.CancellationToken).ConfigureAwait(false);
        }

        using (JsonDocument response = await client.ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false))
        {
            AssertResponse(response.RootElement, configuration, "configurationDone", success: false);
            Assert.Contains("terminal unavailable", response.RootElement.GetProperty("message").GetString() ?? string.Empty,
                StringComparison.Ordinal);
        }
        using (JsonDocument response = await client.ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false))
        {
            AssertResponse(response.RootElement, launch, "launch", success: false);
        }
    }

    /// <summary>
    /// Rejects an unauthenticated launcher without waiting for an unanswered editor reply.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task UnauthenticatedTerminalLauncherFailsPromptly()
    {
        DapTestClient client = await DapTestClient.CreateAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable clientDisposal = client.ConfigureAwait(false);
        int initialize = await client.SendInitializeRequestAsync(TestContext.CancellationToken,
            writeProperties: writer => writer.WriteBoolean("supportsRunInTerminalRequest", true))
            .ConfigureAwait(false);
        using (JsonDocument response = await client.ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false))
        {
            AssertResponse(response.RootElement, initialize, "initialize", success: true);
        }

        int launch = await client.SendRequestAsync("launch", writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("program", ResolveTestProcessHost());
            writer.WriteString("console", "integratedTerminal");
            writer.WriteEndObject();
        }, TestContext.CancellationToken).ConfigureAwait(false);
        using (JsonDocument initialized = await client.ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false))
        {
            AssertEvent(initialized.RootElement, "initialized");
        }

        int configuration = await client.SendRequestAsync(
            "configurationDone", WriteEmptyObject, TestContext.CancellationToken)
            .ConfigureAwait(false);
        using Process launcher = await StartUnauthenticatedLauncherAsync(client).ConfigureAwait(false);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        using (JsonDocument response = await client.ReadMessageAsync(deadline.Token)
            .ConfigureAwait(false))
        {
            AssertResponse(response.RootElement, configuration, "configurationDone", success: false);
            Assert.Contains("authenticate", response.RootElement.GetProperty("message").GetString() ?? string.Empty,
                StringComparison.Ordinal);
        }
        using (JsonDocument response = await client.ReadMessageAsync(deadline.Token)
            .ConfigureAwait(false))
        {
            AssertResponse(response.RootElement, launch, "launch", success: false);
        }

        await launcher.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
        Assert.AreNotEqual(0, launcher.ExitCode);
    }

    /// <summary>
    /// Ends the debugger target when its terminal launcher closes unexpectedly.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task ClosingTerminalLauncherRetiresManagedTarget()
    {
        DapTestClient client = await DapTestClient.CreateAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable clientDisposal = client.ConfigureAwait(false);
        using var launcher = new Process();
        bool launcherStarted = false;
        try
        {
            int initialize = await client.SendInitializeRequestAsync(TestContext.CancellationToken,
                writeProperties: writer => writer.WriteBoolean("supportsRunInTerminalRequest", true))
                .ConfigureAwait(false);
            using (JsonDocument response = await client.ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false))
            {
                AssertResponse(response.RootElement, initialize, "initialize", success: true);
            }

            int launch = await client.SendRequestAsync("launch", writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("program", ResolveTestProcessHost());
                writer.WriteString("console", "integratedTerminal");
                writer.WriteStartArray("args");
                writer.WriteStringValue("--debugger-terminal-stdio-fixture");
                writer.WriteEndArray();
                writer.WriteEndObject();
            }, TestContext.CancellationToken).ConfigureAwait(false);
            using (JsonDocument initialized = await client.ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false))
            {
                AssertEvent(initialized.RootElement, "initialized");
            }

            int configuration = await client.SendRequestAsync(
                "configurationDone", WriteEmptyObject, TestContext.CancellationToken)
                .ConfigureAwait(false);
            using (JsonDocument reverseRequest = await client.ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false))
            {
                JsonElement message = reverseRequest.RootElement;
                launcher.StartInfo = CreateTerminalStart(message.GetProperty("arguments"));
                launcherStarted = launcher.Start();
                Assert.IsTrue(launcherStarted);
                string response = string.Create(CultureInfo.InvariantCulture,
                    $"{{\"seq\":100,\"type\":\"response\",\"request_seq\":{message.GetProperty("seq").GetInt32()}," +
                    $"\"command\":\"runInTerminal\",\"success\":true," +
                    $"\"body\":{{\"shellProcessId\":{launcher.Id}}}}}");
                await client.SendFrameAsync(CreateFrame(response), fragment: false,
                    TestContext.CancellationToken).ConfigureAwait(false);
            }

            using (JsonDocument response = await client.ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false))
            {
                AssertResponse(response.RootElement, configuration, "configurationDone", success: true);
            }
            using (JsonDocument response = await client.ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false))
            {
                AssertResponse(response.RootElement, launch, "launch", success: true);
            }
            int targetId;
            using (JsonDocument process = await client.ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false))
            {
                AssertEvent(process.RootElement, "process");
                targetId = process.RootElement.GetProperty("body")
                    .GetProperty("systemProcessId").GetInt32();
            }
            Assert.AreEqual("ready", await launcher.StandardOutput
                .ReadLineAsync(TestContext.CancellationToken).ConfigureAwait(false));

            using var target = Process.GetProcessById(targetId);
            launcher.Kill(entireProcessTree: false);
            await launcher.WaitForExitAsync(TestContext.CancellationToken).ConfigureAwait(false);
            using (JsonDocument exited = await client.ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false))
            {
                AssertEvent(exited.RootElement, "exited");
                Assert.AreEqual(-1, exited.RootElement.GetProperty("body")
                    .GetProperty("exitCode").GetInt32());
            }
            using (JsonDocument terminated = await client.ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false))
            {
                AssertEvent(terminated.RootElement, "terminated");
            }
            Assert.IsTrue(target.HasExited);
        }
        finally
        {
            if (launcherStarted)
            {
                if (!launcher.HasExited)
                {
                    launcher.Kill(entireProcessTree: true);
                }

                await launcher.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                TestContext.WriteLine(await launcher.StandardError
                    .ReadToEndAsync(CancellationToken.None).ConfigureAwait(false));
            }

            TestContext.WriteLine(client.ProtocolTranscript);
            TestContext.WriteLine(client.Diagnostics.ToString());
        }
    }

    /// <summary>
    /// Delivers a fast no-debug target's terminal output and exact exit code.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task NoDebugTerminalReportsFastTargetExit()
    {
        DapTestClient client = await DapTestClient.CreateAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable clientDisposal = client.ConfigureAwait(false);
        using var launcher = new Process();
        bool launcherStarted = false;
        try
        {
            int initialize = await client.SendInitializeRequestAsync(TestContext.CancellationToken,
                writeProperties: writer => writer.WriteBoolean("supportsRunInTerminalRequest", true))
                .ConfigureAwait(false);
            using (JsonDocument response = await client.ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false))
            {
                AssertResponse(response.RootElement, initialize, "initialize", success: true);
            }

            int launch = await client.SendRequestAsync("launch", writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("program", ResolveTestProcessHost());
                writer.WriteString("console", "integratedTerminal");
                writer.WriteBoolean("noDebug", true);
                writer.WriteStartArray("args");
                writer.WriteStringValue("--print-environment-and-exit");
                writer.WriteStringValue("CSLS_TERMINAL_TEST_VALUE");
                writer.WriteStringValue("23");
                writer.WriteEndArray();
                writer.WriteStartObject("env");
                writer.WriteString("CSLS_TERMINAL_TEST_VALUE", "terminal-value");
                writer.WriteEndObject();
                writer.WriteEndObject();
            }, TestContext.CancellationToken).ConfigureAwait(false);
            using (JsonDocument initialized = await client.ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false))
            {
                AssertEvent(initialized.RootElement, "initialized");
            }

            int configuration = await client.SendRequestAsync(
                "configurationDone", WriteEmptyObject, TestContext.CancellationToken)
                .ConfigureAwait(false);
            using (JsonDocument reverseRequest = await client.ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false))
            {
                JsonElement message = reverseRequest.RootElement;
                launcher.StartInfo = CreateTerminalStart(message.GetProperty("arguments"));
                launcherStarted = launcher.Start();
                Assert.IsTrue(launcherStarted);
                string response = string.Create(CultureInfo.InvariantCulture,
                    $"{{\"seq\":100,\"type\":\"response\",\"request_seq\":{message.GetProperty("seq").GetInt32()}," +
                    $"\"command\":\"runInTerminal\",\"success\":true," +
                    $"\"body\":{{\"shellProcessId\":{launcher.Id}}}}}");
                await client.SendFrameAsync(CreateFrame(response), fragment: false,
                    TestContext.CancellationToken).ConfigureAwait(false);
            }

            using (JsonDocument response = await client.ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false))
            {
                AssertResponse(response.RootElement, configuration, "configurationDone", success: true);
            }
            using (JsonDocument response = await client.ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false))
            {
                AssertResponse(response.RootElement, launch, "launch", success: true);
            }
            using (JsonDocument process = await client.ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false))
            {
                AssertEvent(process.RootElement, "process");
                int targetId = process.RootElement.GetProperty("body")
                    .GetProperty("systemProcessId").GetInt32();
                Assert.AreNotEqual(launcher.Id, targetId);
            }
            using (JsonDocument exited = await client.ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false))
            {
                AssertEvent(exited.RootElement, "exited");
                Assert.AreEqual(23, exited.RootElement.GetProperty("body")
                    .GetProperty("exitCode").GetInt32());
            }
            using (JsonDocument terminated = await client.ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false))
            {
                AssertEvent(terminated.RootElement, "terminated");
            }
            await launcher.WaitForExitAsync(TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(23, launcher.ExitCode);
            Assert.AreEqual("terminal-value", await launcher.StandardOutput
                .ReadToEndAsync(TestContext.CancellationToken).ConfigureAwait(false));
        }
        finally
        {
            if (launcherStarted)
            {
                if (!launcher.HasExited)
                {
                    launcher.Kill(entireProcessTree: true);
                }

                await launcher.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                TestContext.WriteLine(await launcher.StandardError
                    .ReadToEndAsync(CancellationToken.None).ConfigureAwait(false));
            }

            TestContext.WriteLine(client.ProtocolTranscript);
            TestContext.WriteLine(client.Diagnostics.ToString());
        }
    }

    /// <summary>
    /// Leaves managed and no-debug terminal targets running after an explicit detach.
    /// </summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task TerminalDetachPreservesInteractiveTarget(bool noDebug)
    {
        DapTestClient client = await DapTestClient.CreateAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable clientDisposal = client.ConfigureAwait(false);
        using var launcher = new Process();
        bool launcherStarted = false;
        try
        {
            int initialize = await client.SendInitializeRequestAsync(TestContext.CancellationToken,
                writeProperties: writer => writer.WriteBoolean("supportsRunInTerminalRequest", true))
                .ConfigureAwait(false);
            using (JsonDocument response = await client.ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false))
            {
                AssertResponse(response.RootElement, initialize, "initialize", success: true);
            }

            int launch = await client.SendRequestAsync("launch", writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("program", ResolveTestProcessHost());
                writer.WriteString("console", "integratedTerminal");
                writer.WriteBoolean("noDebug", noDebug);
                writer.WriteStartArray("args");
                writer.WriteStringValue("--debugger-terminal-stdio-fixture");
                writer.WriteEndArray();
                writer.WriteEndObject();
            }, TestContext.CancellationToken).ConfigureAwait(false);
            using (JsonDocument initialized = await client.ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false))
            {
                AssertEvent(initialized.RootElement, "initialized");
            }

            int configuration = await client.SendRequestAsync(
                "configurationDone", WriteEmptyObject, TestContext.CancellationToken)
                .ConfigureAwait(false);
            using (JsonDocument reverseRequest = await client.ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false))
            {
                JsonElement message = reverseRequest.RootElement;
                Assert.AreEqual("runInTerminal", message.GetProperty("command").GetString());
                launcher.StartInfo = CreateTerminalStart(message.GetProperty("arguments"));
                launcherStarted = launcher.Start();
                Assert.IsTrue(launcherStarted);
                _ = await client.SendResponseAsync(message.GetProperty("seq").GetInt32(),
                    "runInTerminal", success: true, message: null, TestContext.CancellationToken)
                    .ConfigureAwait(false);
            }

            using (JsonDocument response = await client.ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false))
            {
                AssertResponse(response.RootElement, configuration, "configurationDone", success: true);
            }
            using (JsonDocument response = await client.ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false))
            {
                AssertResponse(response.RootElement, launch, "launch", success: true);
            }
            int targetId;
            using (JsonDocument process = await client.ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false))
            {
                AssertEvent(process.RootElement, "process");
                targetId = process.RootElement.GetProperty("body")
                    .GetProperty("systemProcessId").GetInt32();
            }

            using var target = Process.GetProcessById(targetId);
            try
            {
                Assert.AreEqual("ready", await launcher.StandardOutput
                    .ReadLineAsync(TestContext.CancellationToken).ConfigureAwait(false));
                int disconnect = await client.SendRequestAsync("disconnect", writer =>
                {
                    writer.WriteStartObject();
                    writer.WriteBoolean("terminateDebuggee", false);
                    writer.WriteEndObject();
                }, TestContext.CancellationToken).ConfigureAwait(false);
                using (JsonDocument response = await client.ReadMessageAsync(TestContext.CancellationToken)
                    .ConfigureAwait(false))
                {
                    AssertResponse(response.RootElement, disconnect, "disconnect", success: true);
                }

                Assert.AreEqual(0, await client.WaitForProcessExitAsync(TestContext.CancellationToken)
                    .ConfigureAwait(false));
                Assert.IsFalse(target.HasExited, "Detach terminated the interactive target.");
                await launcher.StandardInput.WriteLineAsync("hello").ConfigureAwait(false);
                await launcher.StandardInput.FlushAsync(TestContext.CancellationToken).ConfigureAwait(false);
                Assert.AreEqual("echo:hello", await launcher.StandardOutput
                    .ReadLineAsync(TestContext.CancellationToken).ConfigureAwait(false));
                await launcher.WaitForExitAsync(TestContext.CancellationToken).ConfigureAwait(false);
                Assert.AreEqual(0, launcher.ExitCode);
                Assert.IsTrue(target.HasExited);
            }
            finally
            {
                if (!target.HasExited)
                {
                    target.Kill(entireProcessTree: true);
                    await target.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            if (launcherStarted && !launcher.HasExited)
            {
                launcher.Kill(entireProcessTree: true);
                await launcher.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }

            TestContext.WriteLine(client.ProtocolTranscript);
            TestContext.WriteLine(client.Diagnostics.ToString());
        }
    }

    /// <summary>
    /// Replaces an interactive no-debug child without keeping the old terminal target alive.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task NoDebugTerminalRestartReplacesInteractiveChild()
    {
        DapTestClient client = await DapTestClient.CreateAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable clientDisposal = client.ConfigureAwait(false);
        using var firstLauncher = new Process();
        using var secondLauncher = new Process();
        bool firstStarted = false;
        bool secondStarted = false;
        try
        {
            int initialize = await client.SendInitializeRequestAsync(TestContext.CancellationToken,
                writeProperties: writer => writer.WriteBoolean("supportsRunInTerminalRequest", true))
                .ConfigureAwait(false);
            using (JsonDocument response = await client.ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false))
            {
                AssertResponse(response.RootElement, initialize, "initialize", success: true);
            }

            int launch = await client.SendRequestAsync("launch", writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("program", ResolveTestProcessHost());
                writer.WriteString("console", "integratedTerminal");
                writer.WriteBoolean("noDebug", true);
                writer.WriteStartArray("args");
                writer.WriteStringValue("--debugger-terminal-stdio-fixture");
                writer.WriteEndArray();
                writer.WriteEndObject();
            }, TestContext.CancellationToken).ConfigureAwait(false);
            using (JsonDocument initialized = await client.ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false))
            {
                AssertEvent(initialized.RootElement, "initialized");
            }

            int configuration = await client.SendRequestAsync(
                "configurationDone", WriteEmptyObject, TestContext.CancellationToken)
                .ConfigureAwait(false);
            using (JsonDocument reverse = await client.ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false))
            {
                JsonElement request = reverse.RootElement;
                firstLauncher.StartInfo = CreateTerminalStart(request.GetProperty("arguments"));
                firstStarted = firstLauncher.Start();
                Assert.IsTrue(firstStarted);
                _ = await client.SendResponseAsync(request.GetProperty("seq").GetInt32(),
                    "runInTerminal", success: true, message: null, TestContext.CancellationToken)
                    .ConfigureAwait(false);
            }
            using (JsonDocument response = await client.ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false))
            {
                AssertResponse(response.RootElement, configuration, "configurationDone", success: true);
            }
            using (JsonDocument response = await client.ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false))
            {
                AssertResponse(response.RootElement, launch, "launch", success: true);
            }
            using (JsonDocument process = await client.ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false))
            {
                AssertEvent(process.RootElement, "process");
                Assert.AreNotEqual(firstLauncher.Id, process.RootElement.GetProperty("body")
                    .GetProperty("systemProcessId").GetInt32());
            }
            Assert.AreEqual("ready", await firstLauncher.StandardOutput
                .ReadLineAsync(TestContext.CancellationToken).ConfigureAwait(false));

            int restart = await client.SendRequestAsync(
                "restart", WriteEmptyObject, TestContext.CancellationToken)
                .ConfigureAwait(false);
            using (JsonDocument oldExited = await client.ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false))
            {
                AssertEvent(oldExited.RootElement, "exited");
            }
            using (JsonDocument reverse = await client.ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false))
            {
                JsonElement request = reverse.RootElement;
                Assert.AreEqual("runInTerminal", request.GetProperty("command").GetString());
                secondLauncher.StartInfo = CreateTerminalStart(request.GetProperty("arguments"));
                secondStarted = secondLauncher.Start();
                Assert.IsTrue(secondStarted);
                _ = await client.SendResponseAsync(request.GetProperty("seq").GetInt32(),
                    "runInTerminal", success: true, message: null, TestContext.CancellationToken)
                    .ConfigureAwait(false);
            }
            using (JsonDocument response = await client.ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false))
            {
                AssertResponse(response.RootElement, restart, "restart", success: true);
            }
            using (JsonDocument process = await client.ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false))
            {
                AssertEvent(process.RootElement, "process");
                Assert.AreNotEqual(secondLauncher.Id, process.RootElement.GetProperty("body")
                    .GetProperty("systemProcessId").GetInt32());
            }
            await firstLauncher.WaitForExitAsync(TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual("ready", await secondLauncher.StandardOutput
                .ReadLineAsync(TestContext.CancellationToken).ConfigureAwait(false));
            await secondLauncher.StandardInput.WriteLineAsync("hello").ConfigureAwait(false);
            await secondLauncher.StandardInput.FlushAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            Assert.AreEqual("echo:hello", await secondLauncher.StandardOutput
                .ReadLineAsync(TestContext.CancellationToken).ConfigureAwait(false));
            using (JsonDocument exited = await client.ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false))
            {
                AssertEvent(exited.RootElement, "exited");
                Assert.AreEqual(0, exited.RootElement.GetProperty("body").GetProperty("exitCode").GetInt32());
            }
            using (JsonDocument terminated = await client.ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false))
            {
                AssertEvent(terminated.RootElement, "terminated");
            }
            await secondLauncher.WaitForExitAsync(TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(0, secondLauncher.ExitCode);
        }
        finally
        {
            if (secondStarted)
            {
                if (!secondLauncher.HasExited)
                {
                    secondLauncher.Kill(entireProcessTree: true);
                }

                await secondLauncher.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }

            if (firstStarted)
            {
                if (!firstLauncher.HasExited)
                {
                    firstLauncher.Kill(entireProcessTree: true);
                }

                await firstLauncher.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }

            TestContext.WriteLine(client.ProtocolTranscript);
            TestContext.WriteLine(client.Diagnostics.ToString());
        }
    }

    /// <summary>
    /// Keeps terminal handles and real target ownership across a managed restart.
    /// </summary>
    [TestMethod]
    [Timeout(45000, CooperativeCancellation = true)]
    public async Task ManagedIntegratedTerminalRestartPreservesStdioAndTargetIdentity()
    {
        DapTestClient client = await DapTestClient.CreateAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable clientDisposal = client.ConfigureAwait(false);
        using var launcher = new Process();
        bool launcherStarted = false;
        using var replacementLauncher = new Process();
        bool replacementStarted = false;
        try
        {
            int initialize = await client.SendInitializeRequestAsync(TestContext.CancellationToken,
                writeProperties: writer => writer.WriteBoolean("supportsRunInTerminalRequest", true))
                .ConfigureAwait(false);
            using (JsonDocument response = await client.ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false))
            {
                AssertResponse(response.RootElement, initialize, "initialize", success: true);
            }

            string program = ResolveTestProcessHost();
            int launch = await client.SendRequestAsync("launch", writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("program", program);
                writer.WriteString("console", "integratedTerminal");
                writer.WriteBoolean("stopAtEntry", true);
                writer.WriteStartArray("args");
                writer.WriteStringValue("--debugger-terminal-stdio-fixture");
                writer.WriteEndArray();
                writer.WriteEndObject();
            }, TestContext.CancellationToken).ConfigureAwait(false);
            using (JsonDocument initialized = await client.ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false))
            {
                AssertEvent(initialized.RootElement, "initialized");
            }

            int configuration = await client.SendRequestAsync(
                "configurationDone", WriteEmptyObject, TestContext.CancellationToken)
                .ConfigureAwait(false);
            using (JsonDocument reverseRequest = await client.ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false))
            {
                JsonElement message = reverseRequest.RootElement;
                Assert.AreEqual("request", message.GetProperty("type").GetString());
                Assert.AreEqual("runInTerminal", message.GetProperty("command").GetString());
                JsonElement arguments = message.GetProperty("arguments");
                Assert.AreEqual("integrated", arguments.GetProperty("kind").GetString());
                ProcessStartInfo start = CreateTerminalStart(arguments);
                launcher.StartInfo = start;
                launcherStarted = launcher.Start();
                Assert.IsTrue(launcherStarted, "The terminal launcher did not start.");
                string response = string.Create(CultureInfo.InvariantCulture,
                    $"{{\"seq\":100,\"type\":\"response\",\"request_seq\":{message.GetProperty("seq").GetInt32()}," +
                    $"\"command\":\"runInTerminal\",\"success\":true," +
                    $"\"body\":{{\"shellProcessId\":{launcher.Id}}}}}");
                await client.SendFrameAsync(CreateFrame(response), fragment: false,
                    TestContext.CancellationToken).ConfigureAwait(false);
            }

            using (JsonDocument response = await client.ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false))
            {
                AssertResponse(response.RootElement, configuration, "configurationDone", success: true);
            }
            using (JsonDocument response = await client.ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false))
            {
                AssertResponse(response.RootElement, launch, "launch", success: true);
            }
            using (JsonDocument process = await client.ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false))
            {
                AssertEvent(process.RootElement, "process");
                int targetId = process.RootElement.GetProperty("body")
                    .GetProperty("systemProcessId").GetInt32();
                Assert.AreNotEqual(launcher.Id, targetId);
            }

            await ContinueTerminalEntryAsync(client).ConfigureAwait(false);
            Assert.AreEqual("ready", await launcher.StandardOutput
                .ReadLineAsync(TestContext.CancellationToken).ConfigureAwait(false));
            int restart = await client.SendRequestAsync(
                "restart", WriteEmptyObject, TestContext.CancellationToken)
                .ConfigureAwait(false);
            using (JsonDocument oldExited = await client.ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false))
            {
                AssertEvent(oldExited.RootElement, "exited");
            }
            using (JsonDocument reverseRequest = await client.ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false))
            {
                JsonElement message = reverseRequest.RootElement;
                Assert.AreEqual("runInTerminal", message.GetProperty("command").GetString());
                replacementLauncher.StartInfo = CreateTerminalStart(message.GetProperty("arguments"));
                replacementStarted = replacementLauncher.Start();
                Assert.IsTrue(replacementStarted, "The replacement terminal did not start.");
                string response = string.Create(CultureInfo.InvariantCulture,
                    $"{{\"seq\":101,\"type\":\"response\",\"request_seq\":{message.GetProperty("seq").GetInt32()}," +
                    $"\"command\":\"runInTerminal\",\"success\":true," +
                    $"\"body\":{{\"shellProcessId\":{replacementLauncher.Id}}}}}");
                await client.SendFrameAsync(CreateFrame(response), fragment: false,
                    TestContext.CancellationToken).ConfigureAwait(false);
            }
            using (JsonDocument response = await client.ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false))
            {
                AssertResponse(response.RootElement, restart, "restart", success: true);
            }
            using (JsonDocument process = await client.ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false))
            {
                AssertEvent(process.RootElement, "process");
                int targetId = process.RootElement.GetProperty("body")
                    .GetProperty("systemProcessId").GetInt32();
                Assert.AreNotEqual(replacementLauncher.Id, targetId);
            }
            await ContinueTerminalEntryAsync(client).ConfigureAwait(false);
            await launcher.WaitForExitAsync(TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual("ready", await replacementLauncher.StandardOutput
                .ReadLineAsync(TestContext.CancellationToken).ConfigureAwait(false));
            await replacementLauncher.StandardInput.WriteLineAsync("hello").ConfigureAwait(false);
            await replacementLauncher.StandardInput.FlushAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            Assert.AreEqual("echo:hello", await replacementLauncher.StandardOutput
                .ReadLineAsync(TestContext.CancellationToken).ConfigureAwait(false));

            using (JsonDocument exited = await client.ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false))
            {
                AssertEvent(exited.RootElement, "exited");
                Assert.AreEqual(0, exited.RootElement.GetProperty("body").GetProperty("exitCode").GetInt32());
            }
            using (JsonDocument terminated = await client.ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false))
            {
                AssertEvent(terminated.RootElement, "terminated");
            }
            await replacementLauncher.WaitForExitAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            Assert.AreEqual(0, replacementLauncher.ExitCode);
        }
        finally
        {
            if (replacementStarted)
            {
                if (!replacementLauncher.HasExited)
                {
                    replacementLauncher.Kill(entireProcessTree: true);
                }

                await replacementLauncher.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                TestContext.WriteLine(await replacementLauncher.StandardError
                    .ReadToEndAsync(CancellationToken.None).ConfigureAwait(false));
            }

            if (launcherStarted)
            {
                if (!launcher.HasExited)
                {
                    launcher.Kill(entireProcessTree: true);
                }

                await launcher.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                TestContext.WriteLine(await launcher.StandardError.ReadToEndAsync(CancellationToken.None)
                    .ConfigureAwait(false));
            }

            TestContext.WriteLine(client.ProtocolTranscript);
            TestContext.WriteLine(client.Diagnostics.ToString());
        }
    }

    private async Task ContinueTerminalEntryAsync(DapTestClient client)
    {
        int threadId;
        using (JsonDocument stopped = await client.ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false))
        {
            AssertEvent(stopped.RootElement, "stopped");
            JsonElement body = stopped.RootElement.GetProperty("body");
            Assert.AreEqual("entry", body.GetProperty("reason").GetString());
            threadId = body.GetProperty("threadId").GetInt32();
        }

        int sequence = await client.SendRequestAsync("continue", writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("threadId", threadId);
            writer.WriteEndObject();
        }, TestContext.CancellationToken).ConfigureAwait(false);
        bool responseReceived = false;
        bool continuedReceived = false;
        while (!responseReceived || !continuedReceived)
        {
            using JsonDocument message = await client.ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            if (message.RootElement.GetProperty("type").GetString() == "response")
            {
                AssertResponse(message.RootElement, sequence, "continue", success: true);
                responseReceived = true;
            }
            else
            {
                AssertEvent(message.RootElement, "continued");
                continuedReceived = true;
            }
        }
    }

    private static ProcessStartInfo CreateTerminalStart(JsonElement request)
    {
        JsonElement.ArrayEnumerator values = request.GetProperty("args").EnumerateArray();
        if (!values.MoveNext())
        {
            throw new InvalidDataException("The terminal request omitted its executable.");
        }

        var start = new ProcessStartInfo(values.Current.GetString()
            ?? throw new InvalidDataException("The terminal executable is empty."))
        {
            UseShellExecute = false,
            WorkingDirectory = request.GetProperty("cwd").GetString()
                ?? throw new InvalidDataException("The terminal working directory is empty."),
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        while (values.MoveNext())
        {
            start.ArgumentList.Add(values.Current.GetString()
                ?? throw new InvalidDataException("A terminal argument is null."));
        }

        foreach (JsonProperty property in request.GetProperty("env").EnumerateObject())
        {
            start.Environment[property.Name] = property.Value.GetString();
        }

        return start;
    }

    private async Task<Process> StartUnauthenticatedLauncherAsync(DapTestClient client)
    {
        using JsonDocument reverseRequest = await client.ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        JsonElement request = reverseRequest.RootElement;
        Assert.AreEqual("runInTerminal", request.GetProperty("command").GetString());
        ProcessStartInfo start = CreateTerminalStart(request.GetProperty("arguments"));
        string secretName = DebuggerTerminalLauncher.SecretEnvironmentVariable;
        byte[] forgedSecret = Convert.FromBase64String(start.Environment[secretName]
            ?? throw new InvalidDataException("The terminal request omitted its launch secret."));
        forgedSecret[0] ^= byte.MaxValue;
        start.Environment[secretName] = Convert.ToBase64String(forgedSecret);
        return Process.Start(start) ?? throw new InvalidOperationException("The terminal launcher did not start.");
    }

    private static byte[] CreateFrame(string payload)
    {
        byte[] body = Encoding.UTF8.GetBytes(payload);
        byte[] header = Encoding.ASCII.GetBytes(string.Create(CultureInfo.InvariantCulture,
            $"Content-Length: {body.Length}\r\n\r\n"));
        return [.. header, .. body];
    }
}
