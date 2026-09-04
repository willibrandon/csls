using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Provides shared programs and launch arguments for JIT-policy tests.
/// </summary>
public sealed partial class DapSessionTests
{
    private static string GetJitFixture(string configuration = "Release") =>
        LanguageFixtures.GetProgramPath("Csls.Debugger.Fixtures.CSharp", configuration);

    private static void WriteJitLaunchArguments(
        Utf8JsonWriter writer,
        string programPath,
        string waitPath,
        bool suppressJitOptimizations,
        bool enableHotReload)
    {
        writer.WriteStartObject();
        writer.WriteString("program", programPath);
        writer.WriteStartArray("args");
        writer.WriteStringValue(waitPath);
        writer.WriteStringValue("41");
        writer.WriteStringValue("ready");
        writer.WriteEndArray();
        writer.WriteBoolean("suppressJITOptimizations", suppressJitOptimizations);
        writer.WriteBoolean("enableHotReload", enableHotReload);
        writer.WriteEndObject();
    }
}
