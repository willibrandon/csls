using System.Diagnostics;
using System.Reflection.PortableExecutable;
using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Builds and serializes the real Portable PDB symbol-server DAP fixture.
/// </summary>
public sealed partial class DapSessionTests
{
    private async Task<(string ProgramPath, string SourcePath, string PdbPath)>
        BuildSymbolServerFixtureAsync(string testDirectory)
    {
        string sourcePath = Path.Join(testDirectory, "Program.cs");
        await File.WriteAllTextAsync(
            sourcePath,
            """
            using System;
            using System.Threading;

            internal static class Program
            {
                private static void Main()
                {
                    int answer = 41;
                    answer++;
                    Console.WriteLine(answer);
                    Thread.Sleep(Timeout.Infinite);
                }
            }
            """,
            TestContext.CancellationToken).ConfigureAwait(false);
        string projectPath = Path.Join(testDirectory, "SymbolServerFixture.csproj");
        await File.WriteAllTextAsync(
            projectPath,
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>net10.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
                <DebugType>portable</DebugType>
                <DebugSymbols>true</DebugSymbols>
              </PropertyGroup>
            </Project>
            """,
            TestContext.CancellationToken).ConfigureAwait(false);
        string dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
        var startInfo = new ProcessStartInfo(dotnet)
        {
            WorkingDirectory = testDirectory,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        startInfo.ArgumentList.Add("build");
        startInfo.ArgumentList.Add(projectPath);
        startInfo.ArgumentList.Add("--nologo");
        using Process process = Process.Start(startInfo)!;
        string output = await process.StandardOutput.ReadToEndAsync(
            TestContext.CancellationToken).ConfigureAwait(false);
        string error = await process.StandardError.ReadToEndAsync(
            TestContext.CancellationToken).ConfigureAwait(false);
        await process.WaitForExitAsync(TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(0, process.ExitCode, $"{output}{Environment.NewLine}{error}");
        string outputDirectory = Path.Join(testDirectory, "bin", "Debug", "net10.0");
        return (
            Path.Join(outputDirectory, "SymbolServerFixture.dll"),
            sourcePath,
            Path.Join(outputDirectory, "SymbolServerFixture.pdb"));
    }

    private static string ReadPortablePdbStoreIndex(string programPath)
    {
        using FileStream stream = File.OpenRead(programPath);
        using var peReader = new PEReader(stream);
        CodeViewDebugDirectoryData codeView = peReader.ReadDebugDirectory()
            .Where(static entry => entry.Type == DebugDirectoryEntryType.CodeView)
            .Select(peReader.ReadCodeViewDebugDirectoryData)
            .Single();
        string fileName = Path.GetFileName(codeView.Path.Replace('\\', '/'));
        return $"{fileName}/{codeView.Guid:N}FFFFFFFF/{fileName}".ToUpperInvariant();
    }

    private static void WriteSymbolServerLaunchArguments(
        Utf8JsonWriter writer,
        string programPath,
        string cachePath,
        string serverUrl)
    {
        writer.WriteStartObject();
        writer.WriteBoolean("noDebug", false);
        writer.WriteString("program", programPath);
        writer.WriteStartObject("symbolOptions");
        writer.WriteStartArray("searchPaths");
        writer.WriteStringValue(serverUrl);
        writer.WriteEndArray();
        writer.WriteString("cachePath", cachePath);
        writer.WriteStartObject("moduleFilter");
        writer.WriteString("mode", "loadOnlyIncluded");
        writer.WriteStartArray("includedModules");
        writer.WriteStringValue("SymbolServerFixture.dll");
        writer.WriteEndArray();
        writer.WriteBoolean("includeSymbolsNextToModules", true);
        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    private static void WriteThreadArguments(Utf8JsonWriter writer, int threadId)
    {
        writer.WriteStartObject();
        writer.WriteNumber("threadId", threadId);
        writer.WriteEndObject();
    }
}
