using System.Reflection.PortableExecutable;
using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Prepares the real Portable PDB symbol-server DAP fixture from the shared test build.
/// </summary>
public sealed partial class DapSessionTests
{
    private static (string ProgramPath, string SourcePath, string PdbPath, int BreakpointLine)
        PrepareSymbolServerFixture(string testDirectory)
    {
        const string project = "Csls.Debugger.Fixtures.CSharp";
        string sourceProgram = DebuggerLanguageFixtures.GetProgramPath(project, "Debug");
        string sourceDirectory = Path.GetDirectoryName(sourceProgram)
            ?? throw new InvalidOperationException("The shared debugger fixture has no output directory.");
        foreach (string file in Directory.EnumerateFiles(sourceDirectory))
        {
            File.Copy(file, Path.Join(testDirectory, Path.GetFileName(file)));
        }

        string sourcePath = Path.Join(FindRepositoryRoot(), "test-assets", project, "Program.cs");
        int breakpointLine = FindSourceLine(File.ReadAllLines(sourcePath), "answer++;");
        return (
            Path.Join(testDirectory, $"{project}.dll"),
            sourcePath,
            Path.Join(testDirectory, $"{project}.pdb"),
            breakpointLine);
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
        string programDirectory = Path.GetDirectoryName(programPath)
            ?? throw new InvalidOperationException("The symbol-server fixture has no program directory.");
        writer.WriteStartObject();
        writer.WriteBoolean("noDebug", false);
        writer.WriteString("program", programPath);
        writer.WriteStartArray("args");
        writer.WriteStringValue(Path.Join(programDirectory, "continue.signal"));
        writer.WriteStringValue("41");
        writer.WriteStringValue("ready");
        writer.WriteEndArray();
        writer.WriteStartObject("sourceFileMap");
        writer.WriteString("/_/", FindRepositoryRoot());
        writer.WriteEndObject();
        writer.WriteStartObject("symbolOptions");
        writer.WriteStartArray("searchPaths");
        writer.WriteStringValue(serverUrl);
        writer.WriteEndArray();
        writer.WriteString("cachePath", cachePath);
        writer.WriteStartObject("moduleFilter");
        writer.WriteString("mode", "loadOnlyIncluded");
        writer.WriteStartArray("includedModules");
        writer.WriteStringValue("Csls.Debugger.Fixtures.CSharp.dll");
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
