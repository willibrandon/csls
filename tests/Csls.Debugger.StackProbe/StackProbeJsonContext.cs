using Csls.Debugger.Contracts;
using System.Text.Json.Serialization;

namespace Csls.Debugger.StackProbe;

/// <summary>
/// Generates the isolated probe's real-process evidence serialization contracts.
/// </summary>
[JsonSerializable(typeof(DebugStackWalkProgress))]
[JsonSerializable(typeof(DebugStackTrace))]
[JsonSerializable(typeof(DebugSessionSnapshot))]
[JsonSerializable(typeof(IReadOnlyList<DebugVariableInfo>))]
internal sealed partial class StackProbeJsonContext : JsonSerializerContext;
