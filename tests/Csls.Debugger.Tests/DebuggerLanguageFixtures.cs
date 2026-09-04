using System.Diagnostics;

namespace Csls.Debugger.Tests;

/// <summary>
/// Owns shared C#, Visual Basic, and F# programs for debugger protocol tests.
/// </summary>
internal sealed class DebuggerLanguageFixtures : IAsyncDisposable
{
    private readonly string _fixtureDirectory;

    private DebuggerLanguageFixtures(string fixtureDirectory)
    {
        _fixtureDirectory = fixtureDirectory;
    }

    /// <summary>
    /// Builds the Debug and Release language fixtures once for the test class.
    /// </summary>
    /// <param name="cancellationToken">The fixture-build cancellation token.</param>
    /// <returns>The initialized fixture owner.</returns>
    internal static async Task<DebuggerLanguageFixtures> CreateAsync(
        CancellationToken cancellationToken)
    {
        string fixtureDirectory = DebuggerTestPath.Canonicalize(Path.Join(
            Path.GetTempPath(),
            $"csls-debugger-languages-{Guid.NewGuid():N}"));
        Directory.CreateDirectory(fixtureDirectory);
        var fixtures = new DebuggerLanguageFixtures(fixtureDirectory);
        try
        {
            await fixtures.BuildAsync(
                "Debug",
                noRestore: false,
                cancellationToken).ConfigureAwait(false);
            await fixtures.BuildAsync(
                "Release",
                noRestore: true,
                cancellationToken).ConfigureAwait(false);
            return fixtures;
        }
        catch
        {
            await fixtures.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Gets one previously built language-fixture program.
    /// </summary>
    /// <param name="project">The language-fixture project name.</param>
    /// <param name="configuration">The Debug or Release configuration.</param>
    /// <returns>The absolute managed program path.</returns>
    internal string GetProgramPath(string project, string configuration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(project);
        string configurationDirectory = configuration switch
        {
            "Debug" => "debug",
            "Release" => "release",
            _ => throw new ArgumentOutOfRangeException(
                nameof(configuration),
                configuration,
                "Debugger language fixtures support Debug and Release configurations.")
        };
        string programPath = Path.Join(
            _fixtureDirectory,
            "bin",
            project,
            configurationDirectory,
            $"{project}.dll");
        return File.Exists(programPath)
            ? programPath
            : throw new FileNotFoundException(
                $"The {configuration} debugger fixture was not built for {project}.",
                programPath);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => new(DebuggerTestDirectoryReleaseWaiter.DeleteAsync(
        _fixtureDirectory,
        TimeSpan.FromSeconds(10)));

    private async Task BuildAsync(
        string configuration,
        bool noRestore,
        CancellationToken cancellationToken)
    {
        string repositoryRoot = DebuggerTestEnvironment.FindRepositoryRoot();
        var startInfo = new ProcessStartInfo(
            Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            WorkingDirectory = repositoryRoot
        };
        startInfo.ArgumentList.Add("build");
        startInfo.ArgumentList.Add(Path.Join(
            "test-assets",
            "Csls.Debugger.LanguageFixtures.slnx"));
        startInfo.ArgumentList.Add("--configuration");
        startInfo.ArgumentList.Add(configuration);
        startInfo.ArgumentList.Add($"--property:ArtifactsPath={_fixtureDirectory}");
        startInfo.ArgumentList.Add("--nologo");
        startInfo.ArgumentList.Add("--disable-build-servers");
        if (noRestore)
        {
            startInfo.ArgumentList.Add("--no-restore");
        }

        (int exitCode, string output, string error) = await DebuggerTestProcess.RunAsync(
            startInfo,
            cancellationToken).ConfigureAwait(false);
        if (exitCode != 0)
        {
            throw new InvalidOperationException(string.Concat(
                "Fixture build failed for ",
                configuration,
                ":",
                Environment.NewLine,
                output,
                error));
        }
    }
}
