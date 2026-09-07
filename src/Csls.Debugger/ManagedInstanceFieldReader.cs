using Csls.Debugger.Interop;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace Csls.Debugger;

/// <summary>
/// Reads physical fields through exact constructed type hierarchies shared by live and captured inspection.
/// </summary>
/// <param name="openModule">Opens owned metadata for the supplied borrowed runtime module.</param>
internal sealed unsafe class ManagedInstanceFieldReader(Func<nint, PEReader> openModule)
{
    /// <summary>
    /// Visits borrowed declarations from the exact type to its bases until the visitor returns false.
    /// </summary>
    /// <param name="value">The dereferenced object or unboxed struct.</param>
    /// <param name="visitor">Consumes each declaration before its native and metadata owners are released.</param>
    /// <param name="cancellationToken">Cancels traversal between declarations.</param>
    internal void VisitTypes(nint value, Func<ManagedRuntimeTypeDeclaration, bool> visitor,
        CancellationToken cancellationToken = default)
    {
        nint instance = 0;
        nint value2 = 0;
        nint currentType = 0;
        try
        {
            instance = ComAbi.QueryInterface(value, ICorDebugObjectValueAbi.InterfaceId);
            value2 = ComAbi.QueryInterface(value, ICorDebugValue2Abi.InterfaceId);
            int result = new ICorDebugValue2Abi(value2).GetExactType((nint)(&currentType));
            currentType = Volatile.Read(ref currentType);
            CorDebugHResult.ThrowIfFailed(result, "ICorDebugValue2.GetExactType");
            _ = RequirePointer(currentType, "ICorDebugValue2.GetExactType");
            VisitExactType(currentType, instance, visitor, cancellationToken);
        }
        finally
        {
            Release(currentType);
            Release(value2);
            Release(instance);
        }
    }

    /// <summary>
    /// Visits an explicit constructed type hierarchy with optional borrowed native instance storage.
    /// </summary>
    internal void VisitExactType(nint type, nint instance, Func<ManagedRuntimeTypeDeclaration, bool> visitor,
        CancellationToken cancellationToken = default)
    {
        _ = ComAbi.AddRef(RequirePointer(type, "Captured exact type"));
        nint currentType = type;
        try
        {
            for (int depth = 0; currentType != 0 && depth < 256; depth++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                nint runtimeClass = 0;
                nint module = 0;
                nint baseType = 0;
                try
                {
                    int result = new ICorDebugTypeAbi(currentType).GetClass((nint)(&runtimeClass));
                    runtimeClass = Volatile.Read(ref runtimeClass);
                    CorDebugHResult.ThrowIfFailed(result, "ICorDebugType.GetClass");
                    _ = RequirePointer(runtimeClass, "ICorDebugType.GetClass");
                    result = new ICorDebugClassAbi(runtimeClass).GetModule((nint)(&module));
                    module = Volatile.Read(ref module);
                    CorDebugHResult.ThrowIfFailed(result, "ICorDebugClass.GetModule");
                    _ = RequirePointer(module, "ICorDebugClass.GetModule");
                    uint typeToken = 0;
                    CorDebugHResult.ThrowIfFailed(new ICorDebugClassAbi(runtimeClass).GetToken((nint)(&typeToken)),
                        "ICorDebugClass.GetToken");
                    using PEReader reader = openModule(module);
                    if (!visitor(new ManagedRuntimeTypeDeclaration(instance, currentType, runtimeClass,
                        module, reader.GetMetadataReader(), Volatile.Read(ref typeToken), depth)))
                    {
                        return;
                    }
                    result = new ICorDebugTypeAbi(currentType).GetBase((nint)(&baseType));
                    baseType = Volatile.Read(ref baseType);
                    CorDebugHResult.ThrowIfFailed(result, "ICorDebugType.GetBase");
                }
                finally
                {
                    Release(module);
                    Release(runtimeClass);
                    Release(currentType);
                    currentType = baseType;
                }
            }
            if (currentType != 0)
            {
                throw new InvalidOperationException("The runtime type hierarchy exceeds the supported depth of 256.");
            }
        }
        finally
        {
            Release(currentType);
        }
    }

    /// <summary>
    /// Returns an owned field value resolved against its precise declaring class and closed generic context.
    /// </summary>
    /// <param name="instance">The borrowed object interface.</param>
    /// <param name="declaringClass">The borrowed field-declaring class.</param>
    /// <param name="fieldToken">The field token scoped to that class's module.</param>
    /// <returns>The field interface owned by the caller.</returns>
    internal static nint ReadField(nint instance, nint declaringClass, uint fieldToken)
    {
        nint fieldValue = 0;
        int result = new ICorDebugObjectValueAbi(instance).GetFieldValue(declaringClass, fieldToken, (nint)(&fieldValue));
        fieldValue = Volatile.Read(ref fieldValue);
        if (result < 0)
        {
            Release(fieldValue);
            CorDebugHResult.ThrowIfFailed(result, "ICorDebugObjectValue.GetFieldValue");
        }
        return RequirePointer(fieldValue, "ICorDebugObjectValue.GetFieldValue");
    }

    private static nint RequirePointer(nint value, string operation) => value != 0
        ? value : throw new InvalidOperationException($"{operation} returned no value.");

    private static void Release(nint value)
    {
        if (value != 0)
        {
            _ = ComAbi.Release(value);
        }
    }
}
