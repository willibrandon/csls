#!/usr/bin/env -S dotnet --
#:property TargetFramework=net10.0
#:property LangVersion=14.0
#:property Nullable=enable
#:property TreatWarningsAsErrors=true
#:package Microsoft.CodeAnalysis.CSharp
#:package ModelContextProtocol
#:package SharpCompress
#:project ../src/Csls.Control.Contracts/Csls.Control.Contracts.csproj
#:project ../src/Csls.Protocol/Csls.Protocol.csproj
#:include ScriptSupport.cs

using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using ModelContextProtocol.Client;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

const string Usage = "Usage: dotnet run --file scripts/Generate-Docs.cs [--verify]";

if (args.Length == 1 && args[0] is "--help" or "-h" or "-?")
{
    await Console.Out.WriteLineAsync(
        "Generates reference documentation from the built CLI, MCP server, and XML contracts.")
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

try
{
    using var timeoutSource = new CancellationTokenSource(TimeSpan.FromMinutes(5));
    CancellationToken cancellationToken = timeoutSource.Token;
    string repositoryRoot = ScriptSupport.FindRepositoryRoot();
    await BuildDocumentationInputsAsync(repositoryRoot, cancellationToken).ConfigureAwait(false);

    string cliReference = await GenerateCliReferenceAsync(
        repositoryRoot,
        cancellationToken).ConfigureAwait(false);
    string mcpReference = await GenerateMcpReferenceAsync(
        repositoryRoot,
        cancellationToken).ConfigureAwait(false);
    string configurationReference = GenerateConfigurationReference(repositoryRoot);
    string contractReference = GenerateContractReference(repositoryRoot);
    (string Path, string Content)[] outputs =
    [
        ("docs-site/src/content/docs/cli-reference.md", cliReference),
        ("docs-site/src/content/docs/mcp-reference.md", mcpReference),
        ("docs-site/src/content/docs/configuration-reference.md", configurationReference),
        ("docs-site/src/content/docs/contract-reference.md", contractReference)
    ];

    foreach ((string relativePath, string content) in outputs)
    {
        await WriteOrVerifyAsync(
            repositoryRoot,
            relativePath,
            content,
            verify,
            cancellationToken).ConfigureAwait(false);
    }

    await Console.Out.WriteLineAsync(
        $"{(verify ? "Verified" : "Generated")} {outputs.Length} documentation references.")
        .ConfigureAwait(false);
    return 0;
}
catch (Exception exception) when (exception is
    IOException or
    InvalidDataException or
    InvalidOperationException or
    UnauthorizedAccessException or
    OperationCanceledException)
{
    await Console.Error.WriteLineAsync(exception.Message).ConfigureAwait(false);
    return 1;
}

static async Task BuildDocumentationInputsAsync(
    string repositoryRoot,
    CancellationToken cancellationToken)
{
    _ = await RunProcessAsync(
        ResolveDotNetHost(),
        ["build", "Csls.slnx", "--configuration", "Debug", "--nologo"],
        repositoryRoot,
        cancellationToken).ConfigureAwait(false);
}

static async Task<string> GenerateCliReferenceAsync(
    string repositoryRoot,
    CancellationToken cancellationToken)
{
    string cliAssembly = Path.Join(
        repositoryRoot,
        "artifacts",
        "bin",
        "Csls.App",
        "debug",
        "csls.dll");
    RequireFile(cliAssembly);
    var pending = new Queue<string[]>();
    pending.Enqueue([]);
    var visited = new HashSet<string>(StringComparer.Ordinal);
    var sections = new List<(string Name, string Help)>();
    while (pending.TryDequeue(out string[]? commandPath))
    {
        string key = string.Join(' ', commandPath);
        if (!visited.Add(key))
        {
            continue;
        }

        string[] processArguments =
        [
            cliAssembly,
            .. commandPath,
            "--help"
        ];
        string help = await RunProcessAsync(
            ResolveDotNetHost(),
            processArguments,
            repositoryRoot,
            cancellationToken).ConfigureAwait(false);
        help = NormalizeHelp(help, repositoryRoot);
        string commandName = commandPath.Length == 0
            ? "csls"
            : $"csls {string.Join(' ', commandPath)}";
        sections.Add((commandName, help));
        foreach (string child in EnumerateHelpCommands(help))
        {
            pending.Enqueue([.. commandPath, child]);
        }
    }

    var page = new StringBuilder(
        "---\ntitle: CLI reference\ndescription: Generated System.CommandLine help for every csls command.\n---\n\n" +
        "This page is generated from the command tree compiled into `csls`.\n\n");
    foreach ((string name, string help) in sections)
    {
        page.Append("## ").AppendLine(name).AppendLine();
        page.AppendLine("```text").Append(help.TrimEnd()).AppendLine().AppendLine("```")
            .AppendLine();
    }

    return EnsureFinalNewLine(page.ToString());
}

static async Task<string> GenerateMcpReferenceAsync(
    string repositoryRoot,
    CancellationToken cancellationToken)
{
    string mcpWorkerPath = Path.Join(
        repositoryRoot,
        "artifacts",
        "bin",
        "Csls.Mcp.Worker",
        "debug",
        "csls-mcp-worker.dll");
    RequireFile(mcpWorkerPath);
    Dictionary<string, string?> environment =
        StdioClientTransportOptions.GetDefaultEnvironmentVariables();
    environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
    environment["DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE"] = "true";
    environment["DOTNET_NOLOGO"] = "1";
    var transport = new StdioClientTransport(
        new StdioClientTransportOptions
        {
            Command = ResolveDotNetHost(),
            Arguments = [mcpWorkerPath],
            Name = "csls-docs",
            WorkingDirectory = repositoryRoot,
            InheritEnvironmentVariables = true,
            EnvironmentVariables = environment,
            StandardErrorLines = Console.Error.WriteLine
        });
    McpClient client = await McpClient.CreateAsync(
        transport,
        cancellationToken: cancellationToken).ConfigureAwait(false);
    try
    {
        IList<McpClientTool> tools = await client.ListToolsAsync(
            cancellationToken: cancellationToken).ConfigureAwait(false);
        IList<McpClientResource> resources = await client.ListResourcesAsync(
            cancellationToken: cancellationToken).ConfigureAwait(false);
        IList<McpClientResourceTemplate> templates =
            await client.ListResourceTemplatesAsync(
                cancellationToken: cancellationToken).ConfigureAwait(false);
        IList<McpClientPrompt> prompts = await client.ListPromptsAsync(
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return BuildMcpReference(tools, resources, templates, prompts);
    }
    finally
    {
        await client.DisposeAsync().ConfigureAwait(false);
    }
}

static string BuildMcpReference(
    IList<McpClientTool> tools,
    IList<McpClientResource> resources,
    IList<McpClientResourceTemplate> templates,
    IList<McpClientPrompt> prompts)
{
    var page = new StringBuilder(
        "---\ntitle: MCP reference\ndescription: Generated multi-workspace tools, resource templates, and prompts from csls-mcp.\n---\n\n" +
        "This page is generated through the official MCP client from the bare csls MCP server. " +
        "Start `csls-mcp` without arguments. Every target-dependent tool and resource requires " +
        "exactly one of `workspace`, `session`, or `socket`. Target selectors are shown " +
        "separately from operation-specific inputs.\n\n" +
        "## Tools\n\n" +
        "| Tool | Behavior | Target | Operation inputs | Description |\n" +
        "| --- | --- | --- | --- | --- |\n");
    foreach (McpClientTool tool in tools.OrderBy(static tool => tool.Name, StringComparer.Ordinal))
    {
        page.Append("| `").Append(tool.Name).Append("` | ")
            .Append(GetToolBehavior(tool)).Append(" | ")
            .Append(GetToolTarget(tool.JsonSchema)).Append(" | ")
            .Append(GetToolInputs(tool.JsonSchema, excludeTargetSelectors: true)).Append(" | ")
            .Append(EscapeTableText(tool.Description)).AppendLine(" |");
    }

    page.AppendLine().AppendLine("## Resources").AppendLine();
    if (resources.Count == 0)
    {
        page.AppendLine(
            "csls exposes target-selected state only through the resource templates below.");
    }
    else
    {
        page.AppendLine("| URI | Name | Description |")
            .AppendLine("| --- | --- | --- |");
        foreach (McpClientResource resource in resources.OrderBy(
            static resource => resource.Uri,
            StringComparer.Ordinal))
        {
            page.Append("| `").Append(resource.Uri).Append("` | ")
                .Append(EscapeTableText(resource.Name)).Append(" | ")
                .Append(EscapeTableText(resource.Description)).AppendLine(" |");
        }
    }

    page.AppendLine().AppendLine("## Resource templates").AppendLine()
        .AppendLine("| URI template | Name | Description |")
        .AppendLine("| --- | --- | --- |");
    foreach (McpClientResourceTemplate template in templates.OrderBy(
        static template => template.UriTemplate,
        StringComparer.Ordinal))
    {
        page.Append("| `").Append(template.UriTemplate).Append("` | ")
            .Append(EscapeTableText(template.Name)).Append(" | ")
            .Append(EscapeTableText(template.Description)).AppendLine(" |");
    }

    page.AppendLine().AppendLine("## Prompts").AppendLine()
        .AppendLine("| Prompt | Description |")
        .AppendLine("| --- | --- |");
    foreach (McpClientPrompt prompt in prompts.OrderBy(
        static prompt => prompt.Name,
        StringComparer.Ordinal))
    {
        page.Append("| `").Append(prompt.Name).Append("` | ")
            .Append(EscapeTableText(prompt.Description)).AppendLine(" |");
    }

    return EnsureFinalNewLine(page.ToString());
}

static string GenerateConfigurationReference(string repositoryRoot)
{
    string sourcePath = Path.Join(
        repositoryRoot,
        "src",
        "Csls.Server",
        "LanguageServerConfiguration.cs");
    string xmlPath = Path.Join(
        repositoryRoot,
        "artifacts",
        "bin",
        "Csls.Server",
        "debug",
        "Csls.Server.xml");
    Dictionary<string, string> summaries = ReadXmlSummaries(xmlPath);
    RequireFile(sourcePath);
    CompilationUnitSyntax root = CSharpSyntaxTree.ParseText(File.ReadAllText(sourcePath))
        .GetCompilationUnitRoot();
    RecordDeclarationSyntax configuration = root.DescendantNodes()
        .OfType<RecordDeclarationSyntax>()
        .Single(static declaration =>
            declaration.Identifier.ValueText == "LanguageServerConfiguration");
    (string Key, string PropertyName)[] settings =
    [
        ("enableAnalyzers", "EnableAnalyzers"),
        ("formatOnSave", "FormatOnSave"),
        ("inlayHints.enableInlayHintsForParameters", "EnableInlayHintsForParameters"),
        ("inlayHints.enableInlayHintsForTypes", "EnableInlayHintsForTypes"),
        ("diagnostics.reportInformationAsHint", "ReportInformationAsHint"),
        ("configuration", "BuildConfiguration"),
        ("logLevel", "LogLevel")
    ];
    var page = new StringBuilder(
        "---\ntitle: Configuration reference\ndescription: Generated csls settings and defaults from the server contract.\n---\n\n" +
        "The `csls` section takes precedence over the compatible `csharp` section.\n\n" +
        "| Setting | Type | Default | Description |\n" +
        "| --- | --- | --- | --- |\n");
    foreach ((string key, string propertyName) in settings)
    {
        PropertyDeclarationSyntax property = configuration.Members
            .OfType<PropertyDeclarationSyntax>()
            .Single(property => property.Identifier.ValueText == propertyName);
        string memberName = $"P:Csls.Server.LanguageServerConfiguration.{propertyName}";
        if (property.Initializer is null &&
            !string.Equals(property.Type.ToString(), "bool", StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Configuration property {propertyName} has no documented default initializer.");
        }

        string defaultValue = property.Initializer is null
            ? "false"
            : FormatConfigurationDefault(property.Initializer.Value);
        if (defaultValue.Length == 0)
        {
            throw new InvalidDataException(
                $"Configuration property {propertyName} has an unsupported default initializer.");
        }

        if (!summaries.TryGetValue(memberName, out string? summary))
        {
            throw new InvalidDataException(
                $"Configuration property {propertyName} has no XML summary.");
        }

        page.Append("| `").Append(key).Append("` | `")
            .Append(GetConfigurationType(property.Type)).Append("` | `")
            .Append(defaultValue).Append("` | ")
            .Append(EscapeTableText(summary)).AppendLine(" |");
    }

    return EnsureFinalNewLine(page.ToString());
}

static string GenerateContractReference(string repositoryRoot)
{
    (string AssemblyName, string AssemblyPath, string XmlPath)[] contracts =
    [
        (
            "Csls.Protocol",
            Path.Join(
                repositoryRoot,
                "artifacts",
                "bin",
                "Csls.Protocol",
                "debug",
                "Csls.Protocol.dll"),
            Path.Join(
                repositoryRoot,
                "artifacts",
                "bin",
                "Csls.Protocol",
                "debug",
                "Csls.Protocol.xml")),
        (
            "Csls.Control.Contracts",
            Path.Join(
                repositoryRoot,
                "artifacts",
                "bin",
                "Csls.Control.Contracts",
                "debug",
                "Csls.Control.Contracts.dll"),
            Path.Join(
                repositoryRoot,
                "artifacts",
                "bin",
                "Csls.Control.Contracts",
                "debug",
                "Csls.Control.Contracts.xml"))
    ];
    var page = new StringBuilder(
        "---\ntitle: Contract reference\ndescription: Generated public LSP and control contract type index.\n---\n\n" +
        "These public wire-contract types are generated from the compiled assemblies and their XML documentation.\n");
    foreach ((string assemblyName, string assemblyPath, string xmlPath) in contracts)
    {
        Dictionary<string, string> summaries = ReadXmlSummaries(xmlPath);
        page.AppendLine().Append("## ").AppendLine(assemblyName).AppendLine()
            .AppendLine("| Type | Description |")
            .AppendLine("| --- | --- |");
        foreach ((string xmlName, string displayName) in EnumeratePublicTypes(assemblyPath))
        {
            string memberName = $"T:{xmlName}";
            if (!summaries.TryGetValue(memberName, out string? summary))
            {
                throw new InvalidDataException(
                    $"Public type {xmlName} has no XML summary.");
            }

            page.Append("| `").Append(displayName).Append("` | ")
                .Append(EscapeTableText(summary)).AppendLine(" |");
        }
    }

    return EnsureFinalNewLine(page.ToString());
}

static Dictionary<string, string> ReadXmlSummaries(string xmlPath)
{
    RequireFile(xmlPath);
    var document = XDocument.Load(xmlPath, LoadOptions.None);
    var summaries = new Dictionary<string, string>(StringComparer.Ordinal);
    foreach (XElement member in document.Descendants("member"))
    {
        string? name = member.Attribute("name")?.Value;
        XElement? summary = member.Element("summary");
        if (name is null || summary is null)
        {
            continue;
        }

        foreach (XElement reference in summary.Descendants().ToArray())
        {
            string replacement = reference.Name.LocalName switch
            {
                "see" => FormatXmlReference(
                    reference.Attribute("cref")?.Value,
                    reference.Attribute("langword")?.Value),
                "paramref" => reference.Attribute("name")?.Value ?? string.Empty,
                "typeparamref" => reference.Attribute("name")?.Value ?? string.Empty,
                _ => reference.Value
            };
            reference.ReplaceWith(replacement);
        }

        summaries[name] = NormalizeWhitespace(summary.Value);
    }

    return summaries;
}

static List<(string XmlName, string DisplayName)> EnumeratePublicTypes(
    string assemblyPath)
{
    RequireFile(assemblyPath);
    using FileStream stream = File.OpenRead(assemblyPath);
    using var peReader = new PEReader(stream);
    MetadataReader reader = peReader.GetMetadataReader();
    var types = new List<(string XmlName, string DisplayName)>();
    foreach (TypeDefinitionHandle handle in reader.TypeDefinitions)
    {
        TypeDefinition definition = reader.GetTypeDefinition(handle);
        TypeAttributes visibility = definition.Attributes & TypeAttributes.VisibilityMask;
        if (visibility is not TypeAttributes.Public and not TypeAttributes.NestedPublic)
        {
            continue;
        }

        types.Add(GetMetadataTypeName(reader, handle));
    }

    types.Sort(static (left, right) =>
        StringComparer.Ordinal.Compare(left.XmlName, right.XmlName));
    return types;
}

static (string XmlName, string DisplayName) GetMetadataTypeName(
    MetadataReader reader,
    TypeDefinitionHandle handle)
{
    TypeDefinition definition = reader.GetTypeDefinition(handle);
    string metadataName = reader.GetString(definition.Name);
    int aritySeparator = metadataName.IndexOf('`', StringComparison.Ordinal);
    string simpleName = aritySeparator < 0
        ? metadataName
        : metadataName[..aritySeparator];
    string[] genericParameters =
    [
        .. definition.GetGenericParameters()
            .Select(parameter => reader.GetString(
                reader.GetGenericParameter(parameter).Name))
    ];
    string displayName = genericParameters.Length == 0
        ? simpleName
        : $"{simpleName}<{string.Join(", ", genericParameters)}>";
    TypeDefinitionHandle declaringHandle = definition.GetDeclaringType();
    if (!declaringHandle.IsNil)
    {
        (string parentXmlName, string parentDisplayName) =
            GetMetadataTypeName(reader, declaringHandle);
        return (
            $"{parentXmlName}.{metadataName}",
            $"{parentDisplayName}.{displayName}");
    }

    string typeNamespace = reader.GetString(definition.Namespace);
    return string.IsNullOrEmpty(typeNamespace)
        ? (metadataName, displayName)
        : ($"{typeNamespace}.{metadataName}", $"{typeNamespace}.{displayName}");
}

static IEnumerable<string> EnumerateHelpCommands(string help)
{
    string[] lines = help.Split('\n');
    int commandsIndex = Array.FindIndex(
        lines,
        static line => string.Equals(line.Trim(), "Commands:", StringComparison.Ordinal));
    if (commandsIndex < 0)
    {
        yield break;
    }

    for (int index = commandsIndex + 1; index < lines.Length; index++)
    {
        string line = lines[index];
        if (string.IsNullOrWhiteSpace(line))
        {
            continue;
        }

        if (!char.IsWhiteSpace(line[0]))
        {
            yield break;
        }

        string trimmed = line.Trim();
        int separator = trimmed.IndexOfAny([' ', '\t', '<', '[']);
        string command = separator < 0 ? trimmed : trimmed[..separator];
        if (command.Length != 0)
        {
            yield return command;
        }
    }
}

static string GetToolBehavior(McpClientTool tool)
{
    bool? readOnly = tool.ProtocolTool.Annotations?.ReadOnlyHint;
    bool? destructive = tool.ProtocolTool.Annotations?.DestructiveHint;
    return (readOnly, destructive) switch
    {
        (true, _) => "Read only",
        (_, true) => "Destructive",
        (false, _) => "Mutating",
        _ => "Unspecified"
    };
}

static string GetToolTarget(JsonElement schema)
{
    if (!schema.TryGetProperty("properties", out JsonElement properties) ||
        properties.ValueKind != JsonValueKind.Object)
    {
        return "None";
    }

    string[] selectors = ["workspace", "session", "socket"];
    int selectorCount = selectors.Count(selector => properties.TryGetProperty(selector, out _));
    if (selectorCount == 0)
    {
        return "None";
    }

    if (selectorCount != selectors.Length)
    {
        throw new InvalidDataException(
            "A target-dependent MCP tool does not expose all three target selectors.");
    }

    if (schema.TryGetProperty("required", out JsonElement requiredProperties) &&
        requiredProperties.ValueKind == JsonValueKind.Array &&
        requiredProperties.EnumerateArray().Any(property =>
            selectors.Contains(property.GetString(), StringComparer.Ordinal)))
    {
        throw new InvalidDataException(
            "MCP target selectors must remain optional in the schema and be validated " +
            "as exactly one at runtime.");
    }

    return "Exactly one of `workspace`, `session`, or `socket`";
}

static string GetToolInputs(JsonElement schema, bool excludeTargetSelectors)
{
    if (!schema.TryGetProperty("properties", out JsonElement properties) ||
        properties.ValueKind != JsonValueKind.Object)
    {
        return "None";
    }

    var required = new HashSet<string>(StringComparer.Ordinal);
    if (schema.TryGetProperty("required", out JsonElement requiredProperties) &&
        requiredProperties.ValueKind == JsonValueKind.Array)
    {
        required.UnionWith(requiredProperties
            .EnumerateArray()
            .Select(static property => property.GetString())
            .OfType<string>());
    }

    var inputs = new List<string>();
    foreach (JsonProperty property in properties.EnumerateObject())
    {
        if (excludeTargetSelectors && IsTargetSelector(property.Name))
        {
            continue;
        }

        inputs.Add(required.Contains(property.Name)
            ? $"`{property.Name}` required"
            : $"`{property.Name}`");
    }

    return inputs.Count == 0 ? "None" : string.Join(", ", inputs);
}

static bool IsTargetSelector(string name) =>
    name is "workspace" or "session" or "socket";

static string NormalizeHelp(string help, string repositoryRoot)
{
    string normalized = help.Replace("\r\n", "\n", StringComparison.Ordinal)
        .Replace('\r', '\n');
    normalized = normalized.Replace(repositoryRoot, ".", StringComparison.Ordinal);
    return EnsureFinalNewLine(normalized);
}

static string GetConfigurationType(TypeSyntax type) => type.ToString() switch
{
    "bool" => "boolean",
    "string" => "string",
    "LogLevel" => "logging level",
    string value => value
};

static string FormatConfigurationDefault(ExpressionSyntax expression) => expression switch
{
    LiteralExpressionSyntax literal => literal.Token.ValueText,
    MemberAccessExpressionSyntax memberAccess => memberAccess.Name.Identifier.ValueText,
    _ => expression.ToString()
};

static string FormatXmlReference(string? cref, string? langword)
{
    if (!string.IsNullOrWhiteSpace(langword))
    {
        return langword;
    }

    if (string.IsNullOrWhiteSpace(cref))
    {
        return string.Empty;
    }

    int prefixSeparator = cref.IndexOf(':', StringComparison.Ordinal);
    string value = prefixSeparator < 0 ? cref : cref[(prefixSeparator + 1)..];
    int parameterList = value.IndexOf('(', StringComparison.Ordinal);
    if (parameterList >= 0)
    {
        value = value[..parameterList];
    }

    int memberSeparator = value.LastIndexOf('.');
    return memberSeparator < 0 ? value : value[(memberSeparator + 1)..];
}

static string NormalizeWhitespace(string value)
{
    var result = new StringBuilder(value.Length);
    bool pendingSpace = false;
    foreach (char character in value)
    {
        if (char.IsWhiteSpace(character))
        {
            pendingSpace = result.Length != 0;
            continue;
        }

        if (pendingSpace)
        {
            result.Append(' ');
            pendingSpace = false;
        }

        result.Append(character);
    }

    return result.ToString();
}

static string EscapeTableText(string? value) => string.IsNullOrWhiteSpace(value)
    ? string.Empty
    : NormalizeWhitespace(value)
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("|", "\\|", StringComparison.Ordinal);

static string EnsureFinalNewLine(string value) => value.TrimEnd() + '\n';

static void RequireFile(string path)
{
    if (!File.Exists(path))
    {
        throw new FileNotFoundException("A documentation input was not built.", path);
    }
}

static string ResolveDotNetHost()
{
    string? configuredHost = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
    return string.IsNullOrWhiteSpace(configuredHost) ? "dotnet" : configuredHost;
}

static async Task<string> RunProcessAsync(
    string fileName,
    IReadOnlyList<string> arguments,
    string workingDirectory,
    CancellationToken cancellationToken)
{
    var startInfo = new ProcessStartInfo
    {
        FileName = fileName,
        RedirectStandardError = true,
        RedirectStandardOutput = true,
        UseShellExecute = false,
        WorkingDirectory = workingDirectory
    };
    foreach (string argument in arguments)
    {
        startInfo.ArgumentList.Add(argument);
    }

    startInfo.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
    startInfo.Environment["DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE"] = "true";
    startInfo.Environment["DOTNET_NOLOGO"] = "1";
    using Process process = Process.Start(startInfo)
        ?? throw new InvalidOperationException($"Process did not start: {fileName}.");
    using CancellationTokenRegistration cancellationRegistration = cancellationToken.Register(
        static state => TryKill((Process)state!),
        process);
    Task<string> outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
    Task<string> errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
    await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
    string output = await outputTask.ConfigureAwait(false);
    string error = await errorTask.ConfigureAwait(false);
    if (process.ExitCode != 0)
    {
        throw new InvalidOperationException(
            $"{fileName} exited with code {process.ExitCode}: {error.Trim()}");
    }

    return output;
}

static void TryKill(Process process)
{
    try
    {
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
        }
    }
    catch (InvalidOperationException) when (process.HasExited)
    {
        return;
    }
}

static async Task WriteOrVerifyAsync(
    string repositoryRoot,
    string relativePath,
    string content,
    bool verify,
    CancellationToken cancellationToken)
{
    string outputPath = Path.Join(repositoryRoot, relativePath);
    if (verify)
    {
        if (!File.Exists(outputPath))
        {
            throw new InvalidDataException(
                $"Generated documentation is missing: {relativePath}.");
        }

        string existing = await File.ReadAllTextAsync(outputPath, cancellationToken)
            .ConfigureAwait(false);
        if (!string.Equals(existing, content, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Generated documentation is stale: {relativePath}. Run {Usage}.");
        }

        return;
    }

    await File.WriteAllTextAsync(
        outputPath,
        content,
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        cancellationToken).ConfigureAwait(false);
}
