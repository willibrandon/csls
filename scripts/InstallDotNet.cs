#:property PublishAot=false
#:property PackAsTool=false

using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

const string SdkVersion = "10.0.400";
var releasesUri = new Uri("https://dotnetcli.blob.core.windows.net/dotnet/release-metadata/10.0/releases.json");

if (args.Length == 1 && args[0] is "--help" or "-h" or "-?")
{
    await Console.Out.WriteLineAsync(
        "Installs the pinned .NET SDK from verified Microsoft release metadata.")
        .ConfigureAwait(false);
    await Console.Out.WriteLineAsync(
        "Usage: dotnet run --file scripts/InstallDotNet.cs [--runtime <rid>]")
        .ConfigureAwait(false);
    return;
}

string? requestedRuntimeIdentifier = null;
for (int argumentIndex = 0; argumentIndex < args.Length; argumentIndex += 2)
{
    if (argumentIndex + 1 >= args.Length ||
        !string.Equals(args[argumentIndex], "--runtime", StringComparison.Ordinal))
    {
        await Console.Error.WriteLineAsync(
            "Usage: dotnet run --file scripts/InstallDotNet.cs [--runtime <rid>]")
            .ConfigureAwait(false);
        Environment.ExitCode = 2;
        return;
    }

    requestedRuntimeIdentifier = args[argumentIndex + 1];
}

string repositoryRoot = Path.GetFullPath(Path.Join(GetScriptDirectory(), ".."));
string runtimeIdentifier = requestedRuntimeIdentifier ?? GetHostRuntimeIdentifier();
string installDirectory = requestedRuntimeIdentifier is null
    ? Path.Join(repositoryRoot, ".dotnet")
    : Path.Join(repositoryRoot, "artifacts", "tools", "dotnet", runtimeIdentifier);
string installedDotNet = Path.Join(
    installDirectory,
    OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");

if (File.Exists(installedDotNet))
{
    string? installedVersion = await ReadSdkVersionAsync(installedDotNet).ConfigureAwait(false);
    if (string.Equals(installedVersion, SdkVersion, StringComparison.Ordinal))
    {
        await Console.Out.WriteLineAsync(
            $".NET SDK {SdkVersion} is already installed at {installDirectory}.")
            .ConfigureAwait(false);
        return;
    }
}

using HttpClient http = new() { Timeout = TimeSpan.FromMinutes(10) };
using HttpResponseMessage releasesResponse = await http
    .GetAsync(releasesUri, HttpCompletionOption.ResponseHeadersRead)
    .ConfigureAwait(false);
releasesResponse.EnsureSuccessStatusCode();
using Stream releasesStream = await releasesResponse.Content.ReadAsStreamAsync().ConfigureAwait(false);
using JsonDocument releases = await JsonDocument.ParseAsync(releasesStream).ConfigureAwait(false);
(Uri Url, string Hash, string Extension) archive = FindSdkArchive(
    releases.RootElement,
    runtimeIdentifier);

string temporaryArchive = Path.Join(
    Path.GetTempPath(),
    $"csls-dotnet-sdk-{SdkVersion}-{Guid.NewGuid():N}{archive.Extension}");

try
{
    await Console.Out.WriteLineAsync(
        $"Downloading .NET SDK {SdkVersion} for {runtimeIdentifier}...")
        .ConfigureAwait(false);
    using HttpResponseMessage archiveResponse = await http
        .GetAsync(archive.Url, HttpCompletionOption.ResponseHeadersRead)
        .ConfigureAwait(false);
    archiveResponse.EnsureSuccessStatusCode();
    using (Stream source = await archiveResponse.Content.ReadAsStreamAsync().ConfigureAwait(false))
    using (FileStream destination = File.Create(temporaryArchive))
    {
        await source.CopyToAsync(destination).ConfigureAwait(false);
    }

    string actualHash = Convert.ToHexString(
        SHA512.HashData(await File.ReadAllBytesAsync(temporaryArchive).ConfigureAwait(false)));
    if (!string.Equals(actualHash, archive.Hash, StringComparison.OrdinalIgnoreCase))
    {
        throw new InvalidDataException("The downloaded SDK archive failed SHA-512 verification.");
    }

    Directory.CreateDirectory(installDirectory);
    if (archive.Extension == ".zip")
    {
        await ZipFile.ExtractToDirectoryAsync(
            temporaryArchive,
            installDirectory,
            overwriteFiles: true,
            CancellationToken.None).ConfigureAwait(false);
    }
    else
    {
        using FileStream file = File.OpenRead(temporaryArchive);
        using GZipStream gzip = new(file, CompressionMode.Decompress);
        await TarFile.ExtractToDirectoryAsync(
            gzip,
            installDirectory,
            overwriteFiles: true,
            CancellationToken.None).ConfigureAwait(false);
    }

    string? verifiedVersion = await ReadSdkVersionAsync(installedDotNet).ConfigureAwait(false);
    if (!string.Equals(verifiedVersion, SdkVersion, StringComparison.Ordinal))
    {
        throw new InvalidDataException(
            $"SDK extraction completed, but {installedDotNet} reported {verifiedVersion ?? "no version"}.");
    }

    await Console.Out.WriteLineAsync(
        $"Installed .NET SDK {SdkVersion} at {installDirectory}.")
        .ConfigureAwait(false);
}
finally
{
    File.Delete(temporaryArchive);
}

static string GetScriptDirectory([CallerFilePath] string scriptPath = "") =>
    Path.GetDirectoryName(scriptPath)
    ?? throw new InvalidOperationException("The script directory could not be determined.");

static async Task<string?> ReadSdkVersionAsync(string dotnetPath)
{
    ProcessStartInfo startInfo = new(dotnetPath, "--version")
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true
    };
    using Process process = Process.Start(startInfo)
        ?? throw new InvalidOperationException($"Unable to start {dotnetPath}.");
    string output = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
    string error = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
    await process.WaitForExitAsync().ConfigureAwait(false);
    return process.ExitCode == 0 ? output.Trim() : throw new InvalidOperationException(error.Trim());
}

