using Csls.Debugger.Contracts;

namespace Csls.Debugger;

/// <summary>
/// Normalizes source-language primitive aliases to stable CLR metadata identities.
/// </summary>
internal static class ManagedRuntimeTypeAliases
{
    /// <summary>
    /// Resolves a source or CLR primitive name to its metadata and debugger identities.
    /// </summary>
    /// <param name="name">The source or CLR type name.</param>
    /// <param name="language">The source language that assigns alias meaning.</param>
    /// <param name="metadataName">The normalized CLR metadata name.</param>
    /// <param name="debuggerName">The normalized debugger-facing name.</param>
    /// <returns>True when the input names a recognized primitive type.</returns>
    internal static bool TryNormalize(
        string name,
        DebugExpressionLanguage language,
        out string metadataName,
        out string debuggerName)
    {
        (metadataName, debuggerName) = NormalizeClrName(name);
        if (metadataName.Length == 0 &&
            language == DebugExpressionLanguage.VisualBasic)
        {
            (metadataName, debuggerName) = NormalizeVisualBasicClrName(name);
        }

        if (metadataName.Length != 0)
        {
            return true;
        }

        (metadataName, debuggerName) = language switch
        {
            DebugExpressionLanguage.CSharp => NormalizeCSharpAlias(name),
            DebugExpressionLanguage.VisualBasic => NormalizeVisualBasicAlias(name),
            DebugExpressionLanguage.FSharp => NormalizeFSharpAlias(name),
            _ => (string.Empty, string.Empty)
        };
        return metadataName.Length != 0;
    }

    private static (string MetadataName, string DebuggerName) NormalizeClrName(
        string name) => name switch
        {
            "System.Boolean" => ("System.Boolean", "bool"),
            "System.Byte" => ("System.Byte", "byte"),
            "System.SByte" => ("System.SByte", "sbyte"),
            "System.Char" => ("System.Char", "char"),
            "System.Decimal" => ("System.Decimal", "decimal"),
            "System.Double" => ("System.Double", "double"),
            "System.Single" => ("System.Single", "float"),
            "System.Int32" => ("System.Int32", "int"),
            "System.UInt32" => ("System.UInt32", "uint"),
            "System.Int64" => ("System.Int64", "long"),
            "System.UInt64" => ("System.UInt64", "ulong"),
            "System.Int16" => ("System.Int16", "short"),
            "System.UInt16" => ("System.UInt16", "ushort"),
            "System.Object" => ("System.Object", "object"),
            "System.String" => ("System.String", "string"),
            "System.IntPtr" => ("System.IntPtr", "nint"),
            "System.UIntPtr" => ("System.UIntPtr", "nuint"),
            _ => (string.Empty, string.Empty)
        };

    private static (string MetadataName, string DebuggerName) NormalizeVisualBasicClrName(
        string name) => name.ToUpperInvariant() switch
        {
            "SYSTEM.BOOLEAN" => ("System.Boolean", "bool"),
            "SYSTEM.BYTE" => ("System.Byte", "byte"),
            "SYSTEM.SBYTE" => ("System.SByte", "sbyte"),
            "SYSTEM.CHAR" => ("System.Char", "char"),
            "SYSTEM.DECIMAL" => ("System.Decimal", "decimal"),
            "SYSTEM.DOUBLE" => ("System.Double", "double"),
            "SYSTEM.SINGLE" => ("System.Single", "float"),
            "SYSTEM.INT32" => ("System.Int32", "int"),
            "SYSTEM.UINT32" => ("System.UInt32", "uint"),
            "SYSTEM.INT64" => ("System.Int64", "long"),
            "SYSTEM.UINT64" => ("System.UInt64", "ulong"),
            "SYSTEM.INT16" => ("System.Int16", "short"),
            "SYSTEM.UINT16" => ("System.UInt16", "ushort"),
            "SYSTEM.OBJECT" => ("System.Object", "object"),
            "SYSTEM.STRING" => ("System.String", "string"),
            "SYSTEM.INTPTR" => ("System.IntPtr", "nint"),
            "SYSTEM.UINTPTR" => ("System.UIntPtr", "nuint"),
            _ => (string.Empty, string.Empty)
        };

    private static (string MetadataName, string DebuggerName) NormalizeCSharpAlias(
        string name) => name switch
        {
            "bool" => ("System.Boolean", "bool"),
            "byte" => ("System.Byte", "byte"),
            "sbyte" => ("System.SByte", "sbyte"),
            "char" => ("System.Char", "char"),
            "decimal" => ("System.Decimal", "decimal"),
            "double" => ("System.Double", "double"),
            "float" => ("System.Single", "float"),
            "int" => ("System.Int32", "int"),
            "uint" => ("System.UInt32", "uint"),
            "long" => ("System.Int64", "long"),
            "ulong" => ("System.UInt64", "ulong"),
            "short" => ("System.Int16", "short"),
            "ushort" => ("System.UInt16", "ushort"),
            "object" => ("System.Object", "object"),
            "string" => ("System.String", "string"),
            "nint" => ("System.IntPtr", "nint"),
            "nuint" => ("System.UIntPtr", "nuint"),
            _ => (string.Empty, string.Empty)
        };

    private static (string MetadataName, string DebuggerName) NormalizeVisualBasicAlias(
        string name) => name.ToUpperInvariant() switch
        {
            "BOOLEAN" => ("System.Boolean", "bool"),
            "BYTE" => ("System.Byte", "byte"),
            "SBYTE" => ("System.SByte", "sbyte"),
            "CHAR" => ("System.Char", "char"),
            "DECIMAL" => ("System.Decimal", "decimal"),
            "DOUBLE" => ("System.Double", "double"),
            "SINGLE" => ("System.Single", "float"),
            "INTEGER" => ("System.Int32", "int"),
            "UINTEGER" => ("System.UInt32", "uint"),
            "LONG" => ("System.Int64", "long"),
            "ULONG" => ("System.UInt64", "ulong"),
            "SHORT" => ("System.Int16", "short"),
            "USHORT" => ("System.UInt16", "ushort"),
            "OBJECT" => ("System.Object", "object"),
            "STRING" => ("System.String", "string"),
            _ => (string.Empty, string.Empty)
        };

    private static (string MetadataName, string DebuggerName) NormalizeFSharpAlias(
        string name) => name switch
        {
            "bool" => ("System.Boolean", "bool"),
            "byte" => ("System.Byte", "byte"),
            "sbyte" => ("System.SByte", "sbyte"),
            "char" => ("System.Char", "char"),
            "decimal" => ("System.Decimal", "decimal"),
            "float" => ("System.Double", "double"),
            "float32" => ("System.Single", "float"),
            "int" or "int32" => ("System.Int32", "int"),
            "uint32" => ("System.UInt32", "uint"),
            "int64" => ("System.Int64", "long"),
            "uint64" => ("System.UInt64", "ulong"),
            "int16" => ("System.Int16", "short"),
            "uint16" => ("System.UInt16", "ushort"),
            "obj" => ("System.Object", "object"),
            "string" => ("System.String", "string"),
            "nativeint" => ("System.IntPtr", "nint"),
            "unativeint" => ("System.UIntPtr", "nuint"),
            _ => (string.Empty, string.Empty)
        };
}
