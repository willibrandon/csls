using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Text;
using System.Text.Json;
using static Csls.Support.DocumentationText;

namespace Csls.Support;

/// <summary>
/// Generates the shipping Debug Adapter Protocol request and configuration reference.
/// </summary>
internal static class DapReferenceGenerator
{
    /// <summary>
    /// Generates the DAP reference from shipping requests, capabilities, and editor configuration.
    /// </summary>
    /// <param name="repositoryRoot">The absolute repository root containing the source inputs.</param>
    /// <returns>The complete generated Markdown reference with a final newline.</returns>
    internal static string Generate(string repositoryRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        string dispatchPath = Path.Join(
            repositoryRoot,
            "src",
            "Csls.DebugAdapter",
            "DapSession.Dispatch.cs");
        string initializationPath = Path.Join(
            repositoryRoot,
            "src",
            "Csls.DebugAdapter",
            "DapSession.Initialization.cs");
        string packagePath = Path.Join(
            repositoryRoot,
            "editors",
            "vscode",
            "package.json");
        RequireFile(dispatchPath);
        RequireFile(initializationPath);
        RequireFile(packagePath);

        CompilationUnitSyntax dispatch = CSharpSyntaxTree.ParseText(File.ReadAllText(dispatchPath))
            .GetCompilationUnitRoot();
        string[] requests =
        [
            .. dispatch.DescendantNodes()
                .OfType<CaseSwitchLabelSyntax>()
                .Select(static label => label.Value)
                .OfType<LiteralExpressionSyntax>()
                .Where(static literal => literal.IsKind(SyntaxKind.StringLiteralExpression))
                .Select(static literal => literal.Token.ValueText)
                .Distinct(StringComparer.Ordinal)
        ];
        if (requests.Length == 0)
        {
            throw new InvalidDataException("The DAP dispatcher exposes no request cases.");
        }

        CompilationUnitSyntax initialization = CSharpSyntaxTree
            .ParseText(File.ReadAllText(initializationPath))
            .GetCompilationUnitRoot();
        string[] capabilities =
        [
            .. initialization.DescendantNodes()
                .OfType<InvocationExpressionSyntax>()
                .Where(static invocation => GetInvokedMethodName(invocation) == "WriteBoolean")
                .Select(static invocation => invocation.ArgumentList.Arguments)
                .Where(static arguments => arguments.Count >= 2 &&
                    arguments[0].Expression is LiteralExpressionSyntax name &&
                    name.IsKind(SyntaxKind.StringLiteralExpression) &&
                    arguments[1].Expression.IsKind(SyntaxKind.TrueLiteralExpression))
                .Select(static arguments =>
                    ((LiteralExpressionSyntax)arguments[0].Expression).Token.ValueText)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
        ];
        if (capabilities.Length == 0)
        {
            throw new InvalidDataException("DAP initialization advertises no capabilities.");
        }

        (string Id, string Label, string Description, bool Default)[] exceptionFilters =
        [
            .. initialization.DescendantNodes()
                .OfType<InvocationExpressionSyntax>()
                .Where(static invocation =>
                    GetInvokedMethodName(invocation) == "WriteExceptionBreakpointFilter")
                .Select(static invocation => invocation.ArgumentList.Arguments)
                .Select(static arguments =>
                {
                    if (arguments.Count != 5 ||
                        arguments[1].Expression is not LiteralExpressionSyntax id ||
                        arguments[2].Expression is not LiteralExpressionSyntax label ||
                        arguments[3].Expression is not LiteralExpressionSyntax description ||
                        !id.IsKind(SyntaxKind.StringLiteralExpression) ||
                        !label.IsKind(SyntaxKind.StringLiteralExpression) ||
                        !description.IsKind(SyntaxKind.StringLiteralExpression) ||
                        arguments[4].Expression.Kind() is not (
                            SyntaxKind.TrueLiteralExpression or SyntaxKind.FalseLiteralExpression))
                    {
                        throw new InvalidDataException(
                            "A DAP exception filter does not use the documented literal shape.");
                    }

                    return (
                        id.Token.ValueText,
                        label.Token.ValueText,
                        description.Token.ValueText,
                        arguments[4].Expression.IsKind(SyntaxKind.TrueLiteralExpression));
                })
        ];
        if (exceptionFilters.Length == 0)
        {
            throw new InvalidDataException("DAP initialization exposes no exception filters.");
        }

        using var package = JsonDocument.Parse(File.ReadAllText(packagePath));
        JsonElement debugger = package.RootElement
            .GetProperty("contributes")
            .GetProperty("debuggers")
            .EnumerateArray()
            .Single(static candidate => string.Equals(
                candidate.GetProperty("type").GetString(),
                "coreclr",
                StringComparison.Ordinal));

        var page = new StringBuilder(
            "---\ntitle: Debug Adapter Protocol reference\ndescription: Generated csls DAP requests, capabilities, and target configuration.\n---\n\n" +
            "This page is generated from the shipping DAP dispatcher, initialize response, and " +
            "editor configuration schema. Unknown requests return an unsuccessful DAP response.\n\n" +
            "## Requests\n\n" +
            "| Request | Purpose |\n" +
            "| --- | --- |\n");
        foreach (string request in requests)
        {
            page.Append("| `").Append(request).Append("` | ")
                .Append(GetDapRequestDescription(request)).AppendLine(" |");
        }

        page.AppendLine().AppendLine("## Advertised capabilities").AppendLine()
            .AppendLine("| Initialize capability |")
            .AppendLine("| --- |");
        foreach (string capability in capabilities)
        {
            page.Append("| `").Append(capability).AppendLine("` |");
        }

        page.AppendLine().AppendLine("## Exception filters").AppendLine()
            .AppendLine("| Filter | Label | Default | Description |")
            .AppendLine("| --- | --- | --- | --- |");
        foreach ((string id, string label, string description, bool defaultValue) in exceptionFilters)
        {
            page.Append("| `").Append(id).Append("` | ")
                .Append(EscapeTableText(label)).Append(" | ")
                .Append(defaultValue ? "Yes" : "No").Append(" | ")
                .Append(EscapeTableText(description)).AppendLine(" |");
        }

        JsonElement configurations = debugger.GetProperty("configurationAttributes");
        AppendDapConfiguration(page, configurations.GetProperty("launch"), "Launch");
        AppendDapConfiguration(page, configurations.GetProperty("attach"), "Attach");
        return EnsureFinalNewLine(page.ToString());
    }

