using System.Diagnostics;
using System.Text.Json;
using System.Xml.Linq;

namespace Csls.Debugger.Tests;

/// <summary>
/// Owns the real symbol-bearing programs shared by the debugger protocol tests.
/// </summary>
internal sealed class DebuggerSymbolFixtures : IAsyncDisposable
{
    private readonly string _fixtureDirectory;

    private DebuggerSymbolFixtures(
        string fixtureDirectory,
        string sourcePath,
        byte[] source)
    {
        _fixtureDirectory = fixtureDirectory;
        SourcePath = sourcePath;
        ValidSourceLinkProgramPath = GetProgramPath(fixtureDirectory, "SourceLinkValid");
        ImplicitSourceLinkProgramPath = GetProgramPath(fixtureDirectory, "SourceLinkImplicit");
        MismatchedSourceLinkProgramPath = GetProgramPath(
            fixtureDirectory,
            "SourceLinkMismatched");
        WindowsPdbProgramPath = OperatingSystem.IsWindows()
            ? GetProgramPath(fixtureDirectory, "WindowsPdbFixture")
            : null;
        ValidSourceLinkServer = new SourceLinkTestServer(source);
        ImplicitSourceLinkServer = new SourceLinkTestServer(source);
        MismatchedSourceLinkServer = new SourceLinkTestServer([.. source, (byte)' ']);
    }

    /// <summary>
    /// Gets the original source path recorded by the Windows PDB fixture.
    /// </summary>
    internal string SourcePath { get; }

    /// <summary>
    /// Gets the program whose Source Link endpoint serves checksum-valid content.
    /// </summary>
    internal string ValidSourceLinkProgramPath { get; }

    /// <summary>
    /// Gets the program whose Source Link endpoint must not be accessed implicitly.
    /// </summary>
    internal string ImplicitSourceLinkProgramPath { get; }

    /// <summary>
    /// Gets the program whose Source Link endpoint serves checksum-invalid content.
    /// </summary>
    internal string MismatchedSourceLinkProgramPath { get; }

    /// <summary>
    /// Gets the Windows-PDB program path when running on Windows.
    /// </summary>
    internal string? WindowsPdbProgramPath { get; }

    /// <summary>
    /// Gets the server that provides checksum-valid source content.
    /// </summary>
    internal SourceLinkTestServer ValidSourceLinkServer { get; }

    /// <summary>
    /// Gets the server used to prove that loopback Source Link access requires consent.
    /// </summary>
    internal SourceLinkTestServer ImplicitSourceLinkServer { get; }

    /// <summary>
    /// Gets the server that provides checksum-invalid source content.
    /// </summary>
    internal SourceLinkTestServer MismatchedSourceLinkServer { get; }

