using Csls.TestProcessHost;
using System.Diagnostics;
using System.Globalization;
using System.Text;

if (args is ["--windows-observed", ..] && OperatingSystem.IsWindows())
{
    if (await Console.In.ReadLineAsync().ConfigureAwait(false) != "capture")
    {
        throw new InvalidOperationException("The native observer did not release the capture gate.");
    }
    args = args[1..];
}

if (args is ["--windows-native-fault", string nativeFaultMode] && OperatingSystem.IsWindows())
{
    return await WindowsNativeFaultFixture.RunAsync(nativeFaultMode).ConfigureAwait(false);
}

if (args is ["--windows-mapped-memory-query", string mappedMemoryPath] && OperatingSystem.IsWindows())
{
    return WindowsDumpMappedMemoryFixture.Run(mappedMemoryPath);
}

if (args is ["--windows-native-dump", string dumpProcessId, string dumpCreationTime, string nativeDumpPath] &&
    OperatingSystem.IsWindows())
{
    return WindowsNativeDumpFixture.Run(int.Parse(dumpProcessId, CultureInfo.InvariantCulture),
        long.Parse(dumpCreationTime, CultureInfo.InvariantCulture), nativeDumpPath);
}

if (args is ["--windows-native-dump", string selectedDumpProcessId, string selectedDumpCreationTime,
    string selectedNativeDumpPath, string captureKind] && OperatingSystem.IsWindows())
{
    return WindowsNativeDumpFixture.Run(int.Parse(selectedDumpProcessId, CultureInfo.InvariantCulture),
        long.Parse(selectedDumpCreationTime, CultureInfo.InvariantCulture), selectedNativeDumpPath, captureKind);
}

if (args is ["--windows-native-dump", string diagnosedDumpProcessId, string diagnosedDumpCreationTime,
    string diagnosedNativeDumpPath, string diagnosedCaptureKind, string collectorDiagnosticsPath] &&
    OperatingSystem.IsWindows())
{
    return WindowsNativeDumpFixture.Run(int.Parse(diagnosedDumpProcessId, CultureInfo.InvariantCulture),
        long.Parse(diagnosedDumpCreationTime, CultureInfo.InvariantCulture), diagnosedNativeDumpPath,
        diagnosedCaptureKind, collectorDiagnosticsPath);
}

if (args is ["--windows-module-churn", string churnStopPath] && OperatingSystem.IsWindows())
{
    return WindowsModuleChurnFixture.Run(churnStopPath);
}

if (args is ["--windows-retired-snapshot", string snapshotProcessId, string snapshotCreationTime] &&
    OperatingSystem.IsWindows())
{
    return WindowsRetiredSnapshotFixture.Run(int.Parse(snapshotProcessId, CultureInfo.InvariantCulture),
        long.Parse(snapshotCreationTime, CultureInfo.InvariantCulture), "ready");
}

if (args is ["--debugger-process-tree", string processTreePipe])
{
    return await DebuggerProcessTreeFixture.RunRootAsync(processTreePipe).ConfigureAwait(false);
}

if (args is ["--debugger-process-tree-child", string processTreeKind])
{
    return await DebuggerProcessTreeFixture.RunChildAsync(processTreeKind).ConfigureAwait(false);
}

if (args is ["--debugger-inherited-output-root", string rootOutputPipe, string childOutputPipe])
{
    return await DebuggerProcessTreeFixture.RunInheritedOutputRootAsync(rootOutputPipe, childOutputPipe).ConfigureAwait(false);
}

if (args is ["--debugger-inherited-output-child", string inheritedOutputPipe])
{
    return await DebuggerProcessTreeFixture.RunInheritedOutputChildAsync(inheritedOutputPipe).ConfigureAwait(false);
}

if (args is ["--debugger-native-authorization-fixture", string nativeDebuggerPid])
{
    return DebuggerNativeAuthorizationFixture.Run(int.Parse(nativeDebuggerPid, CultureInfo.InvariantCulture));
}

if (args is ["--unix-wait-status-fixture", string waitedExitCode])
{
    return UnixWaitStatusFixture.Run(
        int.Parse(waitedExitCode, NumberStyles.Integer, CultureInfo.InvariantCulture));
}

if (args is ["--unix-wait-untracked-fixture", string untrackedExitCode])
{
    return await UnixWaitStatusFixture.RunUntrackedAsync(
        int.Parse(untrackedExitCode, NumberStyles.Integer, CultureInfo.InvariantCulture)).ConfigureAwait(false);
}

if (args is ["--debugger-deep-stack-fixture", string stackDepth])
{
    return DebuggerDeepStackFixture.Run(int.Parse(stackDepth, CultureInfo.InvariantCulture));
}

if (args is ["--debugger-stack-overflow-fixture"])
{
    return DebuggerDeepStackFixture.RunOverflow();
}

