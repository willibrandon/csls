#!/usr/bin/env -S dotnet --
#:property TargetFramework=net10.0
#:property LangVersion=14.0
#:property Nullable=enable
#:property TreatWarningsAsErrors=true

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

const string Usage = "Usage: dotnet run --file scripts/Generate-DapProtocol.cs [--verify]";

if (args.Length == 1 && args[0] is "--help" or "-h" or "-?")
{
    await Console.Out.WriteLineAsync(
        "Generates deterministic C# contracts from the checked-in Debug Adapter Protocol schema.")
        .ConfigureAwait(false);
    await Console.Out.WriteLineAsync(Usage).ConfigureAwait(false);
    return 0;
}

bool verify = args.Length == 1 && string.Equals(args[0], "--verify", StringComparison.Ordinal);
if (args.Length != 0 && !verify)
{
    await Console.Error.WriteLineAsync(Usage).ConfigureAwait(false);
    return 2;
}

string repositoryRoot = FindRepositoryRoot();
string schemaPath = Path.Join(
    repositoryRoot,
    "src",
    "Csls.DebugAdapter",
    "Protocol",
    "debugAdapterProtocol.json");
byte[] schemaBytes = await File.ReadAllBytesAsync(schemaPath).ConfigureAwait(false);
string schemaDigest = Convert.ToHexString(SHA256.HashData(schemaBytes));
using var schema = JsonDocument.Parse(schemaBytes);
JsonElement definitionsElement = schema.RootElement.GetProperty("definitions");
var definitions = definitionsElement
    .EnumerateObject()
    .ToDictionary(static property => property.Name, static property => property.Value, StringComparer.Ordinal);
var protocolTypes = new Dictionary<string, JsonElement>(definitions, StringComparer.Ordinal);
DiscoverInlineTypes(protocolTypes);
var enumNames = protocolTypes
    .Where(static pair => IsEnum(pair.Value))
    .Select(static pair => pair.Key)
    .ToHashSet(StringComparer.Ordinal);

var outputs = new SortedDictionary<string, string>(StringComparer.Ordinal);
foreach ((string name, JsonElement definition) in protocolTypes)
{
    outputs[$"{name}.g.cs"] = IsEnum(definition)
        ? GenerateEnum(name, definition, schemaDigest)
        : GenerateClass(name, protocolTypes, enumNames, schemaDigest);
}

outputs["DapProtocolJsonSerializerContext.g.cs"] = GenerateSerializerContext(
    protocolTypes.Keys,
    schemaDigest);

string outputDirectory = Path.Join(
    repositoryRoot,
    "src",
    "Csls.DebugAdapter",
    "Protocol",
    "Generated");
Directory.CreateDirectory(outputDirectory);
var failures = new List<string>();
foreach ((string fileName, string content) in outputs)
{
    string outputPath = Path.Join(outputDirectory, fileName);
    if (verify)
    {
        if (!File.Exists(outputPath) ||
            !string.Equals(
                await File.ReadAllTextAsync(outputPath).ConfigureAwait(false),
                content,
                StringComparison.Ordinal))
        {
            failures.Add($"Generated DAP contract is stale: {Path.GetRelativePath(repositoryRoot, outputPath)}");
        }

        continue;
    }

    await File.WriteAllTextAsync(outputPath, content, new UTF8Encoding(false)).ConfigureAwait(false);
}

foreach (string existingPath in Directory.EnumerateFiles(outputDirectory, "*.g.cs"))
{
    if (outputs.ContainsKey(Path.GetFileName(existingPath)))
    {
        continue;
    }

    if (verify)
    {
        failures.Add(
            $"Generated DAP contract is no longer produced: " +
            Path.GetRelativePath(repositoryRoot, existingPath));
    }
    else
    {
        File.Delete(existingPath);
    }
}

if (failures.Count != 0)
{
    foreach (string failure in failures)
    {
        await Console.Error.WriteLineAsync(failure).ConfigureAwait(false);
    }

    return 1;
}

await Console.Out.WriteLineAsync(
    $"{(verify ? "Verified" : "Generated")} {outputs.Count} DAP contract files " +
    $"from schema SHA-256 {schemaDigest}.").ConfigureAwait(false);
return 0;

