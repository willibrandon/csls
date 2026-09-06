using Csls.Debugger;
using Csls.Debugger.Contracts;
using System.Text.Json;

namespace Csls.DebugAdapter;

/// <summary>
/// Validates mutually exclusive live-process and process-dump attach destinations.
/// </summary>
internal static class DapAttachOptionsParser
{
    /// <summary>
    /// Parses one process identifier or dump path and its runtime options.
    /// </summary>
    /// <param name="arguments">The DAP attach arguments.</param>
    /// <returns>The validated attach configuration.</returns>
    internal static DapAttachConfiguration Parse(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("The attach request requires an object containing processId or dumpPath.");
        }

        bool hasProcess = arguments.TryGetProperty("processId", out JsonElement processIdValue);
        bool hasDump = arguments.TryGetProperty("dumpPath", out JsonElement dumpPathValue);
        if (hasProcess && hasDump)
        {
            throw new ArgumentException("The attach request requires exactly one of processId and dumpPath.");
        }

        if (hasDump)
        {
            string dumpPath = ParseAbsolutePath(dumpPathValue, "dumpPath");
            string? liveOption = arguments.EnumerateObject().Select(static option => option.Name)
                .FirstOrDefault(static name => name is "sourceFileMap" or "sourceLinkOptions" or "symbolOptions" or
                    "requireExactSource" or "justMyCode" or "enableStepFiltering");
            if (liveOption is not null)
            {
                throw new ArgumentException(
                    $"The attach {liveOption} option applies to a live processId target.");
            }
            int runtimeIndex = 0;
            if (arguments.TryGetProperty("runtimeIndex", out JsonElement index) &&
                (index.ValueKind != JsonValueKind.Number || !index.TryGetInt32(out runtimeIndex) || runtimeIndex < 0))
            {
                throw new ArgumentException("The attach runtimeIndex must be a nonnegative integer.");
            }

            string? dacPath = arguments.TryGetProperty("dacPath", out JsonElement dac)
                ? ParseAbsolutePath(dac, "dacPath")
                : null;
            return new DapDumpAttachConfiguration
            {
                Options = new DebugDumpOpenRequest(dumpPath, runtimeIndex, dacPath)
            };
        }

        if (!hasProcess || processIdValue.ValueKind != JsonValueKind.Number ||
            !processIdValue.TryGetInt32(out int processId) ||
            processId <= 0)
        {
            throw new ArgumentException(
                "The attach request requires a positive integer processId.");
        }

        if (arguments.TryGetProperty("runtimeIndex", out _) || arguments.TryGetProperty("dacPath", out _))
        {
            throw new ArgumentException("The attach runtimeIndex and dacPath options require dumpPath.");
        }

        var result = new DapProcessAttachConfiguration
        {
            Options = new DebuggeeAttachOptions
            {
                ProcessId = processId,
                SourceFileMap = DapSourceOptionsParser.ParseSourceFileMap(arguments),
                RequireExactSource = DapBooleanOptionParser.Get(arguments, "requireExactSource", defaultValue: true),
                SourceLinkOptions = DapSourceOptionsParser.ParseSourceLinkOptions(arguments),
                SymbolOptions = DapSymbolOptionsParser.Parse(arguments),
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

    private static string ParseAbsolutePath(JsonElement value, string name)
    {
        string? path = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException($"The attach {name} must be a nonempty absolute path.");
        }

        return Path.GetFullPath(path);
    }
}
