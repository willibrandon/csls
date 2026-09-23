# Third-party notices

csls is informed by the following independently maintained project:

- csharp-language-server: https://github.com/razzmatazz/csharp-language-server
  - License: MIT
  - Source snapshot: 9e7fd6745f38ae817ce42a3ba76b3621099f8d5f
  - Snapshot date: 2026-08-22
- Zed C# extension: https://github.com/zed-extensions/csharp
  - License: Apache-2.0
  - Language queries and task definitions adapted from version 1.2.2
- Debug Adapter Protocol schema: https://github.com/microsoft/debug-adapter-protocol
  - License: MIT
  - The checked-in schema is used to validate and generate debugger protocol contracts.
- .NET runtime ICorDebug IDL: https://github.com/dotnet/runtime
  - License: MIT
  - The checked-in IDL is used to generate NativeAOT debugger ABI projections.

Runtime and development dependencies retain their respective licenses. The
dependency ledger and release SBOM are generated and validated during release.
