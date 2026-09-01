using System.Diagnostics;

if (args is ["--print-environment", string environmentVariable])
{
    await Console.Out.WriteAsync(
        Environment.GetEnvironmentVariable(environmentVariable) ?? string.Empty).ConfigureAwait(false);
    return 0;
}

if (args is ["--wait-for-standard-input"])
{
    _ = await Console.In.ReadToEndAsync().ConfigureAwait(false);
    return 0;
}

if (args is ["--wait-for-file", string waitPath])
{
    await WaitForFileAsync(waitPath).ConfigureAwait(false);
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