static string GenerateEnum(string name, JsonElement definition, string schemaDigest)
{
    StringBuilder source = CreateHeader(schemaDigest);
    source.AppendLine("using System.CodeDom.Compiler;")
        .AppendLine("using System.Text.Json.Serialization;")
        .AppendLine()
        .AppendLine("namespace Csls.DebugAdapter.Protocol;")
        .AppendLine()
        .AppendLine("/// <summary>")
        .Append("/// Represents the DAP ").Append(name).AppendLine(" value set.")
        .AppendLine("/// </summary>")
        .AppendLine("[GeneratedCode(\"Generate-DapProtocol\", \"1.0\")]")
        .Append("[JsonConverter(typeof(JsonStringEnumConverter<").Append(name).AppendLine(">))]")
        .Append("internal enum ").AppendLine(name)
        .AppendLine("{");
    foreach (JsonElement valueElement in definition.GetProperty("enum").EnumerateArray())
    {
        string value = valueElement.GetString()
            ?? throw new InvalidDataException($"DAP enum {name} contains a null value.");
        string memberName = ToIdentifier(value);
        source.AppendLine("    /// <summary>")
            .Append("    /// Represents the DAP '").Append(value).AppendLine("' value.")
            .AppendLine("    /// </summary>")
            .Append("    [JsonStringEnumMemberName(\"").Append(EscapeString(value)).AppendLine("\")]")
            .Append("    ").Append(memberName).AppendLine(",");
    }

    return source.AppendLine("}").ToString();
}

static string GenerateClass(
    string name,
    IReadOnlyDictionary<string, JsonElement> definitions,
    IReadOnlySet<string> enumNames,
    string schemaDigest)
{
    (SortedDictionary<string, JsonElement> Properties, HashSet<string> Required) =
        CollectProperties(name, definitions, []);
    StringBuilder source = CreateHeader(schemaDigest);
    source.AppendLine("using System.CodeDom.Compiler;")
        .AppendLine("using System.Text.Json;")
        .AppendLine("using System.Text.Json.Serialization;")
        .AppendLine()
        .AppendLine("namespace Csls.DebugAdapter.Protocol;")
        .AppendLine()
        .AppendLine("/// <summary>")
        .Append("/// Represents the DAP ").Append(name).AppendLine(" contract.")
        .AppendLine("/// </summary>")
        .AppendLine("[GeneratedCode(\"Generate-DapProtocol\", \"1.0\")]")
        .Append("internal sealed class ").AppendLine(name)
        .AppendLine("{");
    foreach ((string jsonName, JsonElement propertySchema) in Properties)
    {
        string propertyName = ToIdentifier(jsonName);
        if (string.Equals(propertyName, name, StringComparison.Ordinal))
        {
            propertyName += "Value";
        }

        bool required = Required.Contains(jsonName);
        (string typeName, bool referenceType, bool nullableValueType) = GetTypeName(
            propertySchema,
            enumNames,
            name,
            jsonName);
        string declaredType = required || string.Equals(typeName, "JsonElement", StringComparison.Ordinal)
            ? typeName
            : referenceType || nullableValueType
                ? typeName + "?"
                : typeName;
        string initializer = required && referenceType ? " = null!;" : string.Empty;
        source.AppendLine("    /// <summary>")
            .Append("    /// Gets or initializes the DAP '").Append(jsonName).AppendLine("' value.")
            .AppendLine("    /// </summary>")
            .Append("    [JsonPropertyName(\"").Append(EscapeString(jsonName)).AppendLine("\")]")
            .Append("    public ").Append(declaredType).Append(' ').Append(propertyName)
            .Append(" { get; init; }").AppendLine(initializer)
            .AppendLine();
    }

    return source.AppendLine("}").ToString();
}

static string GenerateSerializerContext(IEnumerable<string> names, string schemaDigest)
{
    StringBuilder source = CreateHeader(schemaDigest);
    source.AppendLine("using System.Text.Json.Serialization;")
        .AppendLine()
        .AppendLine("namespace Csls.DebugAdapter.Protocol;")
        .AppendLine()
        .AppendLine("/// <summary>")
        .AppendLine("/// Provides NativeAOT JSON metadata for generated DAP contracts.")
        .AppendLine("/// </summary>");
    foreach (string name in names.Order(StringComparer.Ordinal))
    {
        source.Append("[JsonSerializable(typeof(Csls.DebugAdapter.Protocol.")
            .Append(name)
            .AppendLine("), TypeInfoPropertyName = \"" + name + "\")]");
    }

    source.AppendLine("internal sealed partial class DapProtocolJsonSerializerContext : JsonSerializerContext")
        .AppendLine("{")
        .AppendLine("}");
    return source.ToString();
}

