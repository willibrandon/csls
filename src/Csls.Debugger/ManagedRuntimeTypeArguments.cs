using Csls.Debugger.Interop;

namespace Csls.Debugger;

/// <summary>
/// Retains bounded exact runtime type arguments for a declaring type.
/// </summary>
internal static class ManagedRuntimeTypeArguments
{
    private const int MaximumArgumentCount = 64;

    /// <summary>
    /// Reads the owned runtime type arguments of an exact declaring type.
    /// </summary>
    /// <param name="type">The borrowed exact ICorDebugType pointer.</param>
    /// <returns>The owned ICorDebugType pointers in declaration order.</returns>
    internal static unsafe nint[] Retain(nint type)
    {
        nint enumerator = 0;
        try
        {
            nint* enumeratorAddress = &enumerator;
            CorDebugHResult.ThrowIfFailed(
                new ICorDebugTypeAbi(type).EnumerateTypeParameters((nint)enumeratorAddress),
                "ICorDebugType.EnumerateTypeParameters");
            enumerator = Volatile.Read(ref *enumeratorAddress);
            return Read(enumerator);
        }
        finally
        {
            Release(enumerator);
        }
    }

    /// <summary>
    /// Reads owned declaring-type arguments followed by method arguments from a stopped IL frame.
    /// </summary>
    internal static unsafe nint[] RetainFrame(nint frame)
    {
        nint ilFrame2 = ComAbi.QueryInterface(frame, ICorDebugILFrame2Abi.InterfaceId);
        nint enumerator = 0;
        try
        {
            nint* address = &enumerator;
            CorDebugHResult.ThrowIfFailed(new ICorDebugILFrame2Abi(ilFrame2).EnumerateTypeParameters((nint)address),
                "ICorDebugILFrame2.EnumerateTypeParameters");
            enumerator = Volatile.Read(ref *address);
            return Read(enumerator);
        }
        finally
        {
            Release(enumerator);
            Release(ilFrame2);
        }
    }

    private static unsafe nint[] Read(nint enumerator)
    {
        List<nint> arguments = [];
        try
        {
            if (enumerator == 0)
            {
                throw new InvalidOperationException("The runtime returned no type-argument enumerator.");
            }

            for (int index = 0; index <= MaximumArgumentCount; index++)
            {
                nint argument = 0;
                try
                {
                    uint fetched = 0;
                    nint* argumentAddress = &argument;
                    uint* fetchedAddress = &fetched;
                    CorDebugHResult.ThrowIfFailed(
                        new ICorDebugTypeEnumAbi(enumerator).Next(
                            1, (nint)argumentAddress, (nint)fetchedAddress),
                        "ICorDebugTypeEnum.Next");
                    argument = Volatile.Read(ref *argumentAddress);
                    if (Volatile.Read(ref *fetchedAddress) == 0)
                    {
                        return [.. arguments];
                    }

                    if (index == MaximumArgumentCount)
                    {
                        break;
                    }

                    if (argument == 0)
                    {
                        throw new InvalidOperationException("The runtime returned a null type argument.");
                    }

                    arguments.Add(argument);
                    argument = 0;
                }
                finally
                {
                    if (argument != 0)
                    {
                        _ = ComAbi.Release(argument);
                    }
                }
            }

            throw new InvalidOperationException($"A runtime type exceeds {MaximumArgumentCount} arguments.");
        }
        catch
        {
            foreach (nint argument in arguments)
            {
                _ = ComAbi.Release(argument);
            }

            throw;
        }
    }

    private static void Release(nint pointer)
    {
        if (pointer != 0)
        {
            _ = ComAbi.Release(pointer);
        }
    }
}
