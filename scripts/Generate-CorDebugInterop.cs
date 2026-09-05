#!/usr/bin/env -S dotnet --
#:property TargetFramework=net10.0
#:property LangVersion=14.0
#:property Nullable=enable
#:property TreatWarningsAsErrors=true

using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

const string Usage = "Usage: dotnet run --file scripts/Generate-CorDebugInterop.cs [--verify]";

if (args.Length == 1 && args[0] is "--help" or "-h" or "-?")
{
    await Console.Out.WriteLineAsync(
        "Generates deterministic NativeAOT ICorDebug ABI projections from the checked-in runtime IDL.")
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
string idlPath = Path.Join(repositoryRoot, "src", "Csls.Debugger", "Interop", "cordebug.idl");
byte[] idlBytes = await File.ReadAllBytesAsync(idlPath).ConfigureAwait(false);
string idlDigest = Convert.ToHexString(SHA256.HashData(idlBytes));
string idl = Encoding.UTF8.GetString(idlBytes);
string sourceWithoutComments = CreateRegex(
    @"//.*?$",
    RegexOptions.Multiline | RegexOptions.CultureInvariant).Replace(
    CreateRegex(
        @"/\*.*?\*/",
        RegexOptions.Singleline | RegexOptions.CultureInvariant).Replace(idl, string.Empty),
    string.Empty);

Dictionary<
    string,
    (string Guid, string BaseName, List<(string Name, List<(string Type, string Name)> Parameters)> Methods)> interfaces = new(
        StringComparer.Ordinal);
Regex declarationPattern = CreateRegex(
    @"\binterface\s+(?<name>ICorDebug[A-Za-z0-9_]*)\s*:\s*(?<base>[A-Za-z_][A-Za-z0-9_]*)\s*\{",
    RegexOptions.CultureInvariant);
foreach (Match declaration in declarationPattern.Matches(sourceWithoutComments))
{
    int openingBrace = declaration.Index + declaration.Length - 1;
    int closingBrace = FindMatchingBrace(sourceWithoutComments, openingBrace);
    string attributes = FindInterfaceAttributes(sourceWithoutComments, declaration.Index);
    Match guidMatch = CreateRegex(
        @"\buuid\s*\(\s*(?<guid>[0-9A-Fa-f-]{36})\s*\)",
        RegexOptions.CultureInvariant).Match(attributes);
    if (!guidMatch.Success)
    {
        throw new InvalidDataException(
            $"ICorDebug interface {declaration.Groups["name"].Value} has no UUID attribute.");
    }

    string body = sourceWithoutComments[(openingBrace + 1)..closingBrace];
    interfaces.Add(
        declaration.Groups["name"].Value,
        (
            guidMatch.Groups["guid"].Value.ToUpperInvariant(),
            declaration.Groups["base"].Value,
            ParseMethods(body, declaration.Groups["name"].Value)));
}

if (interfaces.Count < 100 ||
    !interfaces.ContainsKey("ICorDebug") ||
    !interfaces.ContainsKey("ICorDebugProcess") ||
    !interfaces.ContainsKey("ICorDebugManagedCallback"))
{
    throw new InvalidDataException(
        $"The runtime IDL yielded only {interfaces.Count} ICorDebug interfaces.");
}

Dictionary<string, int> slotCounts = new(StringComparer.Ordinal)
{
    ["IUnknown"] = 3
};
int GetSlotCount(string name, HashSet<string> active)
{
    if (slotCounts.TryGetValue(name, out int known))
    {
        return known;
    }

    if (!interfaces.TryGetValue(
        name,
        out (string Guid, string BaseName, List<(string Name, List<(string Type, string Name)> Parameters)> Methods) definition))
    {
        throw new InvalidDataException($"ICorDebug base interface {name} is not defined.");
    }

    if (!active.Add(name))
    {
        throw new InvalidDataException($"ICorDebug inheritance cycle detected at {name}.");
    }

    int count = GetSlotCount(definition.BaseName, active) + definition.Methods.Count;
    active.Remove(name);
    slotCounts[name] = count;
    return count;
}

SortedDictionary<string, string> outputs = new(StringComparer.Ordinal);
foreach (KeyValuePair<
    string,
    (string Guid, string BaseName, List<(string Name, List<(string Type, string Name)> Parameters)> Methods)>
    pair in interfaces.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
{
    string name = pair.Key;
    (string Guid, string BaseName, List<(string Name, List<(string Type, string Name)> Parameters)> Methods) definition = pair.Value;
    int firstSlot = GetSlotCount(definition.BaseName, []);
    outputs[$"{name}Abi.g.cs"] = GenerateInterface(
        name,
        definition.Guid,
        definition.BaseName,
        definition.Methods,
        firstSlot,
        GetSlotCount(name, []),
        idlDigest);
}

outputs["CorDebugTypeId.g.cs"] = GenerateCorDebugTypeId(idlDigest);
outputs["CorDebugAbiManifest.g.cs"] = GenerateManifest(interfaces.Count, idlDigest);

string outputDirectory = Path.Join(
    repositoryRoot,
    "src",
    "Csls.Debugger",
    "Interop",
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
            failures.Add(
                $"Generated ICorDebug projection is stale: " +
                Path.GetRelativePath(repositoryRoot, outputPath));
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
            $"Generated ICorDebug projection is no longer produced: " +
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
    $"{(verify ? "Verified" : "Generated")} {interfaces.Count} ICorDebug interfaces " +
    $"in {outputs.Count} files from IDL SHA-256 {idlDigest}.").ConfigureAwait(false);
return 0;

static List<(string Name, List<(string Type, string Name)> Parameters)> ParseMethods(
    string body,
    string interfaceName)
{
    var methods = new List<(string Name, List<(string Type, string Name)> Parameters)>();
    var statement = new StringBuilder();
    int nestedDepth = 0;
    foreach (char character in body)
    {
        if (character == '{')
        {
            nestedDepth++;
            statement.Clear();
            continue;
        }

        if (character == '}')
        {
            nestedDepth--;
            if (nestedDepth < 0)
            {
                throw new InvalidDataException($"Unbalanced declaration in {interfaceName}.");
            }

            statement.Clear();
            continue;
        }

        if (nestedDepth != 0)
        {
            continue;
        }

        if (character != ';')
        {
            statement.Append(character);
            continue;
        }

        string candidate = statement.ToString().Trim();
        statement.Clear();
        Match method = CreateRegex(
            @"\bHRESULT\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*\((?<parameters>.*)\)\s*$",
            RegexOptions.Singleline | RegexOptions.CultureInvariant).Match(candidate);
        if (!method.Success)
        {
            continue;
        }

        methods.Add((
            method.Groups["name"].Value,
            ParseParameters(method.Groups["parameters"].Value, interfaceName, method.Groups["name"].Value)));
    }

    return methods;
}

static List<(string Type, string Name)> ParseParameters(
    string source,
    string interfaceName,
    string methodName)
{
    string withoutAnnotations = CreateRegex(
        @"\[[^\]]+\]",
        RegexOptions.CultureInvariant).Replace(source, string.Empty);
    if (string.IsNullOrWhiteSpace(withoutAnnotations) ||
        string.Equals(withoutAnnotations.Trim(), "void", StringComparison.Ordinal))
    {
        return [];
    }

    List<(string Type, string Name)> parameters = [];
    foreach (string rawParameter in withoutAnnotations.Split(','))
    {
        string parameter = CreateRegex(
            @"\s+",
            RegexOptions.CultureInvariant).Replace(rawParameter, " ").Trim();
        Match nameMatch = CreateRegex(
            @"(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*(?<array>\[\s*\])?\s*$",
            RegexOptions.CultureInvariant).Match(parameter);
        if (!nameMatch.Success)
        {
            throw new InvalidDataException(
                $"Cannot parse {interfaceName}.{methodName} parameter '{parameter}'.");
        }

        string nativeType = parameter[..nameMatch.Index].Trim();
        bool isPointer = nativeType.Contains('*', StringComparison.Ordinal) ||
            nameMatch.Groups["array"].Success;
        nativeType = nativeType.Replace("*", string.Empty, StringComparison.Ordinal).Trim();
        parameters.Add((
            GetManagedType(nativeType, isPointer, interfaceName, methodName),
            EscapeIdentifier(nameMatch.Groups["name"].Value)));
    }

    return parameters;
}

static string GetManagedType(
    string nativeType,
    bool isPointer,
    string interfaceName,
    string methodName)
{
    if (isPointer ||
        nativeType.StartsWith("ICorDebug", StringComparison.Ordinal) ||
        nativeType is "IUnknown" or "IStream" or "HPROCESS" or "HTHREAD" or
            "HANDLE" or "PVOID" or "LPVOID" or "LPCVOID" or "LPCWSTR" or "LPWSTR" or
            "REFIID" or
            "LPSECURITY_ATTRIBUTES" or "LPSTARTUPINFOW" or "LPPROCESS_INFORMATION" or
            "LPDEBUG_EVENT" or "PCCOR_SIGNATURE")
    {
        return "nint";
    }

    if (nativeType.StartsWith("const ", StringComparison.Ordinal))
    {
        nativeType = nativeType[6..].Trim();
    }

    if (nativeType.StartsWith("md", StringComparison.Ordinal) ||
        nativeType is "BOOL" or "LONG" or "HRESULT" or "int" or "INT" or
            "ULONG" or "ULONG32" or "DWORD" or "UINT" or "UINT32" or
            "CONNID" or "CorElementType")
    {
        return nativeType is "BOOL" or "LONG" or "HRESULT" or "int" or "INT"
            ? "int"
            : "uint";
    }

    if (nativeType is "BYTE" or "UCHAR" or "CHAR")
    {
        return "byte";
    }

    if (nativeType is "SHORT")
    {
        return "short";
    }

    if (nativeType is "WCHAR" or "USHORT")
    {
        return "ushort";
    }

    if (nativeType is "CORDB_ADDRESS" or "TASKID" or "UINT64" or "ULONG64" or "ULONGLONG")
    {
        return "ulong";
    }

    if (nativeType is "SIZE_T" or "UINT_PTR" or "ULONG_PTR")
    {
        return "nuint";
    }

    if (nativeType == "COR_TYPEID")
    {
        return "CorDebugTypeId";
    }

    if (nativeType.StartsWith("Cor", StringComparison.Ordinal) ||
        nativeType.StartsWith("CORDB_", StringComparison.Ordinal) ||
        nativeType is "WriteableMetadataUpdateMode" or "ILCodeKind")
    {
        return "int";
    }

    throw new InvalidDataException(
        $"Unsupported by-value IDL type '{nativeType}' in {interfaceName}.{methodName}.");
}

static string GenerateInterface(
    string nativeName,
    string guid,
    string baseName,
    IReadOnlyList<(string Name, List<(string Type, string Name)> Parameters)> methods,
    int firstSlot,
    int slotCount,
    string idlDigest)
{
    string typeName = nativeName + "Abi";
    StringBuilder source = CreateHeader(idlDigest);
    source.AppendLine("using System.CodeDom.Compiler;")
        .AppendLine("using System.Runtime.CompilerServices;")
        .AppendLine()
        .AppendLine("namespace Csls.Debugger.Interop;")
        .AppendLine()
        .AppendLine("/// <summary>")
        .Append("/// Projects the native ").Append(nativeName).AppendLine(" COM interface ABI.")
        .AppendLine("/// </summary>")
        .AppendLine("[GeneratedCode(\"Generate-CorDebugInterop\", \"1.0\")]")
        .Append("internal readonly unsafe struct ").AppendLine(typeName)
        .AppendLine("{")
        .AppendLine("    private readonly nint _instance;")
        .AppendLine()
        .AppendLine("    /// <summary>")
        .Append("    /// Creates a projection over a non-null ").Append(nativeName).AppendLine(" pointer.")
        .AppendLine("    /// </summary>")
        .AppendLine("    /// <param name=\"instance\">The native COM interface pointer.</param>")
        .Append("    internal ").Append(typeName).AppendLine("(nint instance)")
        .AppendLine("    {")
        .AppendLine("        ArgumentOutOfRangeException.ThrowIfZero(instance);")
        .AppendLine("        _instance = instance;")
        .AppendLine("    }")
        .AppendLine()
        .AppendLine("    /// <summary>")
        .Append("    /// Gets the ").Append(nativeName).AppendLine(" interface identifier.")
        .AppendLine("    /// </summary>")
        .Append("    internal static Guid InterfaceId => new(\"").Append(guid).AppendLine("\");")
        .AppendLine()
        .AppendLine("    /// <summary>")
        .AppendLine("    /// Gets the inherited interface named by the runtime IDL.")
        .AppendLine("    /// </summary>")
        .Append("    internal static string BaseInterfaceName => \"").Append(baseName).AppendLine("\";")
        .AppendLine()
        .AppendLine("    /// <summary>")
        .AppendLine("    /// Gets the total number of entries in this interface vtable.")
        .AppendLine("    /// </summary>")
        .Append("    internal static int VtableSlotCount => ").Append(slotCount).AppendLine(";")
        .AppendLine()
        .AppendLine("    /// <summary>")
        .AppendLine("    /// Gets the underlying native COM interface pointer.")
        .AppendLine("    /// </summary>")
        .AppendLine("    internal nint Instance => _instance;");

    for (int index = 0; index < methods.Count; index++)
    {
        (string methodName, List<(string Type, string Name)> parameters) = methods[index];
        int slot = firstSlot + index;
        source.AppendLine()
            .AppendLine("    /// <summary>")
            .Append("    /// Invokes the native ").Append(nativeName).Append('.').Append(methodName)
            .Append(" method at vtable slot ").Append(slot).AppendLine(".")
            .AppendLine("    /// </summary>");
        foreach ((string _, string parameterName) in parameters)
        {
            source.Append("    /// <param name=\"").Append(parameterName.TrimStart('@'))
                .AppendLine("\">The ABI-level argument value.</param>");
        }

        source.AppendLine("    /// <returns>The HRESULT returned by the runtime.</returns>")
            .Append("    internal int ").Append(methodName).Append('(')
            .Append(string.Join(", ", parameters.Select(static parameter =>
                $"{parameter.Type} {parameter.Name}")))
            .AppendLine(")")
            .AppendLine("    {")
            .AppendLine("        nint* vtable = *(nint**)_instance;")
            .Append("        var operation = (delegate* unmanaged[Stdcall]<nint");
        foreach ((string parameterType, string _) in parameters)
        {
            source.Append(", ").Append(parameterType);
        }

        source.Append(", int>)vtable[").Append(slot).AppendLine("];")
            .Append("        return operation(_instance");
        foreach ((string _, string parameterName) in parameters)
        {
            source.Append(", ").Append(parameterName);
        }

        source.AppendLine(");")
            .AppendLine("    }");
    }

    return source.AppendLine("}").ToString();
}

static string GenerateCorDebugTypeId(string idlDigest)
{
    StringBuilder source = CreateHeader(idlDigest);
    return source.AppendLine("using System.CodeDom.Compiler;")
        .AppendLine("using System.Runtime.InteropServices;")
        .AppendLine()
        .AppendLine("namespace Csls.Debugger.Interop;")
        .AppendLine()
        .AppendLine("/// <summary>")
        .AppendLine("/// Represents the opaque two-token COR_TYPEID runtime value.")
        .AppendLine("/// </summary>")
        .AppendLine("[GeneratedCode(\"Generate-CorDebugInterop\", \"1.0\")]")
        .AppendLine("[StructLayout(LayoutKind.Sequential)]")
        .AppendLine("internal readonly struct CorDebugTypeId")
        .AppendLine("{")
        .AppendLine("    /// <summary>")
        .AppendLine("    /// Gets the first opaque runtime type token.")
        .AppendLine("    /// </summary>")
        .AppendLine("    internal ulong Token1 { get; init; }")
        .AppendLine()
        .AppendLine("    /// <summary>")
        .AppendLine("    /// Gets the second opaque runtime type token.")
        .AppendLine("    /// </summary>")
        .AppendLine("    internal ulong Token2 { get; init; }")
        .AppendLine("}")
        .ToString();
}

static string GenerateManifest(int interfaceCount, string idlDigest)
{
    StringBuilder source = CreateHeader(idlDigest);
    return source.AppendLine("using System.CodeDom.Compiler;")
        .AppendLine()
        .AppendLine("namespace Csls.Debugger.Interop;")
        .AppendLine()
        .AppendLine("/// <summary>")
        .AppendLine("/// Identifies the public runtime IDL used for generated ICorDebug ABI projections.")
        .AppendLine("/// </summary>")
        .AppendLine("[GeneratedCode(\"Generate-CorDebugInterop\", \"1.0\")]")
        .AppendLine("internal static class CorDebugAbiManifest")
        .AppendLine("{")
        .AppendLine("    /// <summary>")
        .AppendLine("    /// Gets the SHA-256 digest of the checked-in runtime IDL.")
        .AppendLine("    /// </summary>")
        .Append("    internal static string IdlSha256 => \"").Append(idlDigest).AppendLine("\";")
        .AppendLine()
        .AppendLine("    /// <summary>")
        .AppendLine("    /// Gets the number of generated ICorDebug interfaces.")
        .AppendLine("    /// </summary>")
        .Append("    internal static int InterfaceCount => ").Append(interfaceCount).AppendLine(";")
        .AppendLine("}")
        .ToString();
}

static StringBuilder CreateHeader(string idlDigest)
{
    return new StringBuilder()
        .AppendLine("// <auto-generated />")
        .Append("// Source: cordebug.idl SHA-256 ").AppendLine(idlDigest)
        .AppendLine("// Generated by scripts/Generate-CorDebugInterop.cs.")
        .AppendLine();
}

static int FindMatchingBrace(string source, int openingBrace)
{
    int depth = 0;
    for (int index = openingBrace; index < source.Length; index++)
    {
        if (source[index] == '{')
        {
            depth++;
        }
        else if (source[index] == '}' && --depth == 0)
        {
            return index;
        }
    }

    throw new InvalidDataException("The runtime IDL contains an unterminated interface body.");
}

static string FindInterfaceAttributes(string source, int interfaceIndex)
{
    int closingBracket = source.LastIndexOf(']', interfaceIndex);
    int openingBracket = source.LastIndexOf('[', closingBracket);
    if (openingBracket < 0 || closingBracket < openingBracket)
    {
        return string.Empty;
    }

    return source[openingBracket..(closingBracket + 1)];
}

static string EscapeIdentifier(string value)
{
    return value is "base" or "checked" or "event" or "fixed" or "in" or "internal" or
        "object" or "out" or "params" or "ref" or "string" or "this" or "unchecked"
        ? "@" + value
        : value;
}

static Regex CreateRegex(string pattern, RegexOptions options)
{
    return new Regex(pattern, options);
}

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
