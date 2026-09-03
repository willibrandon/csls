using Csls.Debugger;
using System.Text.Json;

namespace Csls.DebugAdapter;

/// <summary>
/// Validates a concrete DAP launch request without invoking a shell.
/// </summary>
internal static class DapLaunchOptionsParser
{
    /// <summary>
    /// Parses a managed or no-debug launch request.
    /// </summary>
    /// <param name="arguments">The DAP launch arguments.</param>
    /// <returns>The validated launch mode and process options.</returns>
    internal static DapLaunchConfiguration Parse(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("The launch request requires an arguments object.");
        }

        bool noDebug = arguments.TryGetProperty("noDebug", out JsonElement noDebugValue) &&
            noDebugValue.ValueKind == JsonValueKind.True;
        if (DapBooleanOptionParser.Get(arguments, "stopAtEntry", defaultValue: false))
        {
            throw new ArgumentException(
                "The launch stopAtEntry option is not supported by this debugger capability set.");
        }

        string program = GetRequiredString(arguments, "program");
        if (!Path.IsPathFullyQualified(program))
        {
            throw new ArgumentException("The launch program path must be absolute.");
        }

        program = Path.GetFullPath(program);
        if (!File.Exists(program))
        {
            throw new FileNotFoundException("The launch program does not exist.", program);
        }

        string workingDirectory = arguments.TryGetProperty("cwd", out JsonElement cwdValue)
            ? GetString(cwdValue, "cwd")
            : Path.GetDirectoryName(program) ?? Directory.GetCurrentDirectory();
        if (!Path.IsPathFullyQualified(workingDirectory))
        {
            throw new ArgumentException("The launch working directory must be absolute.");
        }

        workingDirectory = Path.GetFullPath(workingDirectory);
        if (!Directory.Exists(workingDirectory))
        {
            throw new DirectoryNotFoundException(
                $"The launch working directory does not exist: {workingDirectory}");
        }

        var result = new DapLaunchConfiguration
        {
            NoDebug = noDebug,
            Options = new DebuggeeLaunchOptions
            {
                Program = program,
                WorkingDirectory = workingDirectory,
                Arguments = ParseArguments(arguments),
                Environment = ParseEnvironment(arguments),
                RuntimeHostPath = ResolveRuntimeHost(arguments),
                SourceFileMap = DapSourceOptionsParser.ParseSourceFileMap(arguments),
                SourceLinkOptions = DapSourceOptionsParser.ParseSourceLinkOptions(arguments),
                SymbolOptions = DapSymbolOptionsParser.Parse(arguments),
                SuppressJitOptimizations = DapBooleanOptionParser.Get(
                    arguments,
                    "suppressJITOptimizations",
                    defaultValue: false),
                JustMyCode = DapBooleanOptionParser.Get(
                    arguments,
                    "justMyCode",
                    defaultValue: true),
                EnableStepFiltering = DapBooleanOptionParser.Get(
                    arguments,
                    "enableStepFiltering",
                    defaultValue: true)
            }
        };
        DebuggerEngine.ValidateSourceOptions(
            result.Options.SourceFileMap,
            result.Options.SourceLinkOptions,
            result.Options.SymbolOptions);
        return result;
    }

    private static List<string> ParseArguments(JsonElement arguments)
    {
        if (!arguments.TryGetProperty("args", out JsonElement values))
        {
            return [];
        }

        if (values.ValueKind != JsonValueKind.Array)
        {
            throw new ArgumentException("The launch args value must be an array of strings.");
        }

        List<string> result = [];
        foreach (JsonElement value in values.EnumerateArray())
        {
            result.Add(GetString(value, "args"));
        }

        return result;
    }

    private static Dictionary<string, string?> ParseEnvironment(JsonElement arguments)
    {
        Dictionary<string, string?> result = new(StringComparer.Ordinal);
        if (!arguments.TryGetProperty("env", out JsonElement environment))
        {
            return result;
        }

        if (environment.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("The launch env value must be an object.");
        }

        foreach (JsonProperty property in environment.EnumerateObject())
        {
            result[property.Name] = property.Value.ValueKind switch
            {
                JsonValueKind.String => property.Value.GetString(),
                JsonValueKind.Null => null,
                _ => throw new ArgumentException(
                    $"The launch environment value '{property.Name}' must be a string or null.")
            };
        }

        return result;
    }

    private static string? ResolveRuntimeHost(JsonElement arguments)
    {
        if (arguments.TryGetProperty("runtimeHost", out JsonElement runtimeHost))
        {
            string path = GetString(runtimeHost, "runtimeHost");
            if (!Path.IsPathFullyQualified(path) || !File.Exists(path))
            {
                throw new ArgumentException("runtimeHost must identify an existing absolute file.");
            }

            return Path.GetFullPath(path);
        }

        string? configuredPath = Environment.GetEnvironmentVariable("CSLS_RUNTIME_HOST_PATH");
        return string.IsNullOrWhiteSpace(configuredPath) ? null : configuredPath;
    }

    private static string GetRequiredString(JsonElement owner, string propertyName)
    {
        if (!owner.TryGetProperty(propertyName, out JsonElement value))
        {
            throw new ArgumentException($"The launch request requires '{propertyName}'.");
        }

        return GetString(value, propertyName);
    }

    private static string GetString(JsonElement value, string propertyName)
    {
        if (value.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new ArgumentException($"The launch '{propertyName}' value must be a non-empty string.");
        }

        return value.GetString()!;
    }
}