if (args is ["--debugger-native-memory-crash-fixture"])
{
    return DebuggerNativeMemoryCrashFixture.Run();
}

if (args is ["--debugger-large-graph-fixture"])
{
    return DebuggerLargeGraphFixture.Run();
}

if (args is ["--debugger-dump-arrays", string dumpArrayPath])
{
    return DebuggerDumpArrayFixture.Run(dumpArrayPath, "ready");
}

if (args is ["--debugger-fixture", string fixturePath])
{
    return DebuggerFixture.WaitForSignal(
        fixturePath,
        "ready",
        42,
        "answer",
        (ArgumentNumber: 42, ArgumentText: "argument"));
}

if (args is ["--debugger-authorized-fixture", string authorizedFixturePath, string authorizedTestHost])
{
    DebuggerNativeAuthorization.AllowDebugger(int.Parse(authorizedTestHost, CultureInfo.InvariantCulture));
    return DebuggerFixture.WaitForSignal(
        authorizedFixturePath,
        "ready",
        42,
        "answer",
        (ArgumentNumber: 42, ArgumentText: "argument"));
}

if (args is ["--debugger-unsafe-stop-fixture" or "--debugger-dump-fixture", string unsafeStopPath])
{
    return DebuggerFixture.WaitForSignal(
        unsafeStopPath,
        "ready",
        42,
        "answer",
        (ArgumentNumber: 42, ArgumentText: "argument"),
        blockForInspection: true);
}

if (args is ["--debugger-reference-assignment-fixture", string referenceAssignmentPath])
{
    return ReferenceAssignmentFixture<Exception>.Run(
        referenceAssignmentPath, new InvalidOperationException("generic base"), new ArgumentException("generic replacement"));
}

if (args is ["--debugger-nullable-assignment-fixture", string nullableAssignmentPath])
{
    return NullableAssignmentDebuggerFixture.Run(nullableAssignmentPath);
}

if (args is ["--debugger-nullable-assignment-fixture", string hostileNullablePath, string nullableAssemblyPath])
{
    return NullableAssignmentDebuggerFixture.Run(hostileNullablePath, nullableAssemblyPath);
}

if (args is ["--debugger-results-view-context-fixture", string resultsViewContextPath])
{
    return DebuggerFixture.WaitForSignal(
        resultsViewContextPath,
        "ready",
        42,
        "answer",
        (ArgumentNumber: 42, ArgumentText: "argument"),
        isolateResultsViewAssembly: true);
}

if (args is ["--debugger-results-view-unavailable-fixture", string unavailableResultsViewPath])
{
    return ResultsViewAvailabilityDebuggerFixture.WaitForSignal(unavailableResultsViewPath, "ready");
}

if (args is ["--debugger-results-view-spoof-fixture", string spoofedResultsViewPath, string exceptionAssemblyPath])
{
    return DebuggerFixture.WaitForSignal(
        spoofedResultsViewPath,
        "ready",
        42,
        "answer",
        (ArgumentNumber: 42, ArgumentText: "argument"),
        resultsViewExceptionAssemblyPath: exceptionAssemblyPath);
}

if (args is ["--debugger-step-fixture", string stepFixturePath])
{
    return DebuggerStepFixture.Run(stepFixturePath);
}

if (args is ["--debugger-response-order-fixture"])
{
    return DebuggerResponseOrderFixture.Run();
}

if (args is ["--debugger-async-step-fixture", string asyncInitialValue])
{
    return await DebuggerAsyncStepFixture.RunAsync(
        int.Parse(asyncInitialValue, NumberStyles.None, CultureInfo.InvariantCulture))
        .ConfigureAwait(false);
}

if (args is ["--debugger-captured-arguments", string capturedAssembly, string capturedArgument,
    string capturedReplacement, string capturedReceiver])
{
    return await DebuggerAsyncArgumentRunner.RunAsync(capturedAssembly, capturedArgument,
        capturedReplacement, capturedReceiver).ConfigureAwait(false);
}

if (args is ["--debugger-unused-argument-shapes", string unusedArgumentAssembly])
{
    return await DebuggerAsyncArgumentRunner.RunUnusedShapesAsync(unusedArgumentAssembly).ConfigureAwait(false);
}

if (args is ["--debugger-concurrent-async-step-fixture"])
{
    return await DebuggerAsyncStepFixture.RunConcurrentAsync().ConfigureAwait(false);
}

if (args is ["--debugger-valuetask-step-fixture", string valueTaskPipeName])
{
    return await DebuggerValueTaskStepFixture.RunAsync(valueTaskPipeName).ConfigureAwait(false);
}

