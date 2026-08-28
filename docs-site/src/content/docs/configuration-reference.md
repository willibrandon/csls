---
title: Configuration reference
description: Generated csls settings and defaults from the server contract.
---

The `csls` section takes precedence over the compatible `csharp` section.

| Setting | Type | Default | Description |
| --- | --- | --- | --- |
| `enableAnalyzers` | `boolean` | `true` | Gets whether project analyzers contribute document diagnostics. |
| `formatOnSave` | `boolean` | `false` | Gets whether the server returns document formatting edits before a save. |
| `inlayHints.enableInlayHintsForParameters` | `boolean` | `false` | Gets whether parameter-name inlay hints are enabled. |
| `inlayHints.enableInlayHintsForTypes` | `boolean` | `false` | Gets whether inferred-type inlay hints are enabled. |
| `diagnostics.reportInformationAsHint` | `boolean` | `true` | Gets whether informational diagnostics are presented as editor hints. |
| `configuration` | `string` | `Debug` | Gets the MSBuild configuration used to evaluate loaded projects. |
| `logLevel` | `logging level` | `Information` | Gets the minimum level written by language-server logging providers. |
