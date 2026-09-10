#!/usr/bin/env -S dotnet --
#:property TargetFramework=net10.0
#:property LangVersion=14.0
#:property Nullable=enable
#:property TreatWarningsAsErrors=true
#:property RootNamespace=Csls
#:include Support/ProcessOutputCapture.cs

using Csls.Support;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;

const string usage =
    "Usage: dotnet run --file scripts/Run-DebuggerTests.cs " +
    "[--supervisor-timeout-seconds <seconds>] <dotnet-test-options>";
if (args.Length != 0 && string.Equals(args[0], "--watchdog", StringComparison.Ordinal))
{
    return await RunWatchdogAsync(args).ConfigureAwait(false);
}

if (args.Length == 1 && args[0] is "--help" or "-h" or "-?")
{
    await Console.Out.WriteLineAsync(
        "Runs debugger tests under a bounded process-tree supervisor.").ConfigureAwait(false);
    await Console.Out.WriteLineAsync(usage).ConfigureAwait(false);
    return 0;
}

var deadline = TimeSpan.FromSeconds(150);
int firstTestArgument = 0;
if (args.Length != 0 && string.Equals(args[0], "--supervisor-timeout-seconds", StringComparison.Ordinal))
{
    if (args.Length < 2 ||
        !int.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out int timeoutSeconds) ||
        timeoutSeconds <= 0)
    {
        await Console.Error.WriteLineAsync(
            "--supervisor-timeout-seconds requires a positive whole number.").ConfigureAwait(false);
        return 2;
    }

    deadline = TimeSpan.FromSeconds(timeoutSeconds);
    firstTestArgument = 2;
}
string resultsDirectory = ResolveResultsDirectory(args, firstTestArgument);

var startInfo = new ProcessStartInfo
{
    FileName = ResolveDotNetHost(),
    RedirectStandardError = true,
    RedirectStandardOutput = true,
    UseShellExecute = false
};
startInfo.ArgumentList.Add("test");
for (int index = firstTestArgument; index < args.Length; index++)
{
    startInfo.ArgumentList.Add(args[index]);
}

using Process process = Process.Start(startInfo)
    ?? throw new InvalidOperationException("The debugger test process did not start.");
StartWatchdog(process, deadline, resultsDirectory);
Task exit = process.WaitForExitAsync();
var deadlineElapsed = Task.Delay(deadline);
Task<string> standardOutput = Task.Run(() => ProcessOutputCapture.ReadAsync(
    process.StandardOutput.BaseStream,
    process.StandardOutput.CurrentEncoding,
    Console.Out));
Task<string> standardError = Task.Run(() => ProcessOutputCapture.ReadAsync(
    process.StandardError.BaseStream,
    process.StandardError.CurrentEncoding,
    Console.Error));
var execution = Task.WhenAll(exit, standardOutput, standardError);
if (await Task.WhenAny(execution, deadlineElapsed).ConfigureAwait(false) == execution)
{
    await execution.ConfigureAwait(false);
    return process.ExitCode;
}

string timeoutMessage = FormattableString.Invariant(
    $"Debugger tests exceeded the supervisor deadline of {deadline.TotalSeconds:F0} seconds. Root PID: {process.Id}.");
string processSnapshot = await CaptureProcessSnapshotAsync().ConfigureAwait(false);
await PreserveTimeoutEvidenceAsync(resultsDirectory, timeoutMessage, processSnapshot).ConfigureAwait(false);
await ReportAsync(timeoutMessage).ConfigureAwait(false);
await ReportAsync(processSnapshot).ConfigureAwait(false);
string? terminationFailure = TerminateProcessTree(process, processSnapshot);
if (terminationFailure is not null)
{
    await ReportAsync(terminationFailure).ConfigureAwait(false);
}

try
{
    await exit.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
}
catch (TimeoutException)
{
    await ReportAsync("Debugger test process did not exit within five seconds after termination.")
        .ConfigureAwait(false);
}

await DrainOutputAsync(standardOutput, standardError).ConfigureAwait(false);
return 124;

static string ResolveDotNetHost()
{
    string? configured = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
    return string.IsNullOrWhiteSpace(configured) ? "dotnet" : configured;
}

static void StartWatchdog(Process testProcess, TimeSpan deadline, string resultsDirectory)
{
    string supervisorAssembly = Path.GetFullPath(Environment.GetCommandLineArgs()[0]);
    using var supervisor = Process.GetCurrentProcess();
    var watchdogInfo = new ProcessStartInfo
    {
        FileName = ResolveDotNetHost(),
        UseShellExecute = false
    };
    watchdogInfo.ArgumentList.Add(supervisorAssembly);
    watchdogInfo.ArgumentList.Add("--watchdog");
    watchdogInfo.ArgumentList.Add(supervisor.Id.ToString(CultureInfo.InvariantCulture));
    watchdogInfo.ArgumentList.Add(supervisor.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture));
    watchdogInfo.ArgumentList.Add(testProcess.Id.ToString(CultureInfo.InvariantCulture));
    watchdogInfo.ArgumentList.Add(testProcess.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture));
    watchdogInfo.ArgumentList.Add(((int)deadline.TotalSeconds + 15).ToString(CultureInfo.InvariantCulture));
    watchdogInfo.ArgumentList.Add(resultsDirectory);

    using Process watchdog = Process.Start(watchdogInfo)
        ?? throw new InvalidOperationException("The debugger test watchdog did not start.");
}