if (args is ["--debugger-async-step-out-fixture", string asyncStepOutPipeName, string asyncStepOutKind])
{
    return await DebuggerAsyncStepOutFixture.RunAsync(asyncStepOutPipeName,
        asyncStepOutKind == "ValueTask").ConfigureAwait(false);
}

if (args is ["--debugger-concurrent-async-step-out-fixture", string concurrentStepOutPipeName, string concurrentStepOutKind])
{
    return await DebuggerAsyncStepOutFixture.RunConcurrentAsync(concurrentStepOutPipeName,
        concurrentStepOutKind == "ValueTask").ConfigureAwait(false);
}

if (args is ["--debugger-scheduled-async-step-out-fixture", string scheduledStepOutPipeName, string scheduledStepOutKind])
{
    return await DebuggerAsyncStepOutFixture.RunAsync(scheduledStepOutPipeName,
        scheduledStepOutKind == "ValueTask", scheduled: true).ConfigureAwait(false);
}

if (args is ["--debugger-async-iterator-step-fixture", string asyncIteratorPipeName])
{
    return await DebuggerAsyncIteratorStepFixture.RunAsync(asyncIteratorPipeName).ConfigureAwait(false);
}

if (args is ["--debugger-concurrent-async-iterator-step-fixture", string concurrentAsyncIteratorPipeName])
{
    return await DebuggerAsyncIteratorStepFixture.RunConcurrentAsync(concurrentAsyncIteratorPipeName).ConfigureAwait(false);
}

if (args is ["--debugger-iterator-step-fixture"])
{
    return DebuggerIteratorStepFixture.Run();
}

if (args is ["--debugger-step-filtering-fixture", string stepFilteringPath])
{
    return DebuggerStepFilteringFixture.Run(stepFilteringPath);
}

if (args is [
    "--debugger-hit-fixture",
    string hitSignalPath,
    string hitProgressPath,
    string hitCount])
{
    return DebuggerHitFixture.Run(
        hitSignalPath,
        hitProgressPath,
        int.Parse(hitCount, NumberStyles.None, CultureInfo.InvariantCulture));
}

if (args is ["--debugger-exception-fixture", string exceptionFixturePath])
{
    return DebuggerExceptionFixture.Run(exceptionFixturePath);
}

if (args is ["--debugger-exception-filter-fixture", string exceptionFilterFixturePath])
{
    return DebuggerExceptionFilterFixture.Run(exceptionFilterFixturePath);
}

if (args is [
    "--debugger-in-memory-fixture",
    string assemblyPath,
    string symbolPath,
    string inMemorySignalPath])
{
    return InMemoryAssemblyRunner.Run(
        assemblyPath,
        symbolPath,
        inMemorySignalPath,
        announce: false);
}

if (args is [
    "--debugger-in-memory-attach-fixture",
    string attachAssemblyPath,
    string attachSymbolPath,
    string attachSignalPath,
    string authorizedDebugger])
{
    DebuggerNativeAuthorization.AllowDebugger(int.Parse(authorizedDebugger, CultureInfo.InvariantCulture));
    return InMemoryAssemblyRunner.Run(
        attachAssemblyPath,
        attachSymbolPath,
        attachSignalPath,
        announce: true);
}

if (args is [
    "--debugger-module-churn-fixture",
    string collectibleAssemblyPath,
    string loadSignalPath,
    string collectibleFixtureSignalPath,
    string unloadedSignalPath,
    string finishSignalPath])
{
    return CollectibleAssemblyRunner.Run(
        collectibleAssemblyPath,
        loadSignalPath,
        collectibleFixtureSignalPath,
        unloadedSignalPath,
        finishSignalPath);
}

if (args is ["--print-environment-entry", string entryVariable])
{
    string? entryValue = Environment.GetEnvironmentVariable(entryVariable);
    Console.Write(entryValue is null ? "unset" : $"set:{entryValue}");
    return 23;
}

if (args is ["--print-environment-and-exit", string printedVariable, string exitCode])
{
    await Console.Out.WriteAsync(
        Environment.GetEnvironmentVariable(printedVariable) ?? string.Empty).ConfigureAwait(false);
    return int.Parse(exitCode, NumberStyles.Integer, CultureInfo.InvariantCulture);
}

if (args is ["--print-environment", string environmentVariable])
{
    await Console.Out.WriteAsync(
        Environment.GetEnvironmentVariable(environmentVariable) ?? string.Empty).ConfigureAwait(false);
    return 0;
}

if (args is ["--print-utf8-environment", string utf8Variable])
{
    Console.OutputEncoding = new UTF8Encoding(false);
    await Console.Out.WriteAsync(
        Environment.GetEnvironmentVariable(utf8Variable) ?? string.Empty).ConfigureAwait(false);
    return 0;
}