static (SortedDictionary<string, JsonElement> Properties, HashSet<string> Required) CollectProperties(
    string name,
    IReadOnlyDictionary<string, JsonElement> definitions,
    HashSet<string> active)
{
    if (!active.Add(name))
    {
        throw new InvalidDataException($"DAP schema inheritance cycle detected at {name}.");
    }

    JsonElement definition = definitions[name];
    var properties = new SortedDictionary<string, JsonElement>(StringComparer.Ordinal);
    var required = new HashSet<string>(StringComparer.Ordinal);
    if (definition.TryGetProperty("allOf", out JsonElement allOf))
    {
        foreach (JsonElement part in allOf.EnumerateArray())
        {
            if (TryGetReferenceName(part, out string? baseName))
            {
                (SortedDictionary<string, JsonElement> inherited, HashSet<string> inheritedRequired) =
                    CollectProperties(baseName, definitions, active);
                foreach ((string propertyName, JsonElement propertySchema) in inherited)
                {
                    properties[propertyName] = propertySchema;
                }

                required.UnionWith(inheritedRequired);
            }

            AddObjectProperties(part, properties, required);
        }
    }

    AddObjectProperties(definition, properties, required);
    _ = active.Remove(name);
    return (properties, required);
}

static void AddObjectProperties(
    JsonElement schema,
    IDictionary<string, JsonElement> properties,
    ISet<string> required)
{
    if (schema.TryGetProperty("properties", out JsonElement propertyObject))
    {
        foreach (JsonProperty property in propertyObject.EnumerateObject())
        {
            properties[property.Name] = property.Value;
        }
    }

    if (schema.TryGetProperty("required", out JsonElement requiredArray))
    {
        foreach (JsonElement item in requiredArray.EnumerateArray())
        {
            string? requiredName = item.GetString();
            if (requiredName is not null)
            {
                _ = required.Add(requiredName);
            }
        }
    }
}

static (string TypeName, bool ReferenceType, bool NullableValueType) GetTypeName(
    JsonElement schema,
    IReadOnlySet<string> enumNames,
    string ownerName,
    string propertyName)
{
    if (TryGetReferenceName(schema, out string? referenceName))
    {
        return (referenceName, !enumNames.Contains(referenceName), enumNames.Contains(referenceName));
    }

    if (!schema.TryGetProperty("type", out JsonElement type) || type.ValueKind != JsonValueKind.String)
    {
        return ("JsonElement", false, false);
    }

    return type.GetString() switch
    {
        "boolean" => ("bool", false, true),
        "integer" => ("int", false, true),
        "number" => ("double", false, true),
        "string" => ("string", true, false),
        "array" => GetArrayType(schema, enumNames, ownerName, propertyName),
        "object" => GetObjectType(schema, enumNames, ownerName, propertyName),
        _ => ("JsonElement", false, false)
    };
}

static (string TypeName, bool ReferenceType, bool NullableValueType) GetArrayType(
    JsonElement schema,
    IReadOnlySet<string> enumNames,
    string ownerName,
    string propertyName)
{
    if (!schema.TryGetProperty("items", out JsonElement items))
    {
        return ("IReadOnlyList<JsonElement>", true, false);
    }

    (string itemType, _, _) = GetTypeName(
        items,
        enumNames,
        ownerName,
        propertyName + "Item");
    return ($"IReadOnlyList<{itemType}>", true, false);
}

static (string TypeName, bool ReferenceType, bool NullableValueType) GetObjectType(
    JsonElement schema,
    IReadOnlySet<string> enumNames,
    string ownerName,
    string propertyName)
{
    if (schema.TryGetProperty("properties", out _))
    {
        return (ownerName + ToIdentifier(propertyName), true, false);
    }

    if (!schema.TryGetProperty("additionalProperties", out JsonElement additionalProperties) ||
        additionalProperties.ValueKind is JsonValueKind.True or JsonValueKind.False)
    {
        return ("JsonElement", false, false);
    }

    (string valueType, _, _) = GetTypeName(
        additionalProperties,
        enumNames,
        ownerName,
        propertyName + "Value");
    return ($"IReadOnlyDictionary<string, {valueType}>", true, false);
}