static async Task<int> RunWatchdogAsync(string[] arguments)
{
    if (arguments.Length != 7 ||
        !TryParsePositiveInt(arguments[1], out int supervisorProcessId) ||
        !TryParsePositiveLong(arguments[2], out long supervisorStartTimeTicks) ||
        !TryParsePositiveInt(arguments[3], out int testProcessId) ||
        !TryParsePositiveLong(arguments[4], out long testStartTimeTicks) ||
        !TryParsePositiveInt(arguments[5], out int timeoutSeconds))
    {
        await ReportAsync("The debugger test watchdog received invalid arguments.").ConfigureAwait(false);
        return 2;
    }

    var deadline = TimeSpan.FromSeconds(timeoutSeconds);
    long startedTimestamp = Stopwatch.GetTimestamp();
    while (Stopwatch.GetElapsedTime(startedTimestamp) < deadline)
    {
        if (!MatchesProcessIdentity(supervisorProcessId, supervisorStartTimeTicks))
        {
            return 0;
        }

        TimeSpan remaining = deadline - Stopwatch.GetElapsedTime(startedTimestamp);
        TimeSpan pollingDelay = remaining < TimeSpan.FromMilliseconds(250)
            ? remaining
            : TimeSpan.FromMilliseconds(250);
        await Task.Delay(pollingDelay).ConfigureAwait(false);
    }

    if (!MatchesProcessIdentity(supervisorProcessId, supervisorStartTimeTicks))
    {
        return 0;
    }

    string timeoutMessage = FormattableString.Invariant(
        $"Debugger test supervisor remained active after {deadline.TotalSeconds:F0} seconds. Supervisor PID: {supervisorProcessId}; test PID: {testProcessId}.");
    string processSnapshot = await CaptureProcessSnapshotAsync().ConfigureAwait(false);
    await PreserveTimeoutEvidenceAsync(arguments[6], timeoutMessage, processSnapshot).ConfigureAwait(false);
    await ReportAsync(timeoutMessage).ConfigureAwait(false);
    await ReportAsync(processSnapshot).ConfigureAwait(false);

    var failures = new List<string>();
    if (MatchesProcessIdentity(testProcessId, testStartTimeTicks))
    {
        TerminateProcessTreeById(testProcessId, processSnapshot, failures);
    }

    TerminateSingleProcess(supervisorProcessId, failures);
    if (failures.Count != 0)
    {
        await ReportAsync(
            $"Debugger test watchdog termination was incomplete: {string.Join("; ", failures)}")
            .ConfigureAwait(false);
    }

    return 124;
}

static bool TryParsePositiveInt(string value, out int result) =>
    int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out result) && result > 0;

static bool TryParsePositiveLong(string value, out long result) =>
    long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out result) && result > 0;

static bool MatchesProcessIdentity(int processId, long startTimeTicks)
{
    try
    {
        using var process = Process.GetProcessById(processId);
        long observedStartTimeTicks = process.StartTime.ToUniversalTime().Ticks;
        long difference = Math.Abs(observedStartTimeTicks - startTimeTicks);
        return difference <= TimeSpan.FromSeconds(1).Ticks && !process.HasExited;
    }
    catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or Win32Exception)
    {
        return false;
    }
}

static string ResolveResultsDirectory(string[] arguments, int firstTestArgument)
{
    for (int index = firstTestArgument; index + 1 < arguments.Length; index++)
    {
        if (string.Equals(arguments[index], "--results-directory", StringComparison.Ordinal))
        {
            return arguments[index + 1];
        }
    }

    return Path.Join("artifacts", "test-results");
}

static async Task<string> CaptureProcessSnapshotAsync()
{
    ProcessStartInfo? snapshotInfo = CreateProcessSnapshotStartInfo();
    if (snapshotInfo is null)
    {
        return "A native process snapshot is unavailable on this platform.";
    }

    using Process snapshot = Process.Start(snapshotInfo)
        ?? throw new InvalidOperationException("The process snapshot command did not start.");
    Task<string> output = snapshot.StandardOutput.ReadToEndAsync(CancellationToken.None);
    Task<string> error = snapshot.StandardError.ReadToEndAsync(CancellationToken.None);
    try
    {
        await snapshot.WaitForExitAsync(CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None)
            .ConfigureAwait(false);
    }
    catch (TimeoutException)
    {
        snapshot.Kill();
        return "The native process snapshot command timed out.";
    }

    string result = await output.ConfigureAwait(false);
    string failure = await error.ConfigureAwait(false);
    return snapshot.ExitCode == 0
        ? result
        : FormattableString.Invariant(
            $"The native process snapshot command exited with code {snapshot.ExitCode}: {failure}");
}

