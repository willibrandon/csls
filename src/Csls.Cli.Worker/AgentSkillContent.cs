namespace Csls.Cli.Worker;

/// <summary>
/// Provides the versioned agent instructions emitted by csls agent init.
/// </summary>
internal static class AgentSkillContent
{
    /// <summary>
    /// Contains the complete reusable csls agent skill document.
    /// </summary>
    internal const string Value = """
        ---
        name: csls
        description: C# language intelligence with the csls CLI and MCP server. Use for workspace diagnosis, navigation, semantic queries, and guarded source edits.
        ---

        # csls C# language intelligence

        Use csls for compiler-backed information about a real .NET workspace. Prefer JSON output for automation and use MCP when an interactive agent session needs several related operations.

        ## Inspect a workspace

        ```console
        csls doctor . --json
        csls sessions list --json
        csls sessions show --session <pid> --json
        ```

        `doctor` always verifies a transient workspace. Query, edit, workspace, request, and trace commands reuse a matching live session when one exists. Pass `--workspace <path>` to start a transient session when no editor owns the workspace, or pass `--session <pid>` to select an exact editor session.

        ## Query language intelligence

        ```console
        csls query diagnostics Program.cs --workspace . --json
        csls query hover Program.cs --line 12 --character 8 --workspace . --json
        csls query completion Program.cs --line 12 --character 8 --workspace . --limit 50 --json
        csls query definition Program.cs --line 12 --character 8 --workspace . --json
        csls query references Program.cs --line 12 --character 8 --workspace . --json
        csls query document-symbols Program.cs --workspace . --json
        csls query symbols Service --workspace . --limit 50 --json
        ```

        Positions are zero-based UTF-16 line and character offsets. Read the current document before choosing a position. When a JSON response contains `nextCursor`, repeat the command with `--cursor <cursor>` to read the next bounded page.

        ## Preview and apply edits

        ```console
        csls edit rename Program.cs NewName --line 12 --character 8 --workspace . --json
        csls edit format Program.cs --workspace . --json
        csls edit code-action Program.cs --kind quickfix --line 12 --character 8 --workspace . --json
        ```

        Edit commands preview guarded plans by default. Inspect the document preconditions and changes before repeating the command with `--apply`.

        ## Maintain a live session

        ```console
        csls workspace restore --session <pid> --json
        csls workspace reload --session <pid> --json
        csls requests list --session <pid> --json
        csls trace start --session <pid> --json
        csls trace stop --session <pid> --json
        ```

        ## Run MCP

        Install the separate `csls-mcp` .NET tool, then run one of these commands from an MCP client:

        ```console
        csls agent mcp --workspace .
        csls agent mcp --session <pid>
        ```

        Direct workspace mode owns a transient language-server process. Session mode attaches without taking ownership of the editor process.
        """ + "\n";
}