static void DiscoverInlineTypes(IDictionary<string, JsonElement> types)
{
    var pending = new Queue<string>(types.Keys.Order(StringComparer.Ordinal));
    var visited = new HashSet<string>(StringComparer.Ordinal);
    while (pending.TryDequeue(out string? ownerName))
    {
        if (!visited.Add(ownerName) || IsEnum(types[ownerName]))
        {
            continue;
        }

        (SortedDictionary<string, JsonElement> properties, _) = CollectProperties(
            ownerName,
            (IReadOnlyDictionary<string, JsonElement>)types,
            []);
        foreach ((string propertyName, JsonElement propertySchema) in properties)
        {
            RegisterInlineType(ownerName, propertyName, propertySchema, types, pending);
        }
    }
}

static void RegisterInlineType(
    string ownerName,
    string propertyName,
    JsonElement schema,
    IDictionary<string, JsonElement> types,
    Queue<string> pending)
{
    if (!schema.TryGetProperty("type", out JsonElement type) ||
        type.ValueKind != JsonValueKind.String)
    {
        return;
    }

    if (string.Equals(type.GetString(), "array", StringComparison.Ordinal) &&
        schema.TryGetProperty("items", out JsonElement items))
    {
        RegisterInlineType(
            ownerName,
            propertyName + "Item",
            items,
            types,
            pending);
        return;
    }

    if (!string.Equals(type.GetString(), "object", StringComparison.Ordinal))
    {
        return;
    }

    if (schema.TryGetProperty("properties", out _))
    {
        string inlineName = ownerName + ToIdentifier(propertyName);
        if (!types.ContainsKey(inlineName))
        {
            types.Add(inlineName, schema);
            pending.Enqueue(inlineName);
        }
    }

    if (schema.TryGetProperty("additionalProperties", out JsonElement additionalProperties) &&
        additionalProperties.ValueKind == JsonValueKind.Object)
    {
        RegisterInlineType(
            ownerName,
            propertyName + "Value",
            additionalProperties,
            types,
            pending);
    }
}

static bool TryGetReferenceName(JsonElement schema, out string name)
{
    if (schema.TryGetProperty("$ref", out JsonElement reference))
    {
        const string Prefix = "#/definitions/";
        string value = reference.GetString()
            ?? throw new InvalidDataException("DAP schema contains a null definition reference.");
        if (!value.StartsWith(Prefix, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Unsupported DAP schema reference: {value}");
        }

        name = value[Prefix.Length..];
        return true;
    }

    name = string.Empty;
    return false;
}

static bool IsEnum(JsonElement definition) =>
    definition.TryGetProperty("type", out JsonElement type) &&
    type.ValueKind == JsonValueKind.String &&
    string.Equals(type.GetString(), "string", StringComparison.Ordinal) &&
    definition.TryGetProperty("enum", out JsonElement values) &&
    values.ValueKind == JsonValueKind.Array;

static StringBuilder CreateHeader(string schemaDigest) => new StringBuilder()
    .AppendLine("// <auto-generated />")
    .Append("// DAP schema SHA-256: ").AppendLine(schemaDigest)
    .AppendLine("#nullable enable")
    .AppendLine();

static string ToIdentifier(string value)
{
    var result = new StringBuilder(value.Length);
    bool capitalize = true;
    foreach (char character in value)
    {
        if (!char.IsLetterOrDigit(character))
        {
            capitalize = true;
            continue;
        }

        result.Append(capitalize ? char.ToUpperInvariant(character) : character);
        capitalize = false;
    }

    if (result.Length == 0)
    {
        return "Value";
    }

    if (char.IsDigit(result[0]))
    {
        result.Insert(0, "Value");
    }

    return result.ToString();
}

static string EscapeString(string value) => value
    .Replace("\\", "\\\\", StringComparison.Ordinal)
    .Replace("\"", "\\\"", StringComparison.Ordinal);

static string FindRepositoryRoot()
{
    DirectoryInfo? directory = new(Directory.GetCurrentDirectory());
    while (directory is not null)
    {
        if (File.Exists(Path.Join(directory.FullName, "Csls.slnx")))
        {
            return directory.FullName;
        }

        directory = directory.Parent;
    }

    throw new DirectoryNotFoundException("The csls repository root was not found.");
}
