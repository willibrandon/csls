---
title: Debug Adapter Protocol reference
description: Generated csls DAP requests, capabilities, and target configuration.
---

This page is generated from the shipping DAP dispatcher, initialize response, and editor configuration schema. Unknown requests return an unsuccessful DAP response.

## Requests

| Request | Purpose |
| --- | --- |
| `initialize` | Negotiate client coordinates and the supported capability allowlist. |
| `launch` | Prepare one concrete debugger-owned managed process launch. |
| `attach` | Prepare attachment to one explicitly selected CoreCLR process. |
| `configurationDone` | Commit configured breakpoints and start the pending target. |
| `setBreakpoints` | Atomically replace source breakpoints for one document. |
| `setFunctionBreakpoints` | Atomically replace managed function breakpoints. |
| `setInstructionBreakpoints` | Atomically replace generation-safe managed-IL breakpoints. |
| `setExceptionBreakpoints` | Atomically replace managed exception-stage policy. |
| `threads` | List managed runtime threads. |
| `modules` | Page loaded managed modules and effective symbol and JIT policy. |
| `loadedSources` | List source documents from validated loaded symbols. |
| `source` | Read bounded source content from an opaque source reference. |
| `breakpointLocations` | List executable source locations in a requested range. |
| `pause` | Pause the managed target. |
| `continue` | Resume the managed target. |
| `next` | Step over at source level. |
| `stepIn` | Step into at source level, optionally selecting one call target. |
| `stepOut` | Step out at source level. |
| `stepInTargets` | List selectable managed call occurrences on the active statement. |
| `gotoTargets` | List runtime-approved destinations in the active managed method. |
| `goto` | Move to one previously approved destination. |
| `restart` | Restart a launch or detach and reattach an attach session. |
| `stackTrace` | Page generation-bound managed stack frames. |
| `scopes` | List argument and local scopes for one frame. |
| `variables` | Page values retained by one generation-bound variable reference. |
| `evaluate` | Evaluate a source-language expression in one managed frame. |
| `completions` | Complete an expression from exact stopped-frame runtime state. |
| `setVariable` | Assign one writable child through side-effect-free evaluation. |
| `setExpression` | Assign one writable expression through side-effect-free evaluation. |
| `readMemory` | Read bounded bytes from an opaque managed-array memory reference. |
| `disassemble` | Read exact-count symbolic ECMA-335 instructions. |
| `exceptionInfo` | Describe the current managed exception stop. |
| `disconnect` | End the adapter session with launch or attach ownership semantics. |
| `cancel` | Acknowledge DAP cancellation after propagating request cancellation. |

## Advertised capabilities

| Initialize capability |
| --- |
| `supportsBreakpointLocationsRequest` |
| `supportsCancelRequest` |
| `supportsCompletionsRequest` |
| `supportsConditionalBreakpoints` |
| `supportsConfigurationDoneRequest` |
| `supportsDisassembleRequest` |
| `supportsEvaluateForHovers` |
| `supportsExceptionFilterOptions` |
| `supportsExceptionInfoRequest` |
| `supportsFunctionBreakpoints` |
| `supportsGotoTargetsRequest` |
| `supportsHitConditionalBreakpoints` |
| `supportsInstructionBreakpoints` |
| `supportsInvalidatedEvent` |
| `supportsLoadedSourcesRequest` |
| `supportsLogPoints` |
| `supportsModulesRequest` |
| `supportsReadMemoryRequest` |
| `supportsRestartRequest` |
| `supportsSetExpression` |
| `supportsSetVariable` |
| `supportsStepInTargetsRequest` |
| `supportsVariablePaging` |

## Exception filters

| Filter | Label | Default | Description |
| --- | --- | --- | --- |
| `all` | Thrown Exceptions | No | Break when any managed exception is thrown. |
| `user-unhandled` | User-Unhandled Exceptions | No | Break when a managed exception escapes user code. |
| `unhandled` | Unhandled Exceptions | Yes | Break when a managed exception has no runtime handler. |

## Launch configuration

| Property | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `program` | `string` | Yes |  | Absolute path to the managed application assembly. |
| `cwd` | `string` | No |  | Working directory for the application. |
| `args` | `array` | No | `[]` | Arguments passed directly to the managed target without shell interpretation. |
| `noDebug` | `boolean` | No | `false` | Launch the target without attaching CoreCLR. |
| `env` | `object` | No |  | Environment variables added to the target. A null value removes an inherited variable. |
| `runtimeHost` | `string` | No |  | Absolute path to the compatible dotnet host used for a managed assembly. |
| `sourceFileMap` | `object` | No |  | Maps absolute build-time source prefixes to absolute local source prefixes. |
| `sourceLinkOptions` | `object` | No |  | Controls Source Link URL patterns, including explicit private-network authorization. |
| `symbolOptions` | `object` | No |  | Controls identity-validated Portable PDB discovery and caching. |
| `justMyCode` | `boolean` | No | `true` | Restrict source stepping to symbol-bearing unoptimized user modules. |
| `enableStepFiltering` | `boolean` | No | `true` | Skip properties, CLR operators, and debugger step-filter attributes during source stepping. |
| `suppressJITOptimizations` | `boolean` | No | `false` | Request unoptimized JIT code for symbol-bearing modules during launch. |

## Attach configuration

| Property | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `processId` | `integer` | Yes |  | Operating-system process identifier for a running .NET process. |
| `sourceFileMap` | `object` | No |  | Maps absolute build-time source prefixes to absolute local source prefixes. |
| `sourceLinkOptions` | `object` | No |  | Controls Source Link URL patterns, including explicit private-network authorization. |
| `symbolOptions` | `object` | No |  | Controls identity-validated Portable PDB discovery and caching. |
| `justMyCode` | `boolean` | No | `true` | Restrict source stepping to symbol-bearing unoptimized user modules. |
| `enableStepFiltering` | `boolean` | No | `true` | Skip properties, CLR operators, and debugger step-filter attributes during source stepping. |
