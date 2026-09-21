#!/usr/bin/env -S dotnet --
#:property TargetFramework=net10.0
#:property LangVersion=14.0
#:property Nullable=enable
#:property TreatWarningsAsErrors=true
#:property UseAppHost=false
#:property RootNamespace=Csls

using System.Text;

string environmentFile = Environment.GetEnvironmentVariable("GITHUB_ENV")
    ?? throw new InvalidOperationException("GITHUB_ENV must identify the job environment file.");
if (!Path.IsPathFullyQualified(environmentFile))
{
    throw new InvalidOperationException("GITHUB_ENV must be an absolute path.");
}

string hostPath = Environment.ProcessPath
    ?? throw new InvalidOperationException("The active .NET host path is unavailable.");
var host = new FileInfo(hostPath);
FileInfo resolvedHost = host.ResolveLinkTarget(returnFinalTarget: true) as FileInfo ?? host;
DirectoryInfo root = resolvedHost.Directory
    ?? throw new InvalidOperationException("The active .NET host has no installation directory.");
if (!string.Equals(resolvedHost.Name, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet",
        StringComparison.OrdinalIgnoreCase) ||
    !Directory.Exists(Path.Join(root.FullName, "shared", "Microsoft.NETCore.App")))
{
    throw new InvalidOperationException($"The active host is not inside a .NET installation: {hostPath}");
}

await File.AppendAllTextAsync(environmentFile, $"DOTNET_ROOT={root.FullName}{Environment.NewLine}",
    new UTF8Encoding(false)).ConfigureAwait(false);
await Console.Error.WriteLineAsync($"Exported DOTNET_ROOT for the active runtime: {root.FullName}")
    .ConfigureAwait(false);
