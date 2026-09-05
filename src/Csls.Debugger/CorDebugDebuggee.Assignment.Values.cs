using Csls.Debugger.Contracts;
using Csls.Debugger.Interop;

namespace Csls.Debugger;

/// <summary>
/// Writes exact primitive, value-type, and reference values through public ICorDebug contracts.
/// </summary>
internal sealed partial class CorDebugDebuggee
{
    private void AssignManagedValue(
        nint destination,
        ManagedExpressionValue source,
        DebugExpressionLanguage language,
        bool sourceIsContextualLiteral,
        ManagedVariableMutationState mutations)
    {
        bool visualBasicNothing = language == DebugExpressionLanguage.VisualBasic && sourceIsContextualLiteral &&
            source is { HasScalar: true, Scalar: null, RuntimeValueReference: <= 0 };
        if (source.IsContextualDefault || visualBasicNothing)
        {
            AssignManagedDefault(destination, mutations);
            return;
        }

        if (ManagedRuntimeValueIdentity.GetElementType(destination) == 0x11)
        {
            if (language == DebugExpressionLanguage.CSharp && sourceIsContextualLiteral &&
                source is { HasScalar: true, Scalar: null, RuntimeValueReference: <= 0 } &&
                ManagedNullableTypeIdentity.IsNullable(destination, OpenRuntimeModule))
            {
                AssignManagedDefault(destination, mutations);
                return;
            }

            if (source.RuntimeValueReference <= 0)
            {
                throw new InvalidOperationException(
                    "Whole-value assignment requires existing unboxed value types; " +
                    "implicit boxing and unboxing are not supported.");
            }

            using var assignment = ManagedValueTypeAssignment.Prepare(
                destination, GetRuntimeValue(source), OpenRuntimeModule);
            BeginVariableMutation(mutations);
            assignment.Write();
            return;
        }

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

            BeginVariableMutation(mutations);
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
            AssignManagedReference(destination, destinationReference, source, mutations);
        }
        finally
        {
            _ = ComAbi.Release(destinationReference);
        }
    }

    private unsafe void AssignManagedReference(
        nint destination,
        nint destinationReference,
        ManagedExpressionValue source,
        ManagedVariableMutationState mutations)
    {
        ManagedReferenceAssignmentValidator.ValidateDestination(destination);
        if (source.HasScalar && source.Scalar is null && source.RuntimeValueReference <= 0)
        {
            BeginVariableMutation(mutations);
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

        nint sourceValue = GetRuntimeValue(source);
        if (!ManagedRuntimeValueIdentity.HaveSameRuntimeType(destination, sourceValue))
        {
            ManagedValueDisplay destinationDisplay = FormatRuntimeValuePair(
                destination, debuggerDisplayDepth: 0, tupleCustomTypeInfo: null).Runtime;
            throw new InvalidOperationException(
                $"Reference assignment from '{source.Type}' to " +
                $"'{destinationDisplay.Type}' requires identical loaded runtime types; " +
                "type names alone do not establish assignment compatibility.");
        }

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
            BeginVariableMutation(mutations);
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

    private void BeginVariableMutation(ManagedVariableMutationState mutations)
    {
        mutations.Advance();
        RetireResultsViewSnapshot();
    }

    private void AssignManagedDefault(nint destination, ManagedVariableMutationState mutations)
    {
        uint elementType = ManagedRuntimeValueIdentity.GetElementType(destination);
        if (elementType is 0x0e or 0x12 or 0x14 or 0x1c or 0x1d)
        {
            nint reference = ComAbi.QueryInterface(destination, ICorDebugReferenceValueAbi.InterfaceId);
            try
            {
                AssignManagedReference(destination, reference,
                    ManagedExpressionValueFactory.FromScalar(value: null, "object"), mutations);
            }
            finally
            {
                _ = ComAbi.Release(reference);
            }

            return;
        }

        using var assignment = ManagedValueTypeAssignment.PrepareDefault(destination);
        BeginVariableMutation(mutations);
        assignment.Write();
    }
}
