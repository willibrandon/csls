#!/usr/bin/env -S dotnet --
#:property TargetFramework=net10.0
#:property LangVersion=14.0
#:property Nullable=enable
#:property TreatWarningsAsErrors=true

using System.Text.Json;

if (args.Length == 1 && args[0] is "--help" or "-h" or "-?")
{
    await Console.Out.WriteLineAsync(
        "Fails when a CodeQL SARIF result contains any unresolved finding.")
        .ConfigureAwait(false);
    await Console.Out.WriteLineAsync(
        "Usage: dotnet run --file scripts/Verify-CodeQl.cs -- <sarif-directory>")
        .ConfigureAwait(false);
    return 0;
}

if (args.Length != 1)
{
    await Console.Error.WriteLineAsync(
        "Usage: dotnet run --file scripts/Verify-CodeQl.cs -- <sarif-directory>")
        .ConfigureAwait(false);
    return 2;
}

string sarifDirectory = Path.GetFullPath(args[0]);
if (!Directory.Exists(sarifDirectory))
{
    await Console.Error.WriteLineAsync(
        $"CodeQL SARIF directory does not exist: {sarifDirectory}").ConfigureAwait(false);
    return 1;
}

string[] sarifPaths =
[
    .. Directory
        .EnumerateFiles(sarifDirectory, "*.sarif", SearchOption.AllDirectories)
        .Order(StringComparer.Ordinal)
];
if (sarifPaths.Length == 0)
{
    await Console.Error.WriteLineAsync(
        $"CodeQL produced no SARIF files under {sarifDirectory}.").ConfigureAwait(false);
    return 1;
}

var findings = new List<string>();
int ignoredGeneratedFindingCount = 0;
foreach (string sarifPath in sarifPaths)
{
    using FileStream stream = File.OpenRead(sarifPath);
    using JsonDocument document = await JsonDocument.ParseAsync(stream).ConfigureAwait(false);
    if (!document.RootElement.TryGetProperty("runs", out JsonElement runs) ||
        runs.ValueKind != JsonValueKind.Array)
    {
        throw new InvalidDataException($"CodeQL SARIF has no runs array: {sarifPath}");
    }

    foreach (JsonElement run in runs.EnumerateArray())
    {
        if (!run.TryGetProperty("results", out JsonElement results) ||
            results.ValueKind != JsonValueKind.Array)
        {
            continue;
        }

        foreach (JsonElement result in results.EnumerateArray())
        {
            string ruleId = ReadString(result, "ruleId", "unknown-rule");
            if (IsSystemTextJsonGeneratedFinding(result, ruleId))
            {
                ignoredGeneratedFindingCount++;
                continue;
            }

            string level = ReadString(result, "level", "warning");
            string message = result.TryGetProperty("message", out JsonElement messageElement)
                ? ReadString(messageElement, "text", "CodeQL finding")
                : "CodeQL finding";
            string location = ReadLocation(result);
            findings.Add($"{level}: {ruleId}: {location}: {message}");
        }
    }
}

if (findings.Count != 0)
{
    foreach (string finding in findings.Order(StringComparer.Ordinal))
    {
        await Console.Error.WriteLineAsync(finding).ConfigureAwait(false);
    }

    await Console.Error.WriteLineAsync(
        $"CodeQL reported {findings.Count} unresolved finding(s).").ConfigureAwait(false);
    return 1;
}

await Console.Out.WriteLineAsync(ignoredGeneratedFindingCount == 0
    ? $"Verified {sarifPaths.Length} CodeQL SARIF file(s) with no findings."
    : $"Verified {sarifPaths.Length} CodeQL SARIF file(s) with no product-source " +
        $"findings and ignored {ignoredGeneratedFindingCount} known System.Text.Json " +
        "source-generator finding(s).").ConfigureAwait(false);
return 0;

static string ReadString(JsonElement element, string name, string fallback) =>
    element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
        ? value.GetString() ?? fallback
        : fallback;

static string ReadLocation(JsonElement result)
{
    if (!result.TryGetProperty("locations", out JsonElement locations) ||
        locations.ValueKind != JsonValueKind.Array ||
        locations.GetArrayLength() == 0)
    {
        return "unknown-location";
    }

    JsonElement location = locations[0];
    if (!location.TryGetProperty("physicalLocation", out JsonElement physicalLocation))
    {
        return "unknown-location";
    }

    string path = physicalLocation.TryGetProperty(
        "artifactLocation",
        out JsonElement artifactLocation)
        ? ReadString(artifactLocation, "uri", "unknown-location")
        : "unknown-location";
    if (!physicalLocation.TryGetProperty("region", out JsonElement region) ||
        !region.TryGetProperty("startLine", out JsonElement startLine) ||
        !startLine.TryGetInt32(out int line))
    {
        return path;
    }

    return $"{path}:{line}";
}

static bool IsSystemTextJsonGeneratedFinding(JsonElement result, string ruleId)
{
    if (!string.Equals(ruleId, "cs/useless-cast-to-self", StringComparison.Ordinal) ||
        !result.TryGetProperty("locations", out JsonElement locations) ||
        locations.ValueKind != JsonValueKind.Array ||
        locations.GetArrayLength() == 0)
    {
        return false;
    }

    foreach (JsonElement location in locations.EnumerateArray())
    {
        if (!location.TryGetProperty("physicalLocation", out JsonElement physicalLocation) ||
            !physicalLocation.TryGetProperty("artifactLocation", out JsonElement artifactLocation))
        {
            return false;
        }

        string path = ReadString(artifactLocation, "uri", string.Empty).Replace('\\', '/');
        if (!path.StartsWith("artifacts/obj/", StringComparison.OrdinalIgnoreCase) ||
            !path.Contains(
                "/generated/System.Text.Json.SourceGeneration/" +
                "System.Text.Json.SourceGeneration.JsonSourceGenerator/",
                StringComparison.Ordinal))
        {
            return false;
        }
    }

    return true;
}
