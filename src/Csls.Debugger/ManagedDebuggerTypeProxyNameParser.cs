using System.Reflection.Metadata;

namespace Csls.Debugger;

/// <summary>
/// Parses bounded assembly-qualified debugger proxy type names without loading target code.
/// </summary>
internal static class ManagedDebuggerTypeProxyNameParser
{
    private const int MaximumTypeNameLength = 4096;
    private const int MaximumTypeNameNodes = 64;

    /// <summary>
    /// Tries to parse one reflection type name into its definition and assembly identity.
    /// </summary>
    /// <param name="value">The attribute-encoded reflection type name.</param>
    /// <param name="result">Receives the parsed proxy identity.</param>
    /// <returns>True when the bounded name is structurally valid.</returns>
    internal static bool TryParse(
        string value,
        out ManagedDebuggerTypeProxyName? result)
    {
        result = null;
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaximumTypeNameLength)
        {
            return false;
        }

        try
        {
            if (!TypeName.TryParse(
                value.AsSpan(),
                out TypeName? parsed,
                new TypeNameParseOptions { MaxNodes = MaximumTypeNameNodes }) ||
                parsed is null ||
                parsed.IsArray ||
                parsed.IsPointer ||
                parsed.IsByRef)
            {
                return false;
            }

            result = new ManagedDebuggerTypeProxyName(parsed);
            return true;
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }
}
