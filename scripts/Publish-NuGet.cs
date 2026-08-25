#!/usr/bin/env -S dotnet --
#:property TargetFramework=net10.0
#:property LangVersion=14.0
#:property Nullable=enable
#:property TreatWarningsAsErrors=true

using System.Diagnostics;
using System.IO.Compression;
using System.Xml.Linq;

if (args.Length == 1 && args[0] is "--help" or "-h" or "-?")
{
    await Console.Out.WriteLineAsync(
        "Publishes a verified csls NuGet package set with the ephemeral login credential.")
        .ConfigureAwait(false);
    await Console.Out.WriteLineAsync(
        "Usage: dotnet run --file scripts/Publish-NuGet.cs -- --packages <path>")
        .ConfigureAwait(false);
    return 0;
}

if (args.Length != 2 || !string.Equals(args[0], "--packages", StringComparison.Ordinal))
{
    await Console.Error.WriteLineAsync(
        "Usage: dotnet run --file scripts/Publish-NuGet.cs -- --packages <path>")
        .ConfigureAwait(false);
    return 2;
}

try
{
    string packagesPath = Path.GetFullPath(args[1]);
    string apiKey = Environment.GetEnvironmentVariable("NUGET_API_KEY")
        ?? throw new InvalidOperationException(
            "NUGET_API_KEY was not provided by the NuGet trusted-publishing login.");
    string[] packages =
    [
        .. Directory.EnumerateFiles(packagesPath, "*.nupkg", SearchOption.TopDirectoryOnly)
            .Where(static path => !path.EndsWith(".symbols.nupkg", StringComparison.OrdinalIgnoreCase))
            .OrderBy(GetPublicationOrder)
            .ThenBy(static path => path, StringComparer.Ordinal)
    ];
    int implementationCount = packages.Count(static path => GetPublicationOrder(path) == 0);
    int manifestCount = packages.Length - implementationCount;
    if (packages.Length != 22 || implementationCount != 20 || manifestCount != 2)
    {
        throw new InvalidDataException(
            "Expected 20 implementation packages and two manifest packages before publication.");
    }

    foreach (string package in packages)
    {
        await PushAsync(package, apiKey).ConfigureAwait(false);
    }

    await Console.Out.WriteLineAsync($"Published {packages.Length} NuGet packages.")
        .ConfigureAwait(false);
    return 0;
}
catch (Exception exception) when (exception is
    IOException or
    InvalidDataException or
    InvalidOperationException or
    UnauthorizedAccessException)
{
    await Console.Error.WriteLineAsync(exception.Message).ConfigureAwait(false);
    return 1;
}

static int GetPublicationOrder(string packagePath)
{
    using ZipArchive archive = ZipFile.OpenRead(packagePath);
    ZipArchiveEntry nuspec = archive.Entries.Single(static entry =>
        entry.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase));
    using Stream stream = nuspec.Open();
    var document = XDocument.Load(stream, LoadOptions.None);
    bool implementation = document
        .Descendants()
        .Where(static element => element.Name.LocalName == "packageType")
        .Any(static element => string.Equals(
            (string?)element.Attribute("name"),
            "DotnetToolRidPackage",
            StringComparison.Ordinal));
    return implementation ? 0 : 1;
}

static async Task PushAsync(string packagePath, string apiKey)
{
    var startInfo = new ProcessStartInfo
    {
        FileName = "dotnet",
        RedirectStandardError = true,
        RedirectStandardOutput = true,
        UseShellExecute = false
    };
    foreach (string argument in new[]
    {
        "nuget",
        "push",
        packagePath,
        "--api-key",
        apiKey,
        "--source",
        "https://api.nuget.org/v3/index.json",
        "--skip-duplicate"
    })
    {
        startInfo.ArgumentList.Add(argument);
    }

    using Process process = Process.Start(startInfo)
        ?? throw new InvalidOperationException("dotnet nuget push did not start.");
    Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
    Task<string> errorTask = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync().ConfigureAwait(false);
    string output = await outputTask.ConfigureAwait(false);
    string error = await errorTask.ConfigureAwait(false);
    await Console.Out.WriteAsync(output).ConfigureAwait(false);
    await Console.Error.WriteAsync(error).ConfigureAwait(false);
    if (process.ExitCode != 0)
    {
        throw new InvalidOperationException(
            $"NuGet publication failed for {Path.GetFileName(packagePath)} with " +
            $"exit code {process.ExitCode}.");
    }
}