    /// <summary>
    /// Builds all platform-appropriate symbol fixtures through one isolated SDK invocation.
    /// </summary>
    /// <param name="cancellationToken">The fixture-build cancellation token.</param>
    /// <returns>The initialized fixture owner.</returns>
    internal static async Task<DebuggerSymbolFixtures> CreateAsync(
        CancellationToken cancellationToken)
    {
        string repositoryRoot = DebuggerTestEnvironment.FindRepositoryRoot();
        string sourceDirectory = Path.Join(
            repositoryRoot,
            "test-assets",
            "Csls.Debugger.Fixtures.CSharp");
        string sourcePath = Path.Join(sourceDirectory, "Program.cs");
        byte[] source = await File.ReadAllBytesAsync(sourcePath, cancellationToken)
            .ConfigureAwait(false);
        string fixtureDirectory = DebuggerTestPath.Canonicalize(Path.Join(
            Path.GetTempPath(),
            $"csls-debugger-symbols-{Guid.NewGuid():N}"));
        Directory.CreateDirectory(fixtureDirectory);
        var fixtures = new DebuggerSymbolFixtures(fixtureDirectory, sourcePath, source);
        try
        {
            fixtures.ValidSourceLinkServer.Start();
            fixtures.ImplicitSourceLinkServer.Start();
            fixtures.MismatchedSourceLinkServer.Start();
            _ = await WriteSourceLinkProjectAsync(
                sourceDirectory,
                fixtureDirectory,
                "SourceLinkValid",
                fixtures.ValidSourceLinkServer.SourceLinkPattern,
                cancellationToken).ConfigureAwait(false);
            _ = await WriteSourceLinkProjectAsync(
                sourceDirectory,
                fixtureDirectory,
                "SourceLinkImplicit",
                fixtures.ImplicitSourceLinkServer.SourceLinkPattern,
                cancellationToken).ConfigureAwait(false);
            _ = await WriteSourceLinkProjectAsync(
                sourceDirectory,
                fixtureDirectory,
                "SourceLinkMismatched",
                fixtures.MismatchedSourceLinkServer.SourceLinkPattern,
                cancellationToken).ConfigureAwait(false);
            bool includeWindowsPdb = fixtures.WindowsPdbProgramPath is not null;
            _ = includeWindowsPdb
                ? await WriteWindowsPdbProjectAsync(
                    sourceDirectory,
                    fixtureDirectory,
                    cancellationToken).ConfigureAwait(false)
                : null;
            string solutionPath = await WriteSolutionAsync(
                fixtureDirectory,
                includeWindowsPdb,
                cancellationToken).ConfigureAwait(false);
            await BuildAsync(solutionPath, fixtureDirectory, cancellationToken)
                .ConfigureAwait(false);
            return fixtures;
        }
        catch
        {
            await fixtures.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await DisposeServersAsync(
            ValidSourceLinkServer,
            ImplicitSourceLinkServer,
            MismatchedSourceLinkServer).ConfigureAwait(false);
        await DebuggerTestDirectoryReleaseWaiter.DeleteAsync(
            _fixtureDirectory,
            TimeSpan.FromSeconds(10)).ConfigureAwait(false);
    }

    private static async Task<string> WriteSourceLinkProjectAsync(
        string sourceDirectory,
        string fixtureDirectory,
        string projectName,
        string sourceLinkPattern,
        CancellationToken cancellationToken)
    {
        string projectDirectory = Path.Join(fixtureDirectory, projectName);
        Directory.CreateDirectory(projectDirectory);
        File.Copy(Path.Join(sourceDirectory, "Program.cs"), Path.Join(projectDirectory, "Program.cs"));
        File.Copy(
            Path.Join(sourceDirectory, "DebuggerFixtureValue.cs"),
            Path.Join(projectDirectory, "DebuggerFixtureValue.cs"));
        await File.WriteAllTextAsync(
            Path.Join(projectDirectory, "sourcelink.json"),
            JsonSerializer.Serialize(new
            {
                documents = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["/_/SourceLink/*"] = sourceLinkPattern
                }
            }),
            cancellationToken).ConfigureAwait(false);
        string debugType = OperatingSystem.IsWindows() ? "full" : "portable";
        await File.WriteAllTextAsync(
            Path.Join(projectDirectory, $"{projectName}.csproj"),
            $$"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <AssemblyName>{{projectName}}</AssemblyName>
                <DebugSymbols>true</DebugSymbols>
                <DebugType>{{debugType}}</DebugType>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
                <OutputType>Exe</OutputType>
                <PathMap>$(MSBuildProjectDirectory)=/_/SourceLink</PathMap>
                <SourceLink>$(MSBuildProjectDirectory)/sourcelink.json</SourceLink>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """,
            cancellationToken).ConfigureAwait(false);
        return Path.Join(
            projectDirectory,
            "bin",
            "Debug",
            "net10.0",
            $"{projectName}.dll");
    }

    private static async Task<string> WriteWindowsPdbProjectAsync(
        string sourceDirectory,
        string fixtureDirectory,
        CancellationToken cancellationToken)
    {
        const string projectName = "WindowsPdbFixture";
        string projectDirectory = Path.Join(fixtureDirectory, projectName);
        Directory.CreateDirectory(projectDirectory);
        var project = new XDocument(
            new XElement(
                "Project",
                new XAttribute("Sdk", "Microsoft.NET.Sdk"),
                new XElement(
                    "PropertyGroup",
                    new XElement("AssemblyName", projectName),
                    new XElement("DebugSymbols", "true"),
                    new XElement("DebugType", "full"),
                    new XElement("Deterministic", "false"),
                    new XElement("EmbedAllSources", "true"),
                    new XElement("EnableDefaultCompileItems", "false"),
                    new XElement("ImplicitUsings", "enable"),
                    new XElement("Nullable", "enable"),
                    new XElement("OutputType", "Exe"),
                    new XElement("TargetFramework", "net10.0")),
                new XElement(
                    "ItemGroup",
                    new XElement(
                        "Compile",
                        new XAttribute("Include", Path.Join(sourceDirectory, "Program.cs"))),
                    new XElement(
                        "Compile",
                        new XAttribute(
                            "Include",
                            Path.Join(sourceDirectory, "DebuggerFixtureValue.cs"))))));
        await File.WriteAllTextAsync(
            Path.Join(projectDirectory, $"{projectName}.csproj"),
            project.ToString(),
            cancellationToken).ConfigureAwait(false);
        return Path.Join(
            projectDirectory,
            "bin",
            "Debug",
            "net10.0",
            $"{projectName}.dll");
    }

    private static async Task<string> WriteSolutionAsync(
        string fixtureDirectory,
        bool includeWindowsPdb,
        CancellationToken cancellationToken)
    {
        string[] projectNames = includeWindowsPdb
            ? ["SourceLinkValid", "SourceLinkImplicit", "SourceLinkMismatched", "WindowsPdbFixture"]
            : ["SourceLinkValid", "SourceLinkImplicit", "SourceLinkMismatched"];
        var solution = new XDocument(
            new XElement(
                "Solution",
                projectNames.Select(projectName => new XElement(
                    "Project",
                    new XAttribute(
                        "Path",
                        Path.Join(projectName, $"{projectName}.csproj"))))));
        string solutionPath = Path.Join(fixtureDirectory, "DebuggerSymbolFixtures.slnx");
        await File.WriteAllTextAsync(
            solutionPath,
            solution.ToString(),
            cancellationToken).ConfigureAwait(false);
        return solutionPath;
    }

    private static async Task BuildAsync(
        string solutionPath,
        string fixtureDirectory,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(
            Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            WorkingDirectory = fixtureDirectory
        };
        startInfo.ArgumentList.Add("build");
        startInfo.ArgumentList.Add(solutionPath);
        startInfo.ArgumentList.Add("--nologo");
        startInfo.ArgumentList.Add("--disable-build-servers");
        startInfo.ArgumentList.Add("--maxcpucount:1");
        startInfo.ArgumentList.Add("--property:UseSharedCompilation=false");
        (int exitCode, string output, string error) = await DebuggerTestProcess.RunAsync(
            startInfo,
            cancellationToken).ConfigureAwait(false);
        if (exitCode != 0)
        {
            throw new InvalidOperationException(
                $"Debugger symbol fixture build failed with exit code {exitCode}:" +
                $"{Environment.NewLine}{output}{Environment.NewLine}{error}");
        }
    }

    private static async Task DisposeServersAsync(params SourceLinkTestServer[] servers)
    {
        foreach (SourceLinkTestServer server in servers)
        {
            await server.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static string GetProgramPath(string fixtureDirectory, string projectName) =>
        Path.Join(
            fixtureDirectory,
            projectName,
            "bin",
            "Debug",
            "net10.0",
            $"{projectName}.dll");
}
