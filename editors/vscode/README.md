# csls for Visual Studio Code

csls provides C# and Razor language features through the same language server used
by the `csls` .NET tool. The extension includes the platform-specific Native AOT
launcher and acquires the required .NET 10 runtime through Microsoft's .NET Install
Tool. It also resolves a .NET 10 SDK for restore, build, run, debug, and test commands
in the Solution view.

VS Code desktop and remote extension hosts use the packaged Native AOT launcher and
Roslyn worker. VS Code for the Web runs the matching csls server in a WebAssembly
worker, so C# language features remain available in virtual workspaces without a
local .NET installation.

The Testing view discovers and runs Microsoft Testing Platform projects. Debugging
uses Microsoft's .NET debugger and installs its verified platform package when it is
first needed.

Disable the C# and C# Dev Kit extensions before enabling csls so only one C#
language client owns each document. Open a C# project, solution, or file-based app
and csls starts automatically.

Reference counts appear above supported C# declarations. Selecting a count opens
VS Code's native references popup with current results.

Use `csls: Restart Language Server` after changing workspace inputs. The
`csls.server.path` setting is intended for local server development; normal installs
use the bundled server.

Documentation is available at https://willibrandon.github.io/csls/.
