# AGENTS.md

`csls` targets .NET 10 and C# 14. Read `CONTRIBUTING.md` before changing code.

## Required conventions

- Use `Csls.*` namespaces and assemblies.
- Put exactly one class, interface, enum, record, struct, or delegate in each C# file.
- Document every public or internal type and member with triple-slash XML documentation.
- Write every XML `<summary>` as exactly three lines: opening tag, text, closing tag.
- Use Central Package Management as the single package-version source.
- Keep nullable references, analyzers, deterministic builds, and warnings-as-errors enabled.
- Keep LSP and MCP stdout protocol-only; diagnostics and progress go to stderr.
- Use System.CommandLine for CLI parsing, StreamJsonRpc for RPC, and Hex1b for terminal UI.
- Core product direct dependencies are limited to the approved Microsoft packages and Hex1b.
- Never add telemetry.
- Implement repository automation only as .NET file-based C# apps. Do not add
  shell, PowerShell, batch, or command scripts.

## Testing

- Use MSTest 4 and Microsoft.Testing.Platform.
- Run tests with `dotnet test`; never use `--no-build`.
- Use real processes, streams, Unix-domain sockets, files, repositories, projects,
  packages, SDK/MSBuild/Roslyn/Razor components, and editor clients.
- Never use a mocking library or a hand-written substitute for a production service.
- Synthetic data is allowed only for malformed or hostile input coverage and must
  still pass through a real transport or file boundary.

## Porting privacy

- Treat the source project and all reference repositories as read-only.
- Do not commit private issue or pull-request mappings, contributor handles, or
  cross-repository references.
- Never comment, react, label, edit, or otherwise notify source-project items.
