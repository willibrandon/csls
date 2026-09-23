using Csls.Debugger.Evaluation;
using System.Buffers;
using System.Collections;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Csls.Debugger;

/// <summary>
/// Configures the supervised debugger worker's platform lifecycle environment.
/// </summary>
public static class DebuggerWorkerEnvironment
{
    private const string InheritedLoaderEnvironmentVariable = "CSLS_DEBUGGER_INHERITED_LOADER_ENVIRONMENT";

    /// <summary>
    /// Initializes platform lifecycle support at the start of the debugger worker.
    /// </summary>
    public static void InitializeCurrentProcess()
    {
        if (!OperatingSystem.IsWindows())
        {
            UnixWaitStatusInterposer.Initialize();
        }
    }

    /// <summary>
    /// Adds the packaged Unix child-status interposer to a worker process start.
    /// </summary>
    /// <param name="startInfo">The worker process start information to configure.</param>
    /// <param name="workerPath">The absolute packaged worker executable or assembly path.</param>
    public static void Configure(ProcessStartInfo startInfo, string workerPath)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        ArgumentException.ThrowIfNullOrWhiteSpace(workerPath);
        DebuggerEvaluatorWorkerEnvironment.Configure(startInfo, workerPath);
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        string libraryName = OperatingSystem.IsMacOS()
            ? "Csls.Debugger.UnixWait.dylib"
            : "Csls.Debugger.UnixWait.so";
        string workerDirectory = Path.GetDirectoryName(workerPath)
            ?? throw new InvalidOperationException(
                $"Debugger worker {workerPath} has no containing directory.");
        string libraryPath = Path.GetFullPath(Path.Join(
            workerDirectory,
            libraryName));
        if (!File.Exists(libraryPath))
        {
            throw new FileNotFoundException(
                "The debugger's Unix child-status interposer is missing. Reinstall csls.",
                libraryPath);
        }

        string variableName = OperatingSystem.IsMacOS()
            ? "DYLD_INSERT_LIBRARIES"
            : "LD_PRELOAD";
        string searchVariableName = OperatingSystem.IsMacOS()
            ? "DYLD_LIBRARY_PATH"
            : "LD_LIBRARY_PATH";
        var snapshot = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(snapshot))
        {
            writer.WriteStartObject();
            startInfo.Environment.TryGetValue(variableName, out string? originalPreload);
            writer.WriteString(variableName, originalPreload);
            startInfo.Environment.TryGetValue(searchVariableName, out string? originalSearchPath);
            writer.WriteString(searchVariableName, originalSearchPath);
            writer.WriteEndObject();
        }

        startInfo.Environment[InheritedLoaderEnvironmentVariable] = Encoding.UTF8.GetString(snapshot.WrittenSpan);
        startInfo.Environment.TryGetValue(variableName, out string? inherited);
        startInfo.Environment[variableName] = inherited is null
            ? libraryPath
            : $"{libraryPath}{Path.PathSeparator}{inherited}";

        startInfo.Environment.TryGetValue(searchVariableName, out string? inheritedSearchPath);
        startInfo.Environment[searchVariableName] = inheritedSearchPath is null
            ? workerDirectory
            : $"{workerDirectory}{Path.PathSeparator}{inheritedSearchPath}";
    }

    /// <summary>
    /// Copies the target's inherited environment with its original native loader settings.
    /// </summary>
    /// <returns>The caller's environment before explicit target overrides are applied.</returns>
    internal static Dictionary<string, string> CreateTargetEnvironment()
    {
        StringComparer comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var environment = new Dictionary<string, string>(comparer);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string name && entry.Value is string value)
            {
                environment[name] = value;
            }
        }

        if (environment.Remove(InheritedLoaderEnvironmentVariable, out string? snapshot))
        {
            using var document = JsonDocument.Parse(snapshot);
            RestoreLoaderSetting(environment, document.RootElement,
                OperatingSystem.IsMacOS() ? "DYLD_INSERT_LIBRARIES" : "LD_PRELOAD");
            RestoreLoaderSetting(environment, document.RootElement,
                OperatingSystem.IsMacOS() ? "DYLD_LIBRARY_PATH" : "LD_LIBRARY_PATH");
        }

        return environment;
    }

    private static void RestoreLoaderSetting(Dictionary<string, string> environment, JsonElement snapshot, string name)
    {
        JsonElement value = snapshot.GetProperty(name);
        if (value.ValueKind == JsonValueKind.Null)
        {
            environment.Remove(name);
        }
        else
        {
            environment[name] = value.GetString()
                ?? throw new InvalidDataException($"The inherited loader setting '{name}' is invalid.");
        }
    }
}
