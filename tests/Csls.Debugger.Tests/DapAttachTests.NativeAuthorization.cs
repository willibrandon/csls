using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies native access errors and recovery through the live DAP connection.
/// </summary>
public sealed partial class DapAttachTests
{
    /// <summary>
    /// Preserves the adapter and target when Linux denies native context capture and later restores access.
    /// </summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Linux)]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task NativeContextPermissionFailurePreservesAttachedSession()
    {
        if (RuntimeInformation.ProcessArchitecture != Architecture.Arm64)
        {
            Assert.Inconclusive("This DAP native-context path executes on Linux ARM64.");
        }

        DapTestClient client = await DapTestClient.CreateAsync(TestContext.CancellationToken).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable clientCleanup = client.ConfigureAwait(false);
        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add(ResolveTestProcessHost());
        startInfo.ArgumentList.Add("--debugger-native-authorization-fixture");
        startInfo.ArgumentList.Add(client.HostProcessId.ToString(CultureInfo.InvariantCulture));
        using Process target = Process.Start(startInfo)
            ?? throw new AssertFailedException("The native permission target did not start.");
        Task<string> error = target.StandardError.ReadToEndAsync(CancellationToken.None);
        try
        {
            Assert.AreEqual("ready", await target.StandardOutput.ReadLineAsync(TestContext.CancellationToken).ConfigureAwait(false));
            await AttachAsync(client, target.Id).ConfigureAwait(false);
            await SetNativeAccessAsync(target, "deny", "denied").ConfigureAwait(false);
            await PauseNativeTargetAsync(client).ConfigureAwait(false);
            int denied = await SendNativeStackRequestAsync(client, target.Id).ConfigureAwait(false);
            using (JsonDocument response = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false))
            {
                Assert.AreEqual("response", response.RootElement.GetProperty("type").GetString());
                Assert.AreEqual(denied, response.RootElement.GetProperty("request_seq").GetInt32());
                Assert.AreEqual("stackTrace", response.RootElement.GetProperty("command").GetString());
                Assert.IsFalse(response.RootElement.GetProperty("success").GetBoolean());
                string message = response.RootElement.GetProperty("message").GetString() ?? string.Empty;
                Assert.Contains("PTRACE_SEIZE", message);
                Assert.Contains("authorize this debugger", message);
            }

            int threads = await client.SendRequestAsync("threads", WriteEmptyObject, TestContext.CancellationToken).ConfigureAwait(false);
            using (JsonDocument response = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false))
            {
                AssertResponse(response.RootElement, threads, "threads");
                Assert.Contains(target.Id, response.RootElement.GetProperty("body").GetProperty("threads").EnumerateArray()
                    .Select(thread => thread.GetProperty("id").GetInt32()).ToArray());
            }

            int resume = await client.SendRequestAsync("continue", WriteEmptyObject, TestContext.CancellationToken).ConfigureAwait(false);
            using (JsonDocument first = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false))
            using (JsonDocument second = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false))
            {
                bool firstIsResponse = first.RootElement.GetProperty("type").GetString() == "response";
                JsonElement response = firstIsResponse ? first.RootElement : second.RootElement;
                JsonElement continued = firstIsResponse ? second.RootElement : first.RootElement;
                AssertResponse(response, resume, "continue");
                Assert.IsTrue(response.GetProperty("body").GetProperty("allThreadsContinued").GetBoolean());
                AssertEvent(continued, "continued");
                Assert.IsTrue(continued.GetProperty("body").GetProperty("allThreadsContinued").GetBoolean());
            }

            await SetNativeAccessAsync(target, "allow", "allowed").ConfigureAwait(false);
            await PauseNativeTargetAsync(client).ConfigureAwait(false);
            int restored = await SendNativeStackRequestAsync(client, target.Id).ConfigureAwait(false);
            using (JsonDocument response = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false))
            {
                AssertResponse(response.RootElement, restored, "stackTrace");
                _ = Assert.ContainsSingle(response.RootElement.GetProperty("body").GetProperty("stackFrames").EnumerateArray()
                    .Where(frame => frame.GetProperty("name").GetString()?.Contains(
                        "DebuggerNativeAuthorizationFixture.Run", StringComparison.Ordinal) == true));
            }

            int disconnect = await client.SendRequestAsync("disconnect", WriteEmptyObject, TestContext.CancellationToken).ConfigureAwait(false);
            using (JsonDocument response = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false))
            {
                AssertResponse(response.RootElement, disconnect, "disconnect");
            }
            Assert.IsFalse(target.HasExited);
            await target.StandardInput.WriteLineAsync("exit".AsMemory(), TestContext.CancellationToken).ConfigureAwait(false);
            await target.StandardInput.FlushAsync(TestContext.CancellationToken).ConfigureAwait(false);
            await target.WaitForExitAsync(TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(0, target.ExitCode);
            Assert.AreEqual(0, await client.WaitForExitAsync(TestContext.CancellationToken).ConfigureAwait(false));
            Assert.IsEmpty(client.Diagnostics.ToString());
        }
        catch
        {
            TestContext.WriteLine(client.ProtocolTranscript);
            TestContext.WriteLine(client.Diagnostics.ToString());
            throw;
        }
        finally
        {
            if (!target.HasExited)
            {
                target.Kill(entireProcessTree: true);
            }
            await target.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.IsEmpty(await error.ConfigureAwait(false));
        }
    }

    private async Task SetNativeAccessAsync(Process target, string command, string expected)
    {
        await target.StandardInput.WriteLineAsync(command.AsMemory(), TestContext.CancellationToken).ConfigureAwait(false);
        await target.StandardInput.FlushAsync(TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(expected, await target.StandardOutput.ReadLineAsync(TestContext.CancellationToken).ConfigureAwait(false));
    }

    private async Task PauseNativeTargetAsync(DapTestClient client)
    {
        (int _, JsonDocument stopped, JsonDocument response) = await PauseAsync(client).ConfigureAwait(false);
        using (stopped)
        using (response)
        {
            Assert.AreEqual("pause", stopped.RootElement.GetProperty("body").GetProperty("reason").GetString());
        }
    }

    private Task<int> SendNativeStackRequestAsync(DapTestClient client, int threadId) => client.SendRequestAsync(
        "stackTrace", writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("threadId", threadId);
            writer.WriteNumber("levels", 16);
            writer.WriteEndObject();
        }, TestContext.CancellationToken);
}
