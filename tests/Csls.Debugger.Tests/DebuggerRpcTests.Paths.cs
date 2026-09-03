using System.Runtime.CompilerServices;

namespace Csls.Debugger.Tests;

/// <summary>
/// Resolves repository paths used by private debugger RPC tests.
/// </summary>
public sealed partial class DebuggerRpcTests
{
    private static string ResolveTestProcessHost(string repositoryRoot) => Path.Join(
        repositoryRoot,
        "artifacts",
        "bin",
        "Csls.TestProcessHost",
        "debug",
        "csls-test-process-host.dll");

    private static string FindRepositoryRoot([CallerFilePath] string sourcePath = "")
    {
        DirectoryInfo? directory = new FileInfo(sourcePath).Directory;
        while (directory is not null && !File.Exists(Path.Join(directory.FullName, "Csls.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate the csls repository root.");
    }
}
