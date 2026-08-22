using System.Diagnostics;

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
    foreach ((string name, string value) in environment)
    {
        Environment.SetEnvironmentVariable(name, value);
    }

    UnixProcess.Execute(startInfo.FileName, startInfo.ArgumentList);
    throw new UnreachableException("execvp returned after replacing the test host.");
}

using Process process = Process.Start(startInfo)
    ?? throw new InvalidOperationException("The hosted process did not start.");
await process.WaitForExitAsync().ConfigureAwait(false);
return process.ExitCode;
