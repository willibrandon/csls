namespace Csls.Debugger;

/// <summary>
/// Validates managed reference locations and exact loaded types before runtime writes.
/// </summary>
internal static class ManagedReferenceAssignmentValidator
{
    /// <summary>
    /// Rejects interior and native pointer locations that cannot receive an object-reference write.
    /// </summary>
    internal static void ValidateDestination(nint value)
    {
        uint elementType = ManagedRuntimeValueIdentity.GetElementType(value);
        if (elementType is not (0x0e or 0x12 or 0x14 or 0x1c or 0x1d))
        {
            throw new InvalidOperationException(
                "Reference assignment requires a managed object-reference location; " +
                "direct writes to managed by-reference and native pointer locations are not supported.");
        }
    }
}