static ProcessStartInfo? CreateProcessSnapshotStartInfo()
{
    if (OperatingSystem.IsWindows())
    {
        ProcessStartInfo windows = CreateRedirectedStartInfo("tasklist.exe");
        windows.ArgumentList.Add("/FO");
        windows.ArgumentList.Add("CSV");
        return windows;
    }

    if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
    {
        ProcessStartInfo unix = CreateRedirectedStartInfo("/bin/ps");
        unix.ArgumentList.Add("-axo");
        unix.ArgumentList.Add("pid=,ppid=,stat=,%cpu=,etime=,rss=,vsz=,comm=");
        return unix;
    }

    return null;
}

static ProcessStartInfo CreateRedirectedStartInfo(string fileName) => new()
{
    FileName = fileName,
    RedirectStandardError = true,
    RedirectStandardOutput = true,
    UseShellExecute = false
};

static async Task PreserveTimeoutEvidenceAsync(string directory, string message, string processSnapshot)
{
    try
    {
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(
            Path.Join(directory, "debugger-test-supervisor-timeout.txt"),
            $"{message}{Environment.NewLine}{processSnapshot}",
            Encoding.UTF8,
            CancellationToken.None).ConfigureAwait(false);
    }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
    {
        await ReportAsync($"Could not preserve debugger timeout evidence: {exception.Message}")
            .ConfigureAwait(false);
    }
}

static string? TerminateProcessTree(Process process, string processSnapshot)
{
    var failures = new List<string>();
    if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
    {
        TerminateProcessTreeById(process.Id, processSnapshot, failures);
        return failures.Count == 0
            ? null
            : $"Debugger test process-tree termination was incomplete: {string.Join("; ", failures)}";
    }

    try
    {
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
        }
    }
    catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
    {
        failures.Add(exception.Message);
    }

    return failures.Count == 0
        ? null
        : $"Debugger test process-tree termination failed: {string.Join("; ", failures)}";
}

static void TerminateProcessTreeById(int rootProcessId, string processSnapshot, List<string> failures)
{
    foreach (int processId in FindDescendantProcessIds(processSnapshot, rootProcessId))
    {
        TerminateSingleProcess(processId, failures);
    }

    TerminateSingleProcess(rootProcessId, failures);
}

static IReadOnlyList<int> FindDescendantProcessIds(string processSnapshot, int rootProcessId)
{
    var childrenByParent = new Dictionary<int, List<int>>();
    foreach (string line in processSnapshot.Split('\n', StringSplitOptions.RemoveEmptyEntries))
    {
        string[] columns = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (columns.Length < 2 ||
            !int.TryParse(columns[0], NumberStyles.None, CultureInfo.InvariantCulture, out int processId) ||
            !int.TryParse(columns[1], NumberStyles.None, CultureInfo.InvariantCulture, out int parentProcessId) ||
            processId == Environment.ProcessId)
        {
            continue;
        }

        if (!childrenByParent.TryGetValue(parentProcessId, out List<int>? children))
        {
            children = [];
            childrenByParent.Add(parentProcessId, children);
        }

        children.Add(processId);
    }

    var descendants = new List<int>();
    var pending = new Stack<int>();
    pending.Push(rootProcessId);
    while (pending.TryPop(out int parentProcessId))
    {
        if (!childrenByParent.TryGetValue(parentProcessId, out List<int>? children))
        {
            continue;
        }

        foreach (int childProcessId in children)
        {
            descendants.Add(childProcessId);
            pending.Push(childProcessId);
        }
    }

    descendants.Reverse();
    return descendants;
}

static void TerminateSingleProcess(int processId, List<string> failures)
{
    try
    {
        using var target = Process.GetProcessById(processId);
        target.Kill();
    }
    catch (ArgumentException)
    {
        // The process exited between the snapshot and the termination pass.
        return;
    }
    catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
    {
        failures.Add(FormattableString.Invariant($"PID {processId}: {exception.Message}"));
    }
}

static async Task DrainOutputAsync(Task<string> standardOutput, Task<string> standardError)
{
    try
    {
        await Task.WhenAll(standardOutput, standardError)
            .WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None)
            .ConfigureAwait(false);
    }
    catch (TimeoutException)
    {
        await ReportAsync("Debugger test output did not close within five seconds after termination.")
            .ConfigureAwait(false);
    }
}

static async Task ReportAsync(string message)
{
    try
    {
        await Console.Error.WriteLineAsync(message)
            .WaitAsync(TimeSpan.FromSeconds(1), CancellationToken.None)
            .ConfigureAwait(false);
    }
    catch (Exception exception) when (exception is IOException or ObjectDisposedException or TimeoutException)
    {
        // The evidence file remains authoritative when the runner log stream is unavailable.
        return;
    }
}
