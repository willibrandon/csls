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
    {
        var result = new Dictionary<ushort, OpCode>();
        foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (field.FieldType == typeof(OpCode) && field.GetValue(null) is OpCode opCode)
            {
                result.Add(unchecked((ushort)opCode.Value), opCode);
            }
        }

        return result.ToFrozenDictionary();
    }
}
