using System.Diagnostics;
using System.Security;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies the readonly-field analyzer through a real compiler process.
/// </summary>
[TestClass]
public sealed class CodeQlReadonlyAnalyzerProcessTests
{
    /// <summary>
    /// Gets framework-managed cancellation for the compiler process.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Rejects initialization-only struct fields while preserving native and mutable storage.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task StructReadonlyFindingsAreCaughtBeforeCodeQl()
    {
        string root = DebuggerTestEnvironment.FindRepositoryRoot();
        string analyzer = Path.Join(root, "artifacts", "bin", "Csls.SourceGen", "debug", "Csls.SourceGen.dll");
        Assert.IsTrue(File.Exists(analyzer), $"The repository analyzer was not built: {analyzer}");
        string escapedAnalyzer = SecurityElement.Escape(analyzer)
            ?? throw new InvalidOperationException("The analyzer path cannot be XML escaped.");
        DirectoryInfo directory = Directory.CreateTempSubdirectory("csls-readonly-analyzer-");
        try
        {
            await File.WriteAllTextAsync(Path.Join(directory.FullName, "ReadonlyProbe.csproj"), $$"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <Nullable>enable</Nullable>
                  </PropertyGroup>
                  <ItemGroup>
                    <Analyzer Include="{{escapedAnalyzer}}" />
                  </ItemGroup>
                </Project>
                """, TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Join(directory.FullName, "ReadonlyProbe.cs"), """
                using System.Runtime.InteropServices;

                public struct InitializedFields
                {
                    public int Number;
                    public string? Text;

                    public InitializedFields(int number, string? text)
                    {
                        Number = number;
                        Text = text;
                    }
                }

                public struct MutableField
                {
                    public int Value;
                    public void Set(int value) => Value = value;
                }

                [StructLayout(LayoutKind.Sequential)]
                public struct NativeField
                {
                    public int Value;
                }
                """, TestContext.CancellationToken).ConfigureAwait(false);

            var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
            {
                WorkingDirectory = directory.FullName
            };
            start.ArgumentList.Add("build");
            start.ArgumentList.Add("ReadonlyProbe.csproj");
            start.ArgumentList.Add("--nologo");
            start.ArgumentList.Add("--verbosity");
            start.ArgumentList.Add("quiet");
            (int exitCode, string output, string error) = await DebuggerTestProcess.RunAsync(
                start, TestContext.CancellationToken).ConfigureAwait(false);
            string result = output + error;
            Assert.AreNotEqual(0, exitCode, result);
            Assert.Contains("CSLS0011: Field 'Number'", result);
            Assert.Contains("CSLS0011: Field 'Text'", result);
            Assert.DoesNotContain("CSLS0011: Field 'Value'", result);
        }
        finally
        {
            await DebuggerTestDirectoryReleaseWaiter.DeleteAsync(directory.FullName, TimeSpan.FromSeconds(10))
                .ConfigureAwait(false);
        }
    }
}
