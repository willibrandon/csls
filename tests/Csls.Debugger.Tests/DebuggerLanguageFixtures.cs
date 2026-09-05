namespace Csls.Debugger.Tests;

/// <summary>
/// Resolves the C#, Visual Basic, and F# programs prepared by the debugger test build.
/// </summary>
internal static class DebuggerLanguageFixtures
{
    /// <summary>
    /// Gets one language-fixture program from the shared test build outputs.
    /// </summary>
    /// <param name="project">The language-fixture project name.</param>
    /// <param name="configuration">The Debug or Release configuration.</param>
    /// <returns>The absolute managed program path.</returns>
    internal static string GetProgramPath(string project, string configuration)
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
        string programPath = Path.GetFullPath(Path.Join(
            AppContext.BaseDirectory,
            "..",
            "..",
            project,
            configurationDirectory,
            $"{project}.dll"));
        return File.Exists(programPath)
            ? programPath
            : throw new FileNotFoundException(
                $"The {configuration} debugger fixture was not built for {project}.",
                programPath);
    }
}
