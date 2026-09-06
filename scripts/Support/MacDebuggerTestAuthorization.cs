using System.Diagnostics;
using System.Xml;
using System.Xml.Linq;

namespace Csls.Support;

/// <summary>
/// Preserves and restores task-port authorization around tests on disposable hosted macOS runners.
/// </summary>
internal static class MacDebuggerTestAuthorization
{
    private const string Right = "system.privilege.taskport";

    /// <summary>
    /// Saves the original policy and removes interactive authentication while retaining its group restriction.
    /// </summary>
    internal static async Task EnableAsync(CancellationToken cancellationToken)
    {
        string backupPath = GetBackupPath();
        string original = await ReadAsync(cancellationToken).ConfigureAwait(false);
        XDocument policy = Parse(original);
        XElement authentication = GetValue(policy, "authenticate-user");
        authentication.Name = "false";

        // CreateNew preserves an earlier backup if preparation is accidentally invoked twice.
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Options = FileOptions.Asynchronous
        };
        if (OperatingSystem.IsMacOS())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        var backup = new FileStream(backupPath, options);
        await using (backup.ConfigureAwait(false))
        {
            var writer = new StreamWriter(backup);
            await using (writer.ConfigureAwait(false))
            {
                await writer.WriteAsync(original.AsMemory(), cancellationToken).ConfigureAwait(false);
            }
        }

        await WriteAsync(policy.ToString(), cancellationToken).ConfigureAwait(false);
        Verify(policy, Parse(await ReadAsync(cancellationToken).ConfigureAwait(false)));
        await Console.Out.WriteLineAsync("Debugger authorization is enabled for the runner's developer group.")
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Restores and verifies the saved policy before removing the job-owned backup file.
    /// </summary>
    internal static async Task RestoreAsync(CancellationToken cancellationToken)
    {
        string backupPath = GetBackupPath();
        if (!File.Exists(backupPath))
        {
            await Console.Out.WriteLineAsync("Debugger authorization was not changed by this job.").ConfigureAwait(false);
            return;
        }

        string original = await File.ReadAllTextAsync(backupPath, cancellationToken).ConfigureAwait(false);
        XDocument policy = Parse(original);
        await WriteAsync(original, cancellationToken).ConfigureAwait(false);
        Verify(policy, Parse(await ReadAsync(cancellationToken).ConfigureAwait(false)));
        File.Delete(backupPath);
        await Console.Out.WriteLineAsync("The original debugger authorization policy is restored.").ConfigureAwait(false);
    }

    private static string GetBackupPath()
    {
        if (!OperatingSystem.IsMacOS() ||
            Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true" ||
            Environment.GetEnvironmentVariable("RUNNER_ENVIRONMENT") != "github-hosted")
        {
            throw new InvalidOperationException("Debugger authorization changes require a GitHub-hosted macOS runner.");
        }

        string? temporaryDirectory = Environment.GetEnvironmentVariable("RUNNER_TEMP");
        if (string.IsNullOrWhiteSpace(temporaryDirectory) ||
            !Path.IsPathFullyQualified(temporaryDirectory) || !Directory.Exists(temporaryDirectory) ||
            Path.GetFullPath(temporaryDirectory) == Path.GetPathRoot(temporaryDirectory))
        {
            throw new InvalidOperationException("RUNNER_TEMP must identify the existing runner temporary directory.");
        }

        return Path.Join(temporaryDirectory, "csls-debugger-taskport-original.plist");
    }

    private static XDocument Parse(string text)
    {
        using var input = new StringReader(text);
        using var reader = XmlReader.Create(input, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore,
            XmlResolver = null,
            MaxCharactersInDocument = 65536
        });
        var policy = XDocument.Load(reader);
        if (policy.Root?.Name != "plist" || policy.Root.Elements().SingleOrDefault()?.Name != "dict" ||
            GetValue(policy, "class").Value != "user" || GetValue(policy, "group").Value != "_developer" ||
            GetValue(policy, "authenticate-user").Name.LocalName is not ("true" or "false"))
        {
            throw new InvalidOperationException("The task-port policy must authorize the developer group using a user rule.");
        }

        return policy;
    }

    private static XElement GetValue(XDocument policy, string key)
    {
        XElement? name = policy.Root?.Element("dict")?.Elements("key")
            .SingleOrDefault(element => element.Value == key);
        return name?.NextNode as XElement ??
            throw new InvalidOperationException($"The task-port policy has no value for '{key}'.");
    }

    private static void Verify(XDocument expected, XDocument actual)
    {
        // Authorization Services maintains these timestamps when writing the database.
        string[] expectedKeys = GetPolicyKeys(expected);
        string[] actualKeys = GetPolicyKeys(actual);
        if (!expectedKeys.SequenceEqual(actualKeys, StringComparer.Ordinal) ||
            expectedKeys.Any(key => !XNode.DeepEquals(GetValue(expected, key), GetValue(actual, key))))
        {
            throw new InvalidOperationException("The task-port authorization policy did not match after writing it.");
        }
    }

    private static string[] GetPolicyKeys(XDocument policy) =>
        policy.Root?.Element("dict")?.Elements("key").Select(element => element.Value)
            .Where(key => key is not ("created" or "modified")).Order(StringComparer.Ordinal).ToArray() ?? [];

    private static Task<string> ReadAsync(CancellationToken cancellationToken) =>
        RunAsync("/usr/bin/security", ["authorizationdb", "read", Right], null, cancellationToken);

    private static Task<string> WriteAsync(string policy, CancellationToken cancellationToken) =>
        RunAsync("/usr/bin/sudo", ["-n", "/usr/bin/security", "authorizationdb", "write", Right], policy, cancellationToken);

    private static async Task<string> RunAsync(
        string executable, string[] arguments, string? input, CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        foreach (string argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.Start();
        Task<string> output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> error = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            if (input is not null)
            {
                await process.StandardInput.WriteAsync(input.AsMemory(), cancellationToken).ConfigureAwait(false);
            }

            process.StandardInput.Close();
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await Task.WhenAll(output, error).ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"{executable} exited with {process.ExitCode}: {await error.ConfigureAwait(false)}");
            }

            return await output.ConfigureAwait(false);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }

            await Task.WhenAll(output, error).ConfigureAwait(false);
        }
    }
}
