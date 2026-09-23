using System.Text.Json.Serialization;

namespace Csls.Debugger.Control;

/// <summary>
/// Supplies NativeAOT-safe JSON metadata for the private terminal launch handshake.
/// </summary>
[JsonSerializable(typeof(DebuggerTerminalLaunchInstruction))]
internal sealed partial class DebuggerTerminalLaunchJsonContext : JsonSerializerContext;
