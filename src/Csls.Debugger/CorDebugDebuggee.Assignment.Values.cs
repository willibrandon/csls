using Csls.Debugger.Contracts;
using Csls.Debugger.Interop;

namespace Csls.Debugger;

/// <summary>
/// Writes exact primitive and reference values through public ICorDebug contracts.
/// </summary>
internal sealed partial class CorDebugDebuggee
{
    private void AssignManagedValue(
        nint destination,
        ManagedExpressionValue source,
        DebugExpressionLanguage language,
        bool sourceIsContextualLiteral)
    {
        if (ComAbi.TryQueryInterface(
            destination,
            ICorDebugGenericValueAbi.InterfaceId,
            out nint generic))
        {
            _ = ComAbi.Release(generic);
            ManagedValueDisplay destinationDisplay = FormatRuntimeValuePair(
                destination, debuggerDisplayDepth: 0, tupleCustomTypeInfo: null).Runtime;
            source = ManagedPrimitiveConversionEvaluator.ConvertForAssignment(
                source,
                destinationDisplay.Type,
                language,
                sourceIsContextualLiteral);
            object? scalar = ManagedExpressionValueFactory.RequireScalar(source);
            if (scalar is null || scalar is string ||
                !string.Equals(
                    destinationDisplay.Type,
                    source.Type,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"A managed '{destinationDisplay.Type}' value requires an exact, " +
                    "non-null primitive assignment of the same type.");
            }

            SetManagedPrimitiveValue(destination, destinationDisplay.Type, scalar);
            return;
        }

        if (!ComAbi.TryQueryInterface(
            destination,
            ICorDebugReferenceValueAbi.InterfaceId,
            out nint destinationReference))
        {
            throw new InvalidOperationException(
                "The selected managed value does not expose a writable primitive or " +
                "reference location.");
        }

        try
        {
            AssignManagedReference(destination, destinationReference, source);
        }
        finally
        {
            _ = ComAbi.Release(destinationReference);
        }
    }

    private unsafe void AssignManagedReference(
        nint destination,
        nint destinationReference,
        ManagedExpressionValue source)
    {
        if (source.HasScalar && source.Scalar is null)
        {
            CorDebugHResult.ThrowIfFailed(
                new ICorDebugReferenceValueAbi(destinationReference).SetValue(0),
                "ICorDebugReferenceValue.SetValue");
            return;
        }

        if (source.RuntimeValueReference <= 0)
        {
            throw new InvalidOperationException(
                "Reference assignment requires null or an existing runtime reference; " +
                "materializing a new target object is not supported yet.");
        }

        ManagedValueDisplay destinationDisplay = FormatRuntimeValuePair(
            destination, debuggerDisplayDepth: 0, tupleCustomTypeInfo: null).Runtime;
        if (!string.Equals(
            destinationDisplay.Type,
            source.Type,
            StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Reference assignment from '{source.Type}' to " +
                $"'{destinationDisplay.Type}' requires a conversion that is not supported.");
        }

        nint sourceValue = GetRuntimeValue(source);
        nint sourceReference = 0;
        try
        {
            sourceReference = ComAbi.QueryInterface(
                sourceValue,
                ICorDebugReferenceValueAbi.InterfaceId);
            ulong address = 0;
            ulong* addressPointer = &address;
            CorDebugHResult.ThrowIfFailed(
                new ICorDebugReferenceValueAbi(sourceReference).GetValue(
                    (nint)addressPointer),
                "ICorDebugReferenceValue.GetValue");
            CorDebugHResult.ThrowIfFailed(
                new ICorDebugReferenceValueAbi(destinationReference).SetValue(
                    Volatile.Read(ref *addressPointer)),
                "ICorDebugReferenceValue.SetValue");
        }
        finally
        {
            if (sourceReference != 0)
            {
                _ = ComAbi.Release(sourceReference);
            }
        }
    }
}
