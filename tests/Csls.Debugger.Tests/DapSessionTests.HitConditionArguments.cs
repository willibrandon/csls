using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Writes source and function hit-condition breakpoint requests.
/// </summary>
public sealed partial class DapSessionTests
{
    private static void WriteHitConditionBreakpointArguments(
        Utf8JsonWriter writer,
        bool useFunctionBreakpoint,
        string? condition,
        string? hitCondition)
    {
        if (useFunctionBreakpoint)
        {
            WriteHitFunctionBreakpointArguments(writer, condition, hitCondition);
        }
        else
        {
            WriteHitSourceBreakpointArguments(writer, condition, hitCondition);
        }
    }

    private static void WriteHitFunctionBreakpointArguments(
        Utf8JsonWriter writer,
        string? condition,
        string? hitCondition)
    {
        writer.WriteStartObject();
        writer.WriteStartArray("breakpoints");
        writer.WriteStartObject();
        writer.WriteString("name", "Csls.TestProcessHost.DebuggerHitFixture.RecordHit");
        WriteOptionalBreakpointPredicate(writer, "condition", condition);
        WriteOptionalBreakpointPredicate(writer, "hitCondition", hitCondition);
        writer.WriteEndObject();
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteHitSourceBreakpointArguments(
        Utf8JsonWriter writer,
        string? condition,
        string? hitCondition)
    {
        string sourcePath = Path.Join(
            FindRepositoryRoot(),
            "tests",
            "Csls.TestProcessHost",
            "DebuggerHitFixture.cs");
        int line = FindSourceLine(File.ReadAllLines(sourcePath), "GC.KeepAlive(observedHit);");
        writer.WriteStartObject();
        writer.WriteStartObject("source");
        writer.WriteString("path", sourcePath);
        writer.WriteEndObject();
        writer.WriteStartArray("breakpoints");
        writer.WriteStartObject();
        writer.WriteNumber("line", line);
        WriteOptionalBreakpointPredicate(writer, "condition", condition);
        WriteOptionalBreakpointPredicate(writer, "hitCondition", hitCondition);
        writer.WriteEndObject();
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteOptionalBreakpointPredicate(
        Utf8JsonWriter writer,
        string name,
        string? value)
    {
        if (value is not null)
        {
            writer.WriteString(name, value);
        }
    }
}
