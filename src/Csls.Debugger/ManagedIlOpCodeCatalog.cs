using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Reflection.Emit;

namespace Csls.Debugger;

/// <summary>
/// Provides the runtime's complete ECMA-335 opcode table for NativeAOT-safe decoding.
/// </summary>
internal static class ManagedIlOpCodeCatalog
{
    private static readonly FrozenDictionary<ushort, OpCode> s_opCodesByValue =
        Create(typeof(OpCodes));

    /// <summary>
    /// Resolves a one-byte or two-byte encoded opcode value.
    /// </summary>
    /// <param name="value">The encoded opcode value.</param>
    /// <param name="opCode">Receives the runtime opcode descriptor.</param>
    /// <returns>True when the value is a defined ECMA-335 opcode.</returns>
    internal static bool TryGet(ushort value, out OpCode opCode) =>
        s_opCodesByValue.TryGetValue(value, out opCode);

    private static FrozenDictionary<ushort, OpCode> Create(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicFields)] Type type)
        => type.GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(static field => field.FieldType == typeof(OpCode))
            .Select(static field => field.GetValue(null))
            .OfType<OpCode>()
            .ToFrozenDictionary(
                static opCode => unchecked((ushort)opCode.Value),
                static opCode => opCode);
}
