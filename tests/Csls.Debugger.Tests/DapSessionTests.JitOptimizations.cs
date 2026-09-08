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
        string programPath = GetJitFixture("Release");
        await AssertModuleOptimizationAsync(
            programPath,
            isSuppressed: false,
            enableHotReload: false)
            .ConfigureAwait(false);
        await AssertModuleOptimizationAsync(
            programPath,
            isSuppressed: true,
            enableHotReload: false)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Reports effective Edit and Continue policy from the real module-load callback.
    /// </summary>
    [TestMethod]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task EnableHotReloadChangesModulePolicy()
    {
        string programPath = GetJitFixture("Debug");
        await AssertModuleOptimizationAsync(
            programPath,
            isSuppressed: true,
            enableHotReload: true).ConfigureAwait(false);
    }

    /// <summary>
    /// Applies source-debugging JIT policy to modules selected for matching symbol loading.
    /// </summary>
    /// <param name="includeSymbols">Whether the launched module is selected for symbol loading.</param>
    /// <param name="enableHotReload">Whether the launch requests Edit and Continue preparation.</param>
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    [Timeout(30000, CooperativeCancellation = true)]
    public Task ModuleJitPolicyFollowsSymbolSelection(bool includeSymbols, bool enableHotReload) =>
        AssertModuleOptimizationAsync(GetJitFixture("Release"), isSuppressed: true, enableHotReload,
            includeSymbols, expectedHotReload: false);

    private async Task AssertModuleOptimizationAsync(
        string programPath,
        bool isSuppressed,
        bool enableHotReload,
        bool? includeSymbols = null,
        bool? expectedHotReload = null)
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
            await PrepareJitLaunchAsync(
                client,
                programPath,
                waitPath,
                isSuppressed,
                enableHotReload,
                includeSymbols)
                .ConfigureAwait(false);
            int sequence = await client.SendRequestAsync(
                "modules",
                WriteEmptyObject,
                TestContext.CancellationToken).ConfigureAwait(false);
            using JsonDocument response = await client
                .ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            AssertResponse(response.RootElement, sequence, "modules", success: true);
            JsonElement loadedModules = response.RootElement.GetProperty("body").GetProperty("modules");
            JsonElement[] modules = [.. loadedModules
                .EnumerateArray()
                .Where(candidate => candidate.TryGetProperty("path", out JsonElement path) &&
                    DebuggerTestPath.AreEquivalent(path.GetString(), programPath))];
            Assert.HasCount(1, modules, response.RootElement.ToString());
            JsonElement module = modules[0];
            bool symbolsLoaded = includeSymbols != false;
            bool sourcePolicyApplied = symbolsLoaded && isSuppressed;
            bool hotReloadPrepared = expectedHotReload ?? enableHotReload;
            Assert.AreEqual(sourcePolicyApplied, !module.GetProperty("isOptimized").GetBoolean(), module.ToString());
            Assert.AreEqual(
                hotReloadPrepared,
                module.GetProperty("isHotReloadEnabled").GetBoolean(),
                response.RootElement.ToString());
            bool advertisesBaseline = module
                .GetProperty("hotReloadCapabilities")
                .EnumerateArray()
                .Any(static capability => capability.GetString() == "Baseline");
            Assert.AreEqual(hotReloadPrepared, advertisesBaseline);
            Assert.AreEqual(0, module.GetProperty("hotReloadGeneration").GetInt32());
            Assert.AreEqual(
                sourcePolicyApplied,
                module.GetProperty("isUserCode").GetBoolean());
            string status = symbolsLoaded ? "Symbols loaded." : "Symbols not found.";
            if (enableHotReload && !hotReloadPrepared)
            {
                status += symbolsLoaded ? " CoreCLR did not enable Hot Reload for this module."
                    : " Hot Reload requires matching debug symbols.";
            }
            Assert.AreEqual(status, module.GetProperty("symbolStatus").GetString());
            if (includeSymbols is not null)
            {
                JsonElement coreLibrary = Assert.ContainsSingle(loadedModules.EnumerateArray()
                    .Where(static candidate => candidate.GetProperty("name").GetString() == "System.Private.CoreLib.dll"));
                Assert.IsTrue(coreLibrary.GetProperty("isOptimized").GetBoolean(), coreLibrary.ToString());
                Assert.IsFalse(coreLibrary.GetProperty("isHotReloadEnabled").GetBoolean());
                Assert.IsEmpty(coreLibrary.GetProperty("hotReloadCapabilities").EnumerateArray());
                Assert.IsFalse(coreLibrary.GetProperty("isUserCode").GetBoolean());
                Assert.AreEqual(enableHotReload ? "Symbols not found. Hot Reload requires matching debug symbols." : "Symbols not found.",
                    coreLibrary.GetProperty("symbolStatus").GetString());
            }
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
        bool suppressJitOptimizations,
        bool enableHotReload,
        bool? includeSymbols = null)
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
                suppressJitOptimizations,
                enableHotReload,
                includeSymbols),
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
