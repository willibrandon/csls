using System.Runtime.CompilerServices;

namespace Csls.Debugger.Tests;

/// <summary>
/// Resolves repository paths used by private debugger RPC tests.
/// </summary>
public sealed partial class DebuggerRpcTests
{
    private static Dictionary<string, string> CreateDefaultSourceFileMap() =>
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["/_/"] = FindRepositoryRoot()
        };

    private static string ResolveTestProcessHost(string repositoryRoot) => Path.Join(
        repositoryRoot,
        "artifacts",
        "bin",
        "Csls.TestProcessHost",
        "debug",
        "csls-test-process-host.dll");

    private static string FindRepositoryRoot([CallerFilePath] string sourcePath = "")
        => DebuggerTestEnvironment.FindRepositoryRoot(sourcePath);
}
