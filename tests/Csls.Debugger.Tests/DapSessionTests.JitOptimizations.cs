using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies launch-time JIT policy and module optimization diagnostics.
/// </summary>
public sealed partial class DapSessionTests
{
    /// <summary>
    /// Reports and suppresses Release-module JIT optimization through the real runtime callback.
    /// </summary>
    [TestMethod]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task SuppressJitOptimizationsChangesReleaseModulePolicy()
    {
        string artifactsPath = Path.Join(
            Path.GetTempPath(),
            $"csls-debugger-jit-{Guid.NewGuid():N}");
        try
        {
            string programPath = await BuildJitFixtureAsync(artifactsPath).ConfigureAwait(false);
            await AssertModuleOptimizationAsync(programPath, isSuppressed: false)
                .ConfigureAwait(false);
            await AssertModuleOptimizationAsync(programPath, isSuppressed: true)
                .ConfigureAwait(false);
        }
        finally
        {
            if (Directory.Exists(artifactsPath))
            {
                Directory.Delete(artifactsPath, recursive: true);
            }
        }
    }

    private async Task AssertModuleOptimizationAsync(string programPath, bool isSuppressed)
    {
        string waitPath = Path.Join(
            Path.GetTempPath(),
            $"csls-debugger-jit-wait-{Guid.NewGuid():N}.signal");
        try
        {
            DapTestClient client = await DapTestClient
                .CreateAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            await using ConfiguredAsyncDisposable disposal = client.ConfigureAwait(false);
            await PrepareJitLaunchAsync(client, programPath, waitPath, isSuppressed)
                .ConfigureAwait(false);
            int sequence = await client.SendRequestAsync(
                "modules",
                WriteEmptyObject,
                TestContext.CancellationToken).ConfigureAwait(false);
            using JsonDocument response = await client
                .ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            AssertResponse(response.RootElement, sequence, "modules", success: true);
            JsonElement[] modules = [.. response.RootElement.GetProperty("body")
                .GetProperty("modules")
                .EnumerateArray()
                .Where(candidate => candidate.TryGetProperty("path", out JsonElement path) &&
                    DebuggerTestPath.AreEquivalent(path.GetString(), programPath))];
            Assert.HasCount(1, modules, response.RootElement.ToString());
            JsonElement module = modules[0];
            Assert.AreEqual(isSuppressed, !module.GetProperty("isOptimized").GetBoolean());
            Assert.AreEqual(
                isSuppressed,
                module.GetProperty("isUserCode").GetBoolean());
            Assert.AreEqual("Symbols loaded.", module.GetProperty("symbolStatus").GetString());
            await DisconnectAsync(client).ConfigureAwait(false);
            Assert.AreEqual(string.Empty, client.Diagnostics.ToString());
        }
        finally
        {
            File.Delete(waitPath);
        }
    }

    private async Task PrepareJitLaunchAsync(
        DapTestClient client,
        string programPath,
        string waitPath,
        bool suppressJitOptimizations)
    {
        int initializeSequence = await client.SendRequestAsync(
            "initialize",
            WriteEmptyObject,
            TestContext.CancellationToken).ConfigureAwait(false);
        using JsonDocument initialize = await client
            .ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        AssertResponse(initialize.RootElement, initializeSequence, "initialize", success: true);
        int launchSequence = await client.SendRequestAsync(
            "launch",
            writer => WriteJitLaunchArguments(
                writer,
                programPath,
                waitPath,
                suppressJitOptimizations),
            TestContext.CancellationToken).ConfigureAwait(false);
        using JsonDocument initialized = await client
            .ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        AssertEvent(initialized.RootElement, "initialized");
        int configurationSequence = await client.SendRequestAsync(
            "configurationDone",
            WriteEmptyObject,
            TestContext.CancellationToken).ConfigureAwait(false);
        await ReadTargetStartAsync(client, configurationSequence, launchSequence)
            .ConfigureAwait(false);
    }

    private async Task ReadTargetStartAsync(
        DapTestClient client,
        int configurationSequence,
        int launchSequence)
    {
        using JsonDocument configuration = await client
            .ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        AssertResponse(
            configuration.RootElement,
            configurationSequence,
            "configurationDone",
            success: true);
        using JsonDocument launch = await client
            .ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        AssertResponse(launch.RootElement, launchSequence, "launch", success: true);
        using JsonDocument process = await client
            .ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        AssertEvent(process.RootElement, "process");
        using JsonDocument output = await client
            .ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        AssertEvent(output.RootElement, "output");
        Assert.AreEqual(
            "ready",
            output.RootElement.GetProperty("body").GetProperty("output").GetString());
    }
}
