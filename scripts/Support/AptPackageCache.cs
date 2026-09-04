using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace Csls.Support;

/// <summary>
/// Reuses browser dependency archives verified against the host's authenticated APT metadata.
/// </summary>
[SupportedOSPlatform("linux")]
internal static class AptPackageCache
{
    /// <summary>
    /// Writes a cache key derived from the runner and APT's complete pending archive plan.
    /// </summary>
    internal static async Task WriteCacheKeyAsync(IReadOnlyList<string> packages)
    {
        string outputPath = Environment.GetEnvironmentVariable("GITHUB_OUTPUT")
            ?? throw new InvalidOperationException("GITHUB_OUTPUT is required for the package cache key.");
        Dictionary<string, (long Size, string Hash)> archives = await ResolveArchivesAsync(packages).ConfigureAwait(false);
        string architecture = await RunAsync("dpkg", ["--print-architecture"]).ConfigureAwait(false);
        string operatingSystem = await File.ReadAllTextAsync("/etc/os-release").ConfigureAwait(false);
        string identity = string.Join('\n',
        [
            architecture,
            operatingSystem,
            Environment.GetEnvironmentVariable("ImageOS"),
            Environment.GetEnvironmentVariable("ImageVersion"),
            .. packages.Order(StringComparer.Ordinal),
            .. archives.OrderBy(static pair => pair.Key, StringComparer.Ordinal)
                .Select(static pair => FormattableString.Invariant($"{pair.Key} {pair.Value.Size} {pair.Value.Hash}"))
        ]);
        string digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        await File.AppendAllTextAsync(outputPath, $"key={digest}{Environment.NewLine}")
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Downloads dependencies or installs them through APT after verifying reusable archives.
    /// </summary>
    internal static async Task ProvisionAsync(
        IReadOnlyList<string> packages,
        string cachePath,
        bool downloadOnly)
    {
        Directory.CreateDirectory(cachePath);
        if (new DirectoryInfo(cachePath).LinkTarget is not null)
        {
            throw new InvalidDataException("The package cache directory must not be a symbolic link.");
        }

        Dictionary<string, (long Size, string Hash)> archives = await ResolveArchivesAsync(packages).ConfigureAwait(false);
        string stagingPath = Directory.CreateTempSubdirectory("csls-browser-apt-").FullName;
        try
        {
            File.SetUnixFileMode(stagingPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            foreach ((string fileName, (long size, string hash)) in archives)
            {
                string cachedPath = Path.Join(cachePath, fileName);
                if (new FileInfo(cachedPath).LinkTarget is not null)
                {
                    throw new InvalidDataException($"Cached package must not be a symbolic link: {fileName}.");
                }

                if (!File.Exists(cachedPath))
                {
                    continue;
                }

                if (new FileInfo(cachedPath).Length != size)
                {
                    throw new InvalidDataException($"Cached package has an unexpected size: {fileName}.");
                }

                string stagedPath = Path.Join(stagingPath, fileName);
                File.Copy(cachedPath, stagedPath);
                await VerifyArchiveAsync(stagedPath, hash).ConfigureAwait(false);
            }

            // APT's own acquisition verifies newly downloaded archives. Existing archive
            // entries need the explicit hash check above before APT accepts their size.
            List<string> installArguments =
            [
                .. CreateInstallArguments(packages, stagingPath)
            ];
            string manifestPath = Path.Join(cachePath, "packages.txt");
            if (downloadOnly)
            {
                if (new FileInfo(manifestPath).LinkTarget is not null)
                {
                    throw new InvalidDataException("The package cache manifest must not be a symbolic link.");
                }

                installArguments.Add("--download-only");
            }

            await RunAsync("apt-get", installArguments, privileged: true, writeOutput: true)
                .ConfigureAwait(false);

            if (downloadOnly)
            {
                foreach ((string fileName, (_, string hash)) in archives)
                {
                    string stagedPath = Path.Join(stagingPath, fileName);
                    await VerifyArchiveAsync(stagedPath, hash).ConfigureAwait(false);
                    File.Copy(stagedPath, Path.Join(cachePath, fileName), overwrite: true);
                }

                await File.WriteAllLinesAsync(manifestPath,
                    archives.OrderBy(static pair => pair.Key, StringComparer.Ordinal)
                        .Select(static pair => $"{pair.Value.Hash}  {pair.Key}"))
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            // Only the unique operation-owned directory is removed, never the shared cache.
            await RunAsync("rm", ["--recursive", "--force", "--", stagingPath], privileged: true)
                .ConfigureAwait(false);
        }
    }

    private static async Task<Dictionary<string, (long Size, string Hash)>> ResolveArchivesAsync(IReadOnlyList<string> packages)
    {
        string probePath = Directory.CreateTempSubdirectory("csls-browser-apt-plan-").FullName;
        try
        {
            string output = await RunAsync("apt-get",
            [
                .. CreateInstallArguments(packages, probePath),
                "--print-uris",
                "--option", "Debug::NoLocking=true",
                "--option", "Acquire::ForceHash=SHA256"
            ]).ConfigureAwait(false);
            var archives = new Dictionary<string, (long Size, string Hash)>(StringComparer.Ordinal);
            foreach (string line in output.Split('\n'))
            {
                if (!line.StartsWith('\''))
                {
                    continue;
                }

                string[] fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (fields.Length != 4 ||
                    fields[1] != Path.GetFileName(fields[1]) ||
                    !fields[1].EndsWith(".deb", StringComparison.Ordinal) ||
                    !long.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out long size) ||
                    size <= 0 ||
                    !fields[3].StartsWith("SHA256:", StringComparison.Ordinal) ||
                    fields[3].Length != 71 ||
                    !fields[3][7..].All(char.IsAsciiHexDigit))
                {
                    throw new InvalidDataException("APT returned an invalid package archive or missing SHA-256 digest.");
                }

                if (!archives.TryAdd(fields[1], (size, fields[3][7..])))
                {
                    throw new InvalidDataException($"APT returned a duplicate archive: {fields[1]}.");
                }
            }

            return archives;
        }
        finally
        {
            Directory.Delete(probePath, recursive: true);
        }
    }

    private static string[] CreateInstallArguments(IReadOnlyList<string> packages, string archivesPath) =>
    [
        "install", "--yes", "--no-install-recommends", "--no-remove",
        "--option", $"Dir::Cache::archives={archivesPath}",
        .. packages
    ];

    private static async Task VerifyArchiveAsync(string path, string expectedHash)
    {
        using FileStream archive = File.OpenRead(path);
        string actualHash = Convert.ToHexStringLower(await SHA256.HashDataAsync(archive).ConfigureAwait(false));
        if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Cached package failed SHA-256 verification: {Path.GetFileName(path)}.");
        }
    }

    private static async Task<string> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        bool privileged = false,
        bool writeOutput = false)
    {
        bool useSudo = privileged && !string.Equals(Environment.UserName, "root", StringComparison.Ordinal);
        var startInfo = new ProcessStartInfo(useSudo ? "sudo" : executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        if (useSudo)
        {
            startInfo.ArgumentList.Add("--non-interactive");
            startInfo.ArgumentList.Add(executable);
        }

        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment["DEBIAN_FRONTEND"] = "noninteractive";
        startInfo.Environment["LC_ALL"] = "C";
        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start {executable}.");
        Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
        Task<string> errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().ConfigureAwait(false);
        string output = await outputTask.ConfigureAwait(false);
        string error = await errorTask.ConfigureAwait(false);
        if (writeOutput)
        {
            await Console.Out.WriteAsync(output).ConfigureAwait(false);
        }

        await Console.Error.WriteAsync(error).ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"{executable} failed with exit code {process.ExitCode}: {output}");
        }

        return output;
    }
}
