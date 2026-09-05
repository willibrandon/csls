### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|------
CSLS0004 | Naming | Error | Static fields use the s_ prefix
CSLS0005 | CodeQuality | Error | Metadata projections use Select before iteration
CSLS0006 | Reliability | Error | Disposable locals use structured ownership
CSLS0007 | Reliability | Error | Disposable collections use exception-safe lifetimes
CSLS0008 | Reliability | Error | Disposable locals have exception-safe cleanup before ownership transfer
CSLS0009 | CodeQuality | Error | Sequence filters are expressed before iteration
CSLS0010 | CodeQuality | Error | Boolean conditional throws use statement control flow
CSLS0011 | CodeQuality | Error | Initialization-only fields use the readonly modifier
CSLS0012 | CodeQuality | Error | By-reference method state is encapsulated after two parameters
CSLS0013 | CodeQuality | Error | Complex Boolean conditions use named decisions
CSLS0014 | CodeQuality | Error | Deconstructed collection aliases are not mutation-only
CSLS0015 | CodeQuality | Error | Nullable out variables are proved before dereferencing
CSLS0016 | CodeQuality | Error | Redundant nested implicit upcasts are removed
CSLS0017 | CodeQuality | Error | Repeated null tests after exiting guards are removed
CSLS0018 | CodeQuality | Error | Writes to unread locals are removed
CSLS0019 | CodeQuality | Error | Explicit casts to the operand's existing type are removed
CSLS0020 | CodeQuality | Error | Same-target conditional assignments use a conditional expression
CSLS0021 | Reliability | Error | Catch blocks explicitly recover from or propagate exceptions
CSLS0022 | Reliability | Error | Path composition preserves preceding components with Path.Join
CSLS0023 | CodeQuality | Error | Nullable properties are captured before unwrapping
CSLS0024 | Reliability | Error | Catch-all handlers filter failures or rethrow the original exception