static string GetHostRuntimeIdentifier()
{
    string architecture = RuntimeInformation.OSArchitecture switch
    {
        Architecture.X64 => "x64",
        Architecture.Arm64 => "arm64",
        Architecture.X86 => "x86",
        _ => throw new PlatformNotSupportedException(
            $"Unsupported architecture: {RuntimeInformation.OSArchitecture}.")
    };

    if (OperatingSystem.IsWindows())
    {
        return $"win-{architecture}";
    }

    if (OperatingSystem.IsMacOS())
    {
        return $"osx-{architecture}";
    }

    if (OperatingSystem.IsLinux())
    {
        string platform = File.Exists("/etc/alpine-release") ? "linux-musl" : "linux";
        return $"{platform}-{architecture}";
    }

    throw new PlatformNotSupportedException(RuntimeInformation.OSDescription);
}

static (Uri Url, string Hash, string Extension) FindSdkArchive(
    JsonElement root,
    string runtimeIdentifier)
{
    foreach (JsonElement release in root.GetProperty("releases").EnumerateArray())
    {
        if (!release.TryGetProperty("sdks", out JsonElement sdks))
        {
            continue;
        }

        foreach (JsonElement sdk in sdks.EnumerateArray())
        {
            if (!string.Equals(sdk.GetProperty("version").GetString(), SdkVersion, StringComparison.Ordinal))
            {
                continue;
            }

            foreach (JsonElement file in sdk.GetProperty("files").EnumerateArray())
            {
                if (!string.Equals(file.GetProperty("rid").GetString(), runtimeIdentifier, StringComparison.Ordinal))
                {
                    continue;
                }

                string name = file.GetProperty("name").GetString()
                    ?? throw new InvalidDataException("SDK metadata omitted the archive name.");
                string extension = name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? ".zip" : ".tar.gz";
                string url = file.GetProperty("url").GetString()
                    ?? throw new InvalidDataException("SDK metadata omitted the archive URL.");
                string hash = file.GetProperty("hash").GetString()
                    ?? throw new InvalidDataException("SDK metadata omitted the archive hash.");
                return (new Uri(url), hash, extension);
            }
        }
    }

    throw new InvalidOperationException(
        $"The official .NET release metadata does not contain SDK {SdkVersion} for {runtimeIdentifier}.");
}
