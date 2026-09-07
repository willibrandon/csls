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
        EntryAppHostPath = Path.ChangeExtension(
            GetProgramPath(fixtureDirectory, "EntryAppHost"),
            OperatingSystem.IsWindows() ? ".exe" : null);
        SymbolFreeProgramPath = GetProgramPath(fixtureDirectory, "SymbolFreeFixture");
        ValidSourceLinkProgramPath = GetProgramPath(fixtureDirectory, "SourceLinkValid");
        CancellationSourceLinkProgramPath = GetProgramPath(fixtureDirectory, "SourceLinkCancellation");
        QueuedSourceLinkProgramPath = GetProgramPath(fixtureDirectory, "SourceLinkQueued");
        ImplicitSourceLinkProgramPath = GetProgramPath(fixtureDirectory, "SourceLinkImplicit");
        MismatchedSourceLinkProgramPath = GetProgramPath(
            fixtureDirectory,
            "SourceLinkMismatched");
        WindowsPdbProgramPath = OperatingSystem.IsWindows()
            ? GetProgramPath(fixtureDirectory, "WindowsPdbFixture")
            : null;
        ValidSourceLinkServer = new SourceLinkTestServer(source);
        CancellationSourceLinkServer = new SourceLinkTestServer(source, holdFirstResponse: true);
        QueuedSourceLinkServer = new SourceLinkTestServer(source, holdFirstResponse: true);
        ImplicitSourceLinkServer = new SourceLinkTestServer(source);
        MismatchedSourceLinkServer = new SourceLinkTestServer([.. source, (byte)' ']);
    }

    /// <summary>
    /// Gets the original source path recorded by the Windows PDB fixture.
    /// </summary>
    internal string SourcePath { get; }

    /// <summary>
    /// Gets the async entry fixture's executable built for the current operating system and architecture.
    /// </summary>
    internal string EntryAppHostPath { get; }

    /// <summary>
    /// Gets an apphost prepared by the build process with one required companion file omitted.
    /// </summary>
    /// <param name="missingAssembly">Whether the managed assembly rather than runtime configuration is omitted.</param>
    /// <returns>The completed, read-only executable fixture path.</returns>
    internal string GetIncompleteAppHostPath(bool missingAssembly) => Path.Join(
        Path.GetDirectoryName(EntryAppHostPath),
        missingAssembly ? "MissingAssembly" : "MissingRuntimeConfig",
        Path.GetFileName(EntryAppHostPath));

    /// <summary>
    /// Gets the executable compiled with symbol generation disabled.
    /// </summary>
    internal string SymbolFreeProgramPath { get; }

    /// <summary>
    /// Gets the program whose Source Link endpoint serves checksum-valid content.
    /// </summary>
    internal string ValidSourceLinkProgramPath { get; }

    /// <summary>
    /// Gets the program whose first source download remains open until cancellation.
    /// </summary>
    internal string CancellationSourceLinkProgramPath { get; }

    /// <summary>
    /// Gets the program whose held source download occupies the request queue during payload-budget checks.
    /// </summary>
    internal string QueuedSourceLinkProgramPath { get; }

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
    /// Gets the server that observes cancellation of a held source response.
    /// </summary>
    internal SourceLinkTestServer CancellationSourceLinkServer { get; }

    /// <summary>
    /// Gets the isolated source endpoint used by the queued-payload cancellation test.
    /// </summary>
    internal SourceLinkTestServer QueuedSourceLinkServer { get; }

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
            fixtures.CancellationSourceLinkServer.Start();
            fixtures.QueuedSourceLinkServer.Start();
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
                "SourceLinkCancellation",
                fixtures.CancellationSourceLinkServer.SourceLinkPattern,
                cancellationToken).ConfigureAwait(false);
            _ = await WriteSourceLinkProjectAsync(
                sourceDirectory,
                fixtureDirectory,
                "SourceLinkQueued",
                fixtures.QueuedSourceLinkServer.SourceLinkPattern,
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
            await WriteEntryAppHostProjectAsync(repositoryRoot, fixtureDirectory, cancellationToken)
                .ConfigureAwait(false);
            _ = await WriteEntrySymbolProjectAsync(
                sourceDirectory, fixtureDirectory, windowsPdb: false, cancellationToken).ConfigureAwait(false);
            _ = includeWindowsPdb
                ? await WriteEntrySymbolProjectAsync(
                    sourceDirectory,
                    fixtureDirectory,
                    windowsPdb: true,
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
            CancellationSourceLinkServer,
            QueuedSourceLinkServer,
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
        File.Copy(
            Path.Join(sourceDirectory, "DebuggerGenericFixture.cs"),
            Path.Join(projectDirectory, "DebuggerGenericFixture.cs"));
        File.Copy(
            Path.Join(sourceDirectory, "UnavailableLocalsFixture.cs"),
            Path.Join(projectDirectory, "UnavailableLocalsFixture.cs"));
        File.Copy(
            Path.Join(sourceDirectory, "ManagedBreakFixture.cs"),
            Path.Join(projectDirectory, "ManagedBreakFixture.cs"));
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

    private static async Task<string> WriteEntrySymbolProjectAsync(
        string sourceDirectory,
        string fixtureDirectory,
        bool windowsPdb,
        CancellationToken cancellationToken)
    {
        string projectName = windowsPdb ? "WindowsPdbFixture" : "SymbolFreeFixture";
        string projectDirectory = Path.Join(fixtureDirectory, projectName);
        Directory.CreateDirectory(projectDirectory);
        var project = new XDocument(
            new XElement(
                "Project",
                new XAttribute("Sdk", "Microsoft.NET.Sdk"),
                new XElement(
                    "PropertyGroup",
                    new XElement("AssemblyName", projectName),
                    new XElement("DebugSymbols", windowsPdb),
                    new XElement("DebugType", windowsPdb ? "full" : "none"),
                    new XElement("Deterministic", !windowsPdb),
                    new XElement("EmbedAllSources", windowsPdb),
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
                            Path.Join(sourceDirectory, "DebuggerFixtureValue.cs"))),
                    new XElement(
                        "Compile",
                        new XAttribute(
                            "Include",
                            Path.Join(sourceDirectory, "DebuggerGenericFixture.cs"))),
                    new XElement(
                        "Compile",
                        new XAttribute(
                            "Include",
                            Path.Join(sourceDirectory, "UnavailableLocalsFixture.cs"))),
                    new XElement(
                        "Compile",
                        new XAttribute(
                            "Include",
                            Path.Join(sourceDirectory, "ManagedBreakFixture.cs"))))));
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

    private static Task WriteEntryAppHostProjectAsync(
        string repositoryRoot,
        string fixtureDirectory,
        CancellationToken cancellationToken)
    {
        string projectDirectory = Path.Join(fixtureDirectory, "EntryAppHost");
        Directory.CreateDirectory(projectDirectory);
        var project = new XDocument(
            new XElement(
                "Project",
                new XAttribute("Sdk", "Microsoft.NET.Sdk"),
                new XElement(
                    "PropertyGroup",
                    new XElement("AssemblyName", "EntryAppHost"),
                    new XElement("AllowUnsafeBlocks", "true"),
                    new XElement("EnableDefaultCompileItems", "false"),
                    new XElement("ImplicitUsings", "enable"),
                    new XElement("Nullable", "enable"),
                    new XElement("OutputType", "Exe"),
                    new XElement("UseAppHost", "true"),
                    new XElement("TargetFramework", "net10.0")),
                new XElement(
                    "ItemGroup",
                    new XElement(
                        "Compile",
                        new XAttribute("Include", Path.Join(
                            repositoryRoot, "tests", "Csls.TestProcessHost", "*.cs")))),
                CreateIncompleteAppHostsTarget()));
        return File.WriteAllTextAsync(
            Path.Join(projectDirectory, "EntryAppHost.csproj"), project.ToString(), cancellationToken);
    }

    private static XElement CreateIncompleteAppHostsTarget()
    {
        string appHostName = OperatingSystem.IsWindows() ? "EntryAppHost.exe" : "EntryAppHost";
        string appHost = $"$(TargetDir){appHostName}";
        string missingAssembly = $"$(TargetDir)MissingAssembly/{appHostName}";
        string missingConfiguration = $"$(TargetDir)MissingRuntimeConfig/{appHostName}";
        const string missingConfigurationAssembly = "$(TargetDir)MissingRuntimeConfig/EntryAppHost.dll";

        // Executable writes stay in the isolated build process. Forking from the parallel
        // test host while a copy is open can leave another child holding its write descriptor.
        return new XElement("Target",
            new XAttribute("Name", "PrepareIncompleteAppHosts"),
            new XAttribute("AfterTargets", "Build"),
            new XAttribute("Inputs", $"$(MSBuildAllProjects);$(TargetPath);{appHost}"),
            new XAttribute("Outputs", $"{missingAssembly};{missingConfiguration};{missingConfigurationAssembly}"),
            CreateFixtureCopyTask(appHost, missingAssembly),
            CreateFixtureCopyTask(appHost, missingConfiguration),
            CreateFixtureCopyTask("$(TargetPath)", missingConfigurationAssembly));
    }

    private static XElement CreateFixtureCopyTask(string source, string destination) => new("Copy",
        new XAttribute("SourceFiles", source),
        new XAttribute("DestinationFiles", destination),
        new XAttribute("SkipUnchangedFiles", "true"),
        new XElement("Output",
            new XAttribute("TaskParameter", "CopiedFiles"),
            new XAttribute("ItemName", "FileWrites")));

    private static async Task<string> WriteSolutionAsync(
        string fixtureDirectory,
        bool includeWindowsPdb,
        CancellationToken cancellationToken)
    {
        string[] projectNames = includeWindowsPdb
            ? ["SourceLinkValid", "SourceLinkImplicit", "SourceLinkMismatched", "SourceLinkCancellation", "SourceLinkQueued", "SymbolFreeFixture", "WindowsPdbFixture", "EntryAppHost"]
            : ["SourceLinkValid", "SourceLinkImplicit", "SourceLinkMismatched", "SourceLinkCancellation", "SourceLinkQueued", "SymbolFreeFixture", "EntryAppHost"];
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
        startInfo.ArgumentList.Add($"-bl:{Path.Join(DebuggerTestEnvironment.FindRepositoryRoot(),
            "artifacts", "diagnostics", "debugger-symbol-fixtures", "{}.binlog")}");
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
