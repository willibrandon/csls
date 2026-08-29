using System.Xml.Linq;

namespace Csls.Workspaces;

/// <summary>
/// Identifies temporary project files emitted by the .NET file-app evaluator.
/// </summary>
internal static class FileBasedAppProjectArtifact
{
    /// <summary>
    /// Determines whether a project is the generated sidecar for one exact entry point.
    /// </summary>
    /// <param name="projectPath">The candidate generated project path.</param>
    /// <param name="entryPointPath">The expected file-app entry point.</param>
    /// <returns>True only when the generated project properties identify the entry point.</returns>
    internal static bool IsGeneratedForEntryPoint(
        string projectPath,
        string entryPointPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(entryPointPath);
        XDocument project;
        try
        {
            project = XDocument.Load(projectPath);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            return false;
        }

        string? fileBasedProgram = project
            .Descendants()
            .FirstOrDefault(static element =>
                element.Name.LocalName.Equals(
                    "FileBasedProgram",
                    StringComparison.Ordinal))
            ?.Value;
        string? declaredEntryPointPath = project
            .Descendants()
            .FirstOrDefault(static element =>
                element.Name.LocalName.Equals(
                    "EntryPointFilePath",
                    StringComparison.Ordinal))
            ?.Value;
        return bool.TryParse(fileBasedProgram, out bool isFileBasedProgram) &&
            isFileBasedProgram &&
            !string.IsNullOrWhiteSpace(declaredEntryPointPath) &&
            string.Equals(
                Path.GetFullPath(declaredEntryPointPath),
                Path.GetFullPath(entryPointPath),
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal);
    }
}
