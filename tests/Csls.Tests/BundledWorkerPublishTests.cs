using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security;

namespace Csls.Tests;

/// <summary>
/// Exercises bundled Native AOT publishing from an empty build directory.
/// </summary>
[TestClass]
public sealed class BundledWorkerPublishTests
{
    /// <summary>
    /// Gets the framework-managed cancellation token for publishing and execution.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Restores compiler targets before publishing and executes the resulting native worker.
    /// </summary>
    [TestMethod]
    [OSCondition(ConditionMode.Include, OperatingSystems.Linux)]
    [Timeout(90000, CooperativeCancellation = true)]
    public async Task FirstPublishProducesExecutableNativeWorker()
    {
        string repositoryRoot = EditorToolResolver.FindRepositoryRoot();
        string targetsPath = SecurityElement.Escape(Path.Join(repositoryRoot, "build", "Csls.BundledWorkers.targets"));
        string fixturePath = Path.Join(Path.GetTempPath(), $"csls-bundled-publish-{Guid.NewGuid():N}");
        string hostPath = Path.Join(fixturePath, "host");
        string workerPath = Path.Join(fixturePath, "worker");
        string publishPath = Path.Join(fixturePath, "publish");
        Directory.CreateDirectory(hostPath);
        Directory.CreateDirectory(workerPath);
        try
        {
            await File.WriteAllTextAsync(Path.Join(hostPath, "Host.csproj"), $$"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <OutputType>Exe</OutputType>
                  </PropertyGroup>
                  <ItemGroup>
                    <CslsBundledWorker Include="../worker/Worker.csproj">
                      <BundlePath>worker</BundlePath>
                    </CslsBundledWorker>
                  </ItemGroup>
                  <Import Project="{{targetsPath}}" />
                </Project>
                """, TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Join(hostPath, "Program.cs"),
                "System.Console.WriteLine(\"bundle host\");", TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Join(workerPath, "Directory.Build.props"), """
                <Project>
                  <PropertyGroup>
                    <ArtifactsPath>$(CslsBundledWorkerArtifactsPath)</ArtifactsPath>
                    <PublishDir>$(CslsBundledWorkerPublishDir)</PublishDir>
                  </PropertyGroup>
                </Project>
                """, TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Join(workerPath, "Worker.csproj"), $$"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <OutputType>Exe</OutputType>
                    <PublishAot>true</PublishAot>
                  </PropertyGroup>
                  <Import Project="{{targetsPath}}" />
                </Project>
                """, TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Join(workerPath, "Program.cs"),
                "System.Console.WriteLine(\"native bundled worker\");", TestContext.CancellationToken).ConfigureAwait(false);

            await RunAsync(EditorToolResolver.ResolveDotNetHost(),
                [
                    "publish", Path.Join(hostPath, "Host.csproj"),
                    "--configuration", "Release",
                    "--runtime", RuntimeInformation.RuntimeIdentifier,
                    "--artifacts-path", Path.Join(fixturePath, "artifacts"),
                    "--output", publishPath,
                    "/bl:" + Path.Join(EditorToolResolver.ResolveArtifactsRoot(repositoryRoot),
                        "binlogs", "bundled-worker-test-{}.binlog")
                ], fixturePath).ConfigureAwait(false);

            string executablePath = Path.Join(publishPath, "worker", "Worker");
            Assert.IsTrue(File.Exists(executablePath), "The first publish must produce the bundled executable.");
            Assert.IsFalse(File.Exists(executablePath + ".dll"), "The worker must be native on its first publish.");
            Assert.IsFalse(File.Exists(executablePath + ".runtimeconfig.json"));
            string output = await RunAsync(executablePath, [], fixturePath).ConfigureAwait(false);
            Assert.AreEqual("native bundled worker" + Environment.NewLine, output);
        }
        finally
        {
            await DirectoryReleaseWaiter.DeleteAsync(fixturePath, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
    }

    private async Task<string> RunAsync(string executablePath, IReadOnlyList<string> arguments, string workingDirectory)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException("The publish fixture failed to start.");
        Task<string> outputTask = process.StandardOutput.ReadToEndAsync(TestContext.CancellationToken);
        Task<string> errorTask = process.StandardError.ReadToEndAsync(TestContext.CancellationToken);
        try
        {
            await process.WaitForExitAsync(TestContext.CancellationToken).ConfigureAwait(false);
            string output = await outputTask.ConfigureAwait(false);
            string error = await errorTask.ConfigureAwait(false);
            Assert.AreEqual(0, process.ExitCode, output + error);
            return output;
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
    }
}
