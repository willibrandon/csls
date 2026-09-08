#!/usr/bin/env -S dotnet --
#:property TargetFramework=net10.0
#:property LangVersion=14.0
#:property Nullable=enable
#:property TreatWarningsAsErrors=true
#:property RootNamespace=Csls
#:package System.CommandLine

using System.CommandLine;
using System.Formats.Tar;

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
    string[] directories = group switch
    {
        "common" =>
        [
            "artifacts/tools/vscode",
            "artifacts/tools/vscode-dotnet-runtime",
            "tests/vscode/node_modules",
            "tests/vscode/dist",
            "editors/vscode/node_modules",
            "editors/vscode/dist"
        ],
        "desktop" => ["artifacts/tools/vscode-csharp", "artifacts/tools/vscode-csdevkit"],
        "remote" => ["artifacts/tools/vscode-server"],
        _ => throw new ArgumentOutOfRangeException(nameof(group), group, "Unknown asset group.")
    };
    string? missingDirectory = directories.FirstOrDefault(directory =>
        !Directory.Exists(Path.Join(repositoryRoot, directory)));
    if (missingDirectory is not null)
    {
        throw new DirectoryNotFoundException($"Required VS Code test assets are missing: {missingDirectory}.");
    }

    string archivePath = GetArchivePath(repositoryRoot, group);
    using (FileStream stream = File.Create(archivePath))
    using (var writer = new TarWriter(stream))
    {
        foreach (string directory in directories)
        {
            var pending = new Stack<string>();
            pending.Push(Path.Join(repositoryRoot, directory));
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
