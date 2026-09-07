# Managed dump inspection design

A dump session uses private debugger control contracts in a supervised managed
worker. ClrMD reads captured runtime, thread, stack, and module identities.
CoreCLR virtual-process inspection reads captured frame arguments and locals
through `ICLRDebugging.OpenVirtualProcess`. MCP `debug_dump_open` opens these
sessions through `debugger/openDump`. DAP `attach` selects an absolute `dumpPath`
or a live `processId`. Dump attachments accept a zero-based `runtimeIndex` and
an optional absolute `dacPath` for the matching runtime Data Access Component.
`binarySearchPaths` supplies up to 64 ordered, existing absolute local directories
containing application binaries and adjacent symbols. Activation validates and
snapshots those paths before acquiring the dump. Matching uses recorded image and
symbol identities, so moved application builds remain inspectable.

Dump activation publishes its capability set before `initialized`, completes
configuration and attachment, and emits a `stopped` event. The backend exposes
bounded managed thread, stack, scope, variable, and module inspection with stable
frame identities. Native frame matching uses the captured method token and stack
pointer. Variable pages read only their requested slots; unavailable captured
storage remains an explicit per-value result. DAP and MCP route inspection through
the same worker contracts, with target-code execution disabled for dump reads.

Captured-frame reads carry request cancellation through CoreCLR's memory and
thread-context callbacks. Callback progress reports completed read counts and
copied bytes at the first read, each 64-read checkpoint, and completion. The
optional private-RPC `DumpReadProgress` receiver observes the same operation.
Managed inspection restores the caller's cancellation after native calls unwind,
preserves progress-observer failures, and releases temporary native references
before reporting the terminal outcome. A failed inspection retires its virtual
process cache; the next read opens a new cache over the same immutable dump while
preserving logical frame identifiers and the session generation.

Captured arrays expose their runtime element count and indexed child pages.
Multidimensional indices use the captured dimensions and signed lower bounds in
row-major order. A session-owned store retains logical frame-slot and child-index
paths, reacquiring native values for each request and releasing them before the
response. Repeated paths retain stable identifiers across native cache replacement.
Each page contains at most 4,096 elements; the session retains at most 65,536 paths
with 256 nested selections. Failed or canceled reads roll back unpublished paths
while preserving earlier handles. Closing the dump retires the entire path store.

Physical parameter names come from the captured module metadata. Local names use
the active half-open PDB scope at the captured IL offset. A bounded seekable view
of captured module memory supplies the PE layout and expected CodeView identity;
the recorded module path locates adjacent local symbols. Matching associated and
embedded Portable PDBs share the live debugger's parameter and local-name readers.
The reader bounds symbol files and embedded expansion sizes, opens Unix inputs
without waiting for FIFO writers, and preserves the caller's image ownership.

CoreCLR requests additional module metadata through `ICorDebugMetaDataLocator`.
The callback searches recorded local paths, explicit binary directories, and installed runtime directories,
checks the captured PE timestamp and image size, and retains a private copy for
native metadata readers. Managed AnyCPU images use their recorded PE identity.
Snapshots contain valid managed metadata and are checked again after copying.
Each image is bounded to 512 MiB, metadata to 64 MiB, and retained copies to
4,096 modules and 1 GiB. Cancellation is checked during discovery and copying.
Virtual-process disposal releases native readers before deleting owned copies.

Process identity is read from the captured platform records. Mach-O captures use
CoreCLR's thread-info segment, with bounded load-command traversal and validation
of its signature, process identifier, thread count, and recorded file range.

Native library resolution validates the requested image's own build identity or
the CoreCLR identity of its trusted installation, according to the runtime's
library-provider index. Windows PE checks include architecture, timestamp, and
image size; Unix image checks use ELF build IDs or Mach-O UUIDs. Explicit DAC
selection uses the DAC's captured identity, or a digest comparison with the
identity-matched trusted installation when the dump indexes by CoreCLR. Windows
signature verification remains enabled. The virtual process owns its COM
references independently of live debugger sessions and releases them before the
captured memory provider closes.
Disconnect and protocol EOF release the supervised worker and its dump mappings.
