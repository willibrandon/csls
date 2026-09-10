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
Task<string> standardOutput = ProcessOutputCapture.ReadAsync(
    process.StandardOutput.BaseStream,
    process.StandardOutput.CurrentEncoding,
    Console.Out);
Task<string> standardError = ProcessOutputCapture.ReadAsync(
    process.StandardError.BaseStream,
    process.StandardError.CurrentEncoding,
    Console.Error);
Task exit = process.WaitForExitAsync();
if (await Task.WhenAny(exit, Task.Delay(deadline)).ConfigureAwait(false) == exit)
{
    await Task.WhenAll(exit, standardOutput, standardError).ConfigureAwait(false);
    return process.ExitCode;
}

string timeoutMessage = FormattableString.Invariant(
    $"Debugger tests exceeded the supervisor deadline of {deadline.TotalSeconds:F0} seconds. Root PID: {process.Id}.");
await Console.Error.WriteLineAsync(timeoutMessage).ConfigureAwait(false);
string processSnapshot = await CaptureProcessSnapshotAsync().ConfigureAwait(false);
await PreserveTimeoutEvidenceAsync(timeoutMessage, processSnapshot).ConfigureAwait(false);
await Console.Error.WriteLineAsync(processSnapshot).ConfigureAwait(false);

var termination = Task.Run(() => TerminateProcessTree(process));
if (await Task.WhenAny(termination, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false) == termination)
{
    await termination.ConfigureAwait(false);
}
else
{
    await Console.Error.WriteLineAsync("Debugger test process-tree termination did not return within five seconds.")
        .ConfigureAwait(false);
}

try
{
    await exit.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
}
catch (TimeoutException)
{
    await Console.Error.WriteLineAsync("Debugger test process did not exit within five seconds after termination.")
        .ConfigureAwait(false);
}

await DrainOutputAsync(standardOutput, standardError).ConfigureAwait(false);
return 124;

static string ResolveDotNetHost()
{
    string? configured = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
    return string.IsNullOrWhiteSpace(configured) ? "dotnet" : configured;
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
        snapshot.Kill(entireProcessTree: true);
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

static async Task PreserveTimeoutEvidenceAsync(string message, string processSnapshot)
{
    string directory = Path.Join("artifacts", "test-results");
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
        await Console.Error.WriteLineAsync($"Could not preserve debugger timeout evidence: {exception.Message}")
            .ConfigureAwait(false);
    }
}

static void TerminateProcessTree(Process process)
{
    try
    {
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
        }
    }
    catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
    {
        if (!process.HasExited)
        {
            Console.Error.WriteLine($"Debugger test process-tree termination failed: {exception.Message}");
        }
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
        await Console.Error.WriteLineAsync("Debugger test output did not close within five seconds after termination.")
            .ConfigureAwait(false);
    }
}