    private static void AppendDapConfiguration(
        StringBuilder page,
        JsonElement configuration,
        string name)
    {
        var required = new HashSet<string>(StringComparer.Ordinal);
        if (configuration.TryGetProperty("required", out JsonElement requiredProperties))
        {
            required.UnionWith(requiredProperties
                .EnumerateArray()
                .Select(static property => property.GetString())
                .OfType<string>());
        }

        page.AppendLine().Append("## ").Append(name).AppendLine(" configuration").AppendLine()
            .AppendLine("| Property | Type | Required | Default | Description |")
            .AppendLine("| --- | --- | --- | --- | --- |");
        foreach (JsonProperty property in configuration.GetProperty("properties").EnumerateObject())
        {
            JsonElement schema = property.Value;
            page.Append("| `").Append(property.Name).Append("` | `")
                .Append(FormatJsonSchemaType(schema)).Append("` | ")
                .Append(required.Contains(property.Name) ? "Yes" : "No").Append(" | ")
                .Append(schema.TryGetProperty("default", out JsonElement defaultValue)
                    ? $"`{EscapeTableText(defaultValue.GetRawText())}`"
                    : string.Empty)
                .Append(" | ")
                .Append(schema.TryGetProperty("description", out JsonElement description)
                    ? EscapeTableText(description.GetString())
                    : string.Empty)
                .AppendLine(" |");
        }
    }

    private static string FormatJsonSchemaType(JsonElement schema)
    {
        if (!schema.TryGetProperty("type", out JsonElement type))
        {
            return "value";
        }

        return type.ValueKind switch
        {
            JsonValueKind.String => type.GetString() ?? "value",
            JsonValueKind.Array => string.Join(" or ", type.EnumerateArray()
                .Select(static item => item.GetString())
                .OfType<string>()),
            _ => throw new InvalidDataException("A DAP configuration type is not a string or array.")
        };
    }

    private static string? GetInvokedMethodName(InvocationExpressionSyntax invocation) =>
        invocation.Expression switch
        {
            MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
            IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
            _ => null
        };

    private static string GetDapRequestDescription(string request) => request switch
    {
        "initialize" => "Negotiate client coordinates and the supported capability allowlist.",
        "launch" => "Prepare one concrete debugger-owned managed process launch.",
        "attach" => "Prepare attachment to one explicitly selected CoreCLR process.",
        "configurationDone" => "Commit configured breakpoints and start the pending target.",
        "setBreakpoints" => "Atomically replace source breakpoints for one document.",
        "setFunctionBreakpoints" => "Atomically replace managed function breakpoints.",
        "setInstructionBreakpoints" => "Atomically replace generation-safe managed-IL breakpoints.",
        "setExceptionBreakpoints" => "Atomically replace managed exception-stage policy.",
        "threads" => "List managed runtime threads.",
        "modules" => "Page loaded managed modules and effective symbol and JIT policy.",
        "loadedSources" => "List source documents from validated loaded symbols.",
        "source" => "Read bounded source content from an opaque source reference.",
        "breakpointLocations" => "List executable source locations in a requested range.",
        "pause" => "Pause the managed target.",
        "continue" => "Resume the managed target.",
        "next" => "Step over at source level.",
        "stepIn" => "Step into at source level, optionally selecting one call target.",
        "stepOut" => "Step out at source level.",
        "stepInTargets" => "List selectable managed call occurrences on the active statement.",
        "gotoTargets" => "List runtime-approved destinations in the active managed method.",
        "goto" => "Move to one previously approved destination.",
        "restart" => "Restart a launch or detach and reattach an attach session.",
        "stackTrace" => "Page managed stack frames with logical identifiers for the visible stop.",
        "scopes" => "List argument and local scopes for one frame.",
        "variables" => "Page values retained by one generation-bound variable reference.",
        "evaluate" => "Evaluate a source-language expression in one managed frame.",
        "completions" => "Complete an expression from exact stopped-frame runtime state.",
        "setVariable" => "Assign one writable child through side-effect-free evaluation.",
        "setExpression" => "Assign one writable expression through side-effect-free evaluation.",
        "readMemory" => "Read bounded bytes from an opaque managed-array memory reference.",
        "disassemble" => "Read exact-count symbolic ECMA-335 instructions.",
        "exceptionInfo" => "Describe the current managed exception stop.",
        "disconnect" => "End the adapter session with launch or attach ownership semantics.",
        "cancel" => "Acknowledge DAP cancellation after propagating request cancellation.",
        _ => throw new InvalidDataException(
            $"DAP request '{request}' has no generated-reference description.")
    };

    private static void RequireFile(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("A documentation input was not built.", path);
        }
    }
}
