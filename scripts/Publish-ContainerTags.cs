#!/usr/bin/env -S dotnet --
#:property TargetFramework=net10.0
#:property LangVersion=14.0
#:property Nullable=enable
#:property TreatWarningsAsErrors=true

using System.Diagnostics;

if (args.Length == 1 && args[0] is "--help" or "-h" or "-?")
{
    await Console.Out.WriteLineAsync(
        "Advances stable GHCR tags to verified immutable image digests.")
        .ConfigureAwait(false);
    await Console.Out.WriteLineAsync(
        "Usage: dotnet run --file scripts/Publish-ContainerTags.cs -- " +
        "--csls-digest <sha256:digest> --csls-mcp-digest <sha256:digest>")
        .ConfigureAwait(false);
    return 0;
}

if (!TryReadOption(args, "--csls-digest", out string? cslsDigest) ||
    !TryReadOption(args, "--csls-mcp-digest", out string? cslsMcpDigest) ||
    args.Length != 4)
{
    await Console.Error.WriteLineAsync(
        "Usage: dotnet run --file scripts/Publish-ContainerTags.cs -- " +
        "--csls-digest <sha256:digest> --csls-mcp-digest <sha256:digest>")
        .ConfigureAwait(false);
    return 2;
}

if (cslsDigest is null ||
    cslsMcpDigest is null ||
    !IsDigest(cslsDigest) ||
    !IsDigest(cslsMcpDigest))
{
    await Console.Error.WriteLineAsync("Both container digests must use sha256.")
        .ConfigureAwait(false);
    return 2;
}

try
{
    await AdvanceTagAsync("csls", cslsDigest).ConfigureAwait(false);
    await AdvanceTagAsync("csls-mcp", cslsMcpDigest).ConfigureAwait(false);
    return 0;
}
catch (InvalidOperationException exception)
{
    await Console.Error.WriteLineAsync(exception.Message).ConfigureAwait(false);
    return 1;
}

static bool TryReadOption(
    IReadOnlyList<string> arguments,
    string option,
    out string? value)
{
    int index = -1;
    for (int candidate = 0; candidate < arguments.Count; candidate++)
    {
        if (string.Equals(arguments[candidate], option, StringComparison.Ordinal))
        {
            index = candidate;
            break;
        }
    }

    if (index < 0 || index == arguments.Count - 1)
    {
        value = null;
        return false;
    }

    value = arguments[index + 1];
    return true;
}

static bool IsDigest(string value) =>
    value.Length == 71 &&
    value.StartsWith("sha256:", StringComparison.Ordinal) &&
    value.AsSpan(7).ToString().All(static character =>
        character is >= '0' and <= '9' or >= 'a' and <= 'f');

static async Task AdvanceTagAsync(string packageId, string digest)
{
    string image = $"ghcr.io/willibrandon/{packageId}";
    var startInfo = new ProcessStartInfo
    {
        FileName = "docker",
        RedirectStandardError = true,
        RedirectStandardOutput = true,
        UseShellExecute = false
    };
    startInfo.ArgumentList.Add("buildx");
    startInfo.ArgumentList.Add("imagetools");
    startInfo.ArgumentList.Add("create");
    startInfo.ArgumentList.Add("--tag");
    startInfo.ArgumentList.Add($"{image}:latest");
    startInfo.ArgumentList.Add($"{image}@{digest}");

    using Process process = Process.Start(startInfo)
        ?? throw new InvalidOperationException("Docker did not start.");
    Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
    Task<string> errorTask = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync().ConfigureAwait(false);
    string output = await outputTask.ConfigureAwait(false);
    string error = await errorTask.ConfigureAwait(false);
    if (process.ExitCode != 0)
    {
        throw new InvalidOperationException(
            $"Docker could not advance {image}:latest.{Environment.NewLine}" +
            $"{output}{Environment.NewLine}{error}");
    }

    await Console.Out.WriteLineAsync($"{image}:latest -> {digest}")
        .ConfigureAwait(false);
}
