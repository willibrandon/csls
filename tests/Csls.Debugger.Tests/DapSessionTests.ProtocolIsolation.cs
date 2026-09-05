using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies DAP protocol descriptor isolation through real Unix process boundaries.
/// </summary>
public sealed partial class DapSessionTests
{
    private const int CloseOnExecFlag = 0x80000;

    /// <summary>
    /// Keeps worker protocol descriptors stable and prevents target inheritance during launch.
    /// </summary>
    [TestMethod]
    [OSCondition(ConditionMode.Include, OperatingSystems.Linux)]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task WorkerProtocolDescriptorsRemainStableAndPrivate()
    {
        string testDirectory = Path.Join(
            Path.GetTempPath(),
            $"csls-debugger-descriptors-{Guid.NewGuid():N}");
        Directory.CreateDirectory(testDirectory);
        string signalPath = Path.Join(testDirectory, "target.signal");
        try
        {
            DapTestClient client = await DapTestClient
                .CreateAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            await using ConfiguredAsyncDisposable clientDisposal = client.ConfigureAwait(false);
            int initializeSequence = await client.SendRequestAsync(
                "initialize",
                WriteEmptyObject,
                TestContext.CancellationToken).ConfigureAwait(false);
            using JsonDocument initialize = await client
                .ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            AssertResponse(
                initialize.RootElement,
                initializeSequence,
                "initialize",
                success: true);
            int workerProcessId = await WaitForOnlyChildProcessAsync(
                client.HostProcessId,
                TestContext.CancellationToken).ConfigureAwait(false);
            List<string> protocolDescriptors = GetStableProtocolDescriptorTargets(
                workerProcessId);

            int launchSequence = await client.SendRequestAsync(
                "launch",
                writer => WriteLaunchArguments(
                    writer,
                    ResolveTestProcessHost(),
                    ["--debugger-fixture", signalPath],
                    wait: true,
                    noDebug: false),
                TestContext.CancellationToken).ConfigureAwait(false);
            using JsonDocument initialized = await client
                .ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            AssertEvent(initialized.RootElement, "initialized");
            int configurationSequence = await client.SendRequestAsync(
                "configurationDone",
                WriteEmptyObject,
                TestContext.CancellationToken).ConfigureAwait(false);
            using JsonDocument configuration = await client
                .ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            using JsonDocument launch = await client
                .ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            using JsonDocument process = await client
                .ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            using JsonDocument ready = await client
                .ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            AssertResponse(
                configuration.RootElement,
                configurationSequence,
                "configurationDone",
                success: true);
            AssertResponse(launch.RootElement, launchSequence, "launch", success: true);
            AssertEvent(process.RootElement, "process");
            AssertEvent(ready.RootElement, "output");
            int targetProcessId = process.RootElement.GetProperty("body")
                .GetProperty("systemProcessId").GetInt32();

            HashSet<string> targetDescriptors = GetDescriptorTargets(targetProcessId);
            foreach (string protocolDescriptor in protocolDescriptors)
            {
                Assert.DoesNotContain(
                    protocolDescriptor,
                    targetDescriptors,
                    $"Target {targetProcessId} inherited worker protocol descriptor " +
                        protocolDescriptor);
            }

            int disconnectSequence = await client.SendRequestAsync(
                "disconnect",
                WriteEmptyObject,
                TestContext.CancellationToken).ConfigureAwait(false);
            await ReadUntilResponseAsync(client, disconnectSequence, "disconnect")
                .ConfigureAwait(false);
            Assert.AreEqual(
                0,
                await client.WaitForExitAsync(TestContext.CancellationToken)
                    .ConfigureAwait(false));
            await AssertProcessExitedAsync(targetProcessId, TestContext.CancellationToken)
                .ConfigureAwait(false);
            Assert.AreEqual(string.Empty, client.Diagnostics.ToString());
        }
        finally
        {
            await DebuggerTestDirectoryReleaseWaiter.DeleteAsync(
                testDirectory,
                TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
    }

    private static List<string> GetStableProtocolDescriptorTargets(int processId)
    {
        string descriptorDirectory = $"/proc/{processId}/fd";
        List<string> stableTargets = [];
        for (int standardDescriptor = 1; standardDescriptor <= 2; standardDescriptor++)
        {
            string standardTarget = ReadDescriptorTarget(processId, standardDescriptor);
            int[] matchingDescriptors =
            [
                .. Directory.EnumerateFileSystemEntries(descriptorDirectory)
                    .Select(static path => int.Parse(
                        Path.GetFileName(path),
                        NumberStyles.None,
                        CultureInfo.InvariantCulture))
                    .Where(descriptor => descriptor > 2)
                    .Where(descriptor => string.Equals(
                        TryReadDescriptorTarget(processId, descriptor),
                        standardTarget,
                        StringComparison.Ordinal))
            ];
            Assert.IsNotEmpty(
                matchingDescriptors,
                $"Worker descriptor {standardDescriptor} must have a stable copy.");
            Assert.IsTrue(
                matchingDescriptors.All(descriptor => HasCloseOnExec(processId, descriptor)),
                $"Every stable copy of worker descriptor {standardDescriptor} must be " +
                    "close-on-exec.");
            stableTargets.Add(standardTarget);
        }

        return stableTargets;
    }

    private static HashSet<string> GetDescriptorTargets(int processId) =>
    [
        .. Directory.EnumerateFileSystemEntries($"/proc/{processId}/fd")
            .Select(path => TryReadDescriptorTarget(
                processId,
                int.Parse(
                    Path.GetFileName(path),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture)))
            .OfType<string>()
    ];

    private static string ReadDescriptorTarget(int processId, int descriptor) =>
        TryReadDescriptorTarget(processId, descriptor)
        ?? throw new InvalidDataException(
            $"Descriptor {descriptor} for process {processId} is not a symbolic link.");

    private static string? TryReadDescriptorTarget(int processId, int descriptor)
    {
        try
        {
            return new FileInfo($"/proc/{processId}/fd/{descriptor}").LinkTarget;
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }

    private static bool HasCloseOnExec(int processId, int descriptor)
    {
        string flagsLine = File.ReadLines($"/proc/{processId}/fdinfo/{descriptor}")
            .Single(static line => line.StartsWith("flags:", StringComparison.Ordinal));
        int flags = Convert.ToInt32(flagsLine["flags:".Length..].Trim(), fromBase: 8);
        return (flags & CloseOnExecFlag) != 0;
    }

    private static async Task<int> WaitForOnlyChildProcessAsync(
        int processId,
        CancellationToken cancellationToken)
    {
        string childrenPath = $"/proc/{processId}/task/{processId}/children";
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(10));
        do
        {
            string[] children = (await File.ReadAllTextAsync(childrenPath, cancellationToken)
                    .ConfigureAwait(false))
                .Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (children.Length == 1)
            {
                return int.Parse(children[0], NumberStyles.None, CultureInfo.InvariantCulture);
            }
        }
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false));

        throw new InvalidOperationException("The debugger worker child process did not start.");
    }
}
