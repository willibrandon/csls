---
title: Debugger symbols and source
description: Configure Portable PDB, Windows PDB, Source Link, source mapping, and symbol servers.
---

Source breakpoints, source stack locations, local names, and source stepping require
symbols whose identity matches the loaded module. csls never accepts a PDB based only on
its filename.

## Supported symbol forms

Portable PDBs work on Windows, Linux, and macOS. The debugger resolves adjacent files,
embedded Portable PDBs, runtime-provided in-memory symbols, trusted local stores, and
HTTP(S) symbol stores. On Windows, identity-matched Windows PDBs use Microsoft's public
DiaSymReader component for x86, x64, and ARM64.

In-memory PE and Portable PDB snapshots receive the same breakpoints, stacks, locals,
stepping, goto, disassembly, and instruction-breakpoint behavior as files on disk. The
debugger consumes runtime symbol updates during launch and recovers available snapshots
during attach without creating temporary module or PDB files.

## Source mapping

Use `sourceFileMap` when a PDB records paths from another build machine:

```json
{
  "sourceFileMap": {
    "C:\\agent\\_work\\app": "/workspaces/app",
    "/build/shared": "/src/shared"
  }
}
```

Both keys and values are absolute paths. Mapping understands POSIX paths, Windows drive
letters, and UNC paths regardless of the adapter host. The most specific matching prefix
wins. A mapped source is still accepted only when its content matches the checksum in
the PDB.

## Symbol search and caching

Configure trusted local directories and anonymous HTTP(S) stores with `symbolOptions`:

```json
{
  "symbolOptions": {
    "searchPaths": [
      "/srv/symbols",
      "https://symbols.example.com/"
    ],
    "searchMicrosoftSymbolServer": true,
    "searchNuGetOrgSymbolServer": false,
    "cachePath": "/home/me/.cache/csls/symbols",
    "moduleFilter": {
      "mode": "loadOnlyIncluded",
      "includedModules": ["MyCompany.*.dll"],
      "includeSymbolsNextToModules": true
    }
  }
}
```

The Microsoft and NuGet.org stores are opt-in. `moduleFilter.mode` is either
`loadAllButExcluded`, paired with `excludedModules`, or `loadOnlyIncluded`, paired with
`includedModules`. Patterns are case-insensitive and support `*` wildcards.
`includeSymbolsNextToModules` defaults to `true`, so adjacent and embedded lookup can
remain available even when configured stores are filtered.

Downloaded PDBs must match the module CodeView identity before use. Cache writes are
bounded, atomic, and keyed by identity. The default cache is `%TEMP%\SymbolCache` on
Windows and `~/.dotnet/symbolcache` on Linux and macOS.

## Source Link

Source Link retrieval is lazy, session-cached, bounded, and checksum-validated. Public
HTTPS endpoints are enabled by default. HTTP, localhost, and private-network hosts
require an exact enabled URL rule; a catch-all rule does not grant private-network
access.

```json
{
  "sourceLinkOptions": {
    "http://127.0.0.1:8080/source/*": { "enabled": true },
    "https://untrusted.example/*": { "enabled": false }
  }
}
```

Rules are matched against the Source Link URL pattern. The debugger sends no managed
credentials or cookies, limits redirects and response sizes, rejects cross-authority
redirects and HTTPS downgrade, and discards content whose PDB checksum does not match.

## Diagnosing missing source

Inspect the editor's `modules` response or module view first. `symbolStatus` reports
whether symbols were absent, filtered, unreadable, identity-mismatched, or rejected by a
server or cache policy. Confirm that:

1. the target module and PDB come from the same build;
2. the recorded document maps to an existing absolute path;
3. the source content matches the PDB checksum;
4. any symbol server is an anonymous base URL without a query or fragment; and
5. the debugger process can read the module, cache, and mapped source as its current user.

An unavailable remote store does not abort target launch. It leaves the affected module
without source symbols and records the bounded diagnostic.
