#!/usr/bin/env -S dotnet --
#:property TargetFramework=net10.0
#:property LangVersion=14.0
#:property Nullable=enable
#:property TreatWarningsAsErrors=true
#:property RootNamespace=Csls
#:package System.CommandLine
#:include Support/VsCodeTestInstallation.cs

using Csls.Support;
using System.CommandLine;
using System.Formats.Tar;
using System.Text.Json;

var root = new Option<DirectoryInfo>("--root")
{
    Description = "Repository directory containing the provisioned assets or downloaded archives.",
    DefaultValueFactory = _ => new DirectoryInfo(Directory.GetCurrentDirectory()),
    Recursive = true
};
var profile = new Option<string>("--profile")
{
    Description = "Editor test profile to extract.",
    Required = true
};
profile.AcceptOnlyFromAmong("desktop", "remote");
var command = new RootCommand("Packs and extracts VS Code test assets by consumer.") { root };
var pack = new Command("pack", "Pack common assets, desktop oracles, and the remote server separately.");
pack.SetAction(result =>
{
    string repositoryRoot = result.GetRequiredValue(root).FullName;
    string[] groups = ["common", "desktop", "remote"];
    foreach (string group in groups)
    {
        Pack(repositoryRoot, group);
    }
});
var extract = new Command("extract", "Extract common assets and the selected editor test profile.") { profile };
extract.SetAction(result =>
{
    string repositoryRoot = result.GetRequiredValue(root).FullName;
    string[] groups = ["common", result.GetRequiredValue(profile)];
    foreach (string archivePath in groups.Select(group => GetArchivePath(repositoryRoot, group)))
    {
        TarFile.ExtractToDirectory(archivePath, repositoryRoot, overwriteFiles: true);
        Console.WriteLine($"Extracted {Path.GetFileName(archivePath)}.");
    }
});
command.Add(pack);
command.Add(extract);
return command.Parse(args).Invoke();

static string GetArchivePath(string repositoryRoot, string group) =>
    Path.Join(repositoryRoot, "artifacts", $"vscode-test-assets-{group}.tar");

static void Pack(string repositoryRoot, string group)
{
    string[] paths = group switch
    {
        "common" =>
        [
            "artifacts/tools/vscode/stable/executable.path",
            Path.GetRelativePath(repositoryRoot, GetEditorRoot(repositoryRoot)),
            "artifacts/tools/vscode-dotnet-runtime/current",
            "tests/vscode/node_modules",
            "tests/vscode/dist",
            "editors/vscode/node_modules",
            "editors/vscode/dist"
        ],
        "desktop" => ["artifacts/tools/vscode-csharp/current", "artifacts/tools/vscode-csdevkit/current"],
        "remote" => [Path.GetRelativePath(repositoryRoot, GetServerRoot(repositoryRoot))],
        _ => throw new ArgumentOutOfRangeException(nameof(group), group, "Unknown asset group.")
    };
    string? missingPath = paths.FirstOrDefault(path =>
        !Directory.Exists(Path.Join(repositoryRoot, path)) && !File.Exists(Path.Join(repositoryRoot, path)));
    if (missingPath is not null)
    {
        throw new FileNotFoundException($"Required VS Code test assets are missing: {missingPath}.");
    }

    string archivePath = GetArchivePath(repositoryRoot, group);
    using (FileStream stream = File.Create(archivePath))
    using (var writer = new TarWriter(stream))
    {
        foreach (string relativePath in paths)
        {
            var pending = new Stack<string>();
            pending.Push(Path.Join(repositoryRoot, relativePath));
            while (pending.TryPop(out string? path))
            {
                string entryName = Path.GetRelativePath(repositoryRoot, path)
                    .Replace(Path.DirectorySeparatorChar, '/');
                writer.WriteEntry(path, entryName);
                FileAttributes attributes = File.GetAttributes(path);
                if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) ==
                    FileAttributes.Directory)
                {
                    foreach (string child in Directory.EnumerateFileSystemEntries(path))
                    {
                        pending.Push(child);
                    }
                }
            }
        }
    }

    Console.WriteLine($"Packed {Path.GetFileName(archivePath)}: {new FileInfo(archivePath).Length} bytes.");
}

static string GetEditorRoot(string repositoryRoot)
{
    string cache = Path.Join(repositoryRoot, "artifacts", "tools", "vscode", "stable");
    string executable = VsCodeTestInstallation.Resolve(cache);
    string directory = Path.GetRelativePath(cache, executable).Split(Path.DirectorySeparatorChar)[0];
    return Path.Join(cache, directory);
}

static string GetServerRoot(string repositoryRoot)
{
    string revision = ReadRevision(Path.Join(GetEditorRoot(repositoryRoot), "resources", "app", "product.json"));
    string server = Path.Join(repositoryRoot, "artifacts", "tools", "vscode-server", revision, "linux-x64");
    if (!File.Exists(Path.Join(server, "node")) || !File.Exists(Path.Join(server, "out", "server-main.js")) ||
        ReadRevision(Path.Join(server, "product.json")) != revision)
    {
        throw new InvalidDataException("Provision the VS Code server matching the selected desktop editor before packing assets.");
    }

    return server;
}

static string ReadRevision(string productPath)
{
    using FileStream stream = File.OpenRead(productPath);
    using var document = JsonDocument.Parse(stream);
    string? revision = document.RootElement.GetProperty("commit").GetString();
    if (revision is not { Length: 40 } || !revision.All(char.IsAsciiHexDigit))
    {
        throw new InvalidDataException("The VS Code product metadata has an invalid commit identifier.");
    }

    return revision;
}
