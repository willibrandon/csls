using Csls.Debugger.Interop;

namespace Csls.Debugger;

/// <summary>
/// Validates managed reference locations and exact loaded types before runtime writes.
/// </summary>
internal static class ManagedReferenceAssignmentValidator
{
    /// <summary>
    /// Rejects interior and native pointer locations that cannot receive an object-reference write.
    /// </summary>
    internal static unsafe void ValidateDestination(nint value)
    {
        uint elementType = 0;
        uint* elementTypeAddress = &elementType;
        CorDebugHResult.ThrowIfFailed(
            new ICorDebugValueAbi(value).GetType((nint)elementTypeAddress),
            "ICorDebugValue.GetType");
        elementType = Volatile.Read(ref *elementTypeAddress);
        if (elementType is not (0x0e or 0x12 or 0x14 or 0x1c or 0x1d))
        {
            throw new InvalidOperationException(
                "Reference assignment requires a managed object-reference location; " +
                "direct writes to managed by-reference and native pointer locations are not supported.");
        }
    }

    /// <summary>
    /// Compares both opaque type tokens for values borrowed from the same stopped process.
    /// </summary>
    internal static bool HaveSameRuntimeType(nint leftValue, nint rightValue)
    {
        CorDebugTypeId left = ReadTypeId(leftValue);
        CorDebugTypeId right = ReadTypeId(rightValue);
        return left.Token1 == right.Token1 && left.Token2 == right.Token2;
    }

    private static unsafe CorDebugTypeId ReadTypeId(nint value)
    {
        nint value2 = ComAbi.QueryInterface(value, ICorDebugValue2Abi.InterfaceId);
        nint exactType = 0;
        nint type2 = 0;
        try
        {
            nint* typeAddress = &exactType;
            int result = new ICorDebugValue2Abi(value2).GetExactType((nint)typeAddress);
            exactType = Volatile.Read(ref *typeAddress);
            CorDebugHResult.ThrowIfFailed(result, "ICorDebugValue2.GetExactType");
            if (exactType == 0)
            {
                throw new InvalidOperationException("ICorDebugValue2.GetExactType returned no runtime type.");
            }

            type2 = ComAbi.QueryInterface(exactType, ICorDebugType2Abi.InterfaceId);
            ulong* tokens = stackalloc ulong[2] { 0, 0 };
            CorDebugHResult.ThrowIfFailed(
                new ICorDebugType2Abi(type2).GetTypeID((nint)tokens),
                "ICorDebugType2.GetTypeID");
            return new CorDebugTypeId
            {
                Token1 = Volatile.Read(ref tokens[0]),
                Token2 = Volatile.Read(ref tokens[1])
            };
        }
        finally
        {
            if (type2 != 0)
            {
                _ = ComAbi.Release(type2);
            }

            if (exactType != 0)
            {
                _ = ComAbi.Release(exactType);
            }

            _ = ComAbi.Release(value2);
        }
    }
}