if (args is ["--print-utf8-environment-and-wait-for-file", string progressVariable, string progressReleasePath])
{
    Console.OutputEncoding = new UTF8Encoding(false);
    await Console.Out.WriteAsync(
        Environment.GetEnvironmentVariable(progressVariable) ?? string.Empty).ConfigureAwait(false);
    await Console.Out.FlushAsync().ConfigureAwait(false);
    await WaitForFileAsync(progressReleasePath).ConfigureAwait(false);
    return 0;
}

if (args is ["--wait-for-standard-input"])
{
    _ = await Console.In.ReadToEndAsync().ConfigureAwait(false);
    return 0;
}

if (args is ["--write-buffered-diagnostics", string diagnosticStream, string diagnosticReleasePath])
{
    using Stream output = diagnosticStream == "stderr" ? Console.OpenStandardError() : Console.OpenStandardOutput();
    string payload = Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + "\nfirst diagnostic\r\nUnicode: \u03bb \ud83d\ude80\rfinal diagnostic";
    // One small write commits all records before the reader can cancel after the process identifier.
    await output.WriteAsync(Encoding.UTF8.GetBytes(payload)).ConfigureAwait(false);
    await WaitForFileAsync(diagnosticReleasePath).ConfigureAwait(false);
    return 0;
}

if (args is ["--wait-for-file", string waitPath])
{
    await WaitForFileAsync(waitPath).ConfigureAwait(false);
    return 0;
}

if (args is ["--announce-process-and-wait-for-file", string processPath, string releasePath])
{
    string temporaryPath = processPath + ".tmp";
    await File.WriteAllTextAsync(temporaryPath, Environment.ProcessId.ToString(CultureInfo.InvariantCulture))
        .ConfigureAwait(false);
    File.Move(temporaryPath, processPath);
    await WaitForFileAsync(releasePath).ConfigureAwait(false);
    return 0;
}

if (args is ["--announce-and-spin-until-file", string spinPath])
{
    await Console.Out.WriteAsync("ready").ConfigureAwait(false);
    await Console.Out.FlushAsync().ConfigureAwait(false);
    while (!File.Exists(spinPath))
    {
        await Task.Delay(1).ConfigureAwait(false);
    }

    return 0;
}

Dictionary<string, string> environment = new(StringComparer.Ordinal);
int argumentIndex = 0;
while (argumentIndex < args.Length &&
    string.Equals(args[argumentIndex], "--environment", StringComparison.Ordinal))
{
    if (argumentIndex + 2 >= args.Length)
    {
        await Console.Error.WriteLineAsync(
            "Each --environment option requires a name and value.").ConfigureAwait(false);
        return 2;
    }

    environment[args[argumentIndex + 1]] = args[argumentIndex + 2];
    argumentIndex += 3;
}

if (argumentIndex >= args.Length ||
    !string.Equals(args[argumentIndex], "--", StringComparison.Ordinal) ||
    argumentIndex + 1 >= args.Length)
{
    await Console.Error.WriteLineAsync(
        "Usage: csls-test-process-host [--environment <name> <value>]... -- <executable> [arguments...]")
        .ConfigureAwait(false);
    return 2;
}

ProcessStartInfo startInfo = new()
{
    FileName = args[argumentIndex + 1],
    UseShellExecute = false
};
foreach ((string name, string value) in environment)
{
    startInfo.Environment[name] = value;
}

for (int index = argumentIndex + 2; index < args.Length; index++)
{
    startInfo.ArgumentList.Add(args[index]);
}

if (!OperatingSystem.IsWindows())
{
    UnixProcess.Execute(startInfo.FileName, startInfo.ArgumentList, environment);
    throw new UnreachableException("execve returned after replacing the test host.");
}

using Process process = Process.Start(startInfo)
    ?? throw new InvalidOperationException("The hosted process did not start.");
await process.WaitForExitAsync().ConfigureAwait(false);
return process.ExitCode;

static async Task WaitForFileAsync(string path)
{
    string fullPath = Path.GetFullPath(path);
    if (File.Exists(fullPath))
    {
        return;
    }

    string directoryPath = Path.GetDirectoryName(fullPath)
        ?? throw new InvalidDataException($"The wait path has no parent: {fullPath}");
    string fileName = Path.GetFileName(fullPath);
    var completion = new TaskCompletionSource(
        TaskCreationOptions.RunContinuationsAsynchronously);
    using var watcher = new FileSystemWatcher(directoryPath, fileName)
    {
        NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite
    };
    watcher.Created += (_, _) => completion.TrySetResult();
    watcher.Changed += (_, _) => completion.TrySetResult();
    watcher.Renamed += (_, _) => completion.TrySetResult();
    watcher.EnableRaisingEvents = true;
    if (File.Exists(fullPath))
    {
        return;
    }

    await completion.Task.ConfigureAwait(false);
}
