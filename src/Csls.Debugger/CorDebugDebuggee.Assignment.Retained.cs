using Csls.Debugger.Contracts;
using Csls.Debugger.Interop;

namespace Csls.Debugger;

/// <summary>
/// Assigns directly to retained object fields without reevaluating the expression that produced the object.
/// </summary>
internal sealed partial class CorDebugDebuggee
{
    /// <summary>
    /// Writes a stopped-state value to a retained object's physical field and refreshes that exact storage.
    /// </summary>
    /// <param name="frameId">The logical source frame in which the value is evaluated.</param>
    /// <param name="variablesReference">The current-generation object returned by inspection or evaluation.</param>
    /// <param name="name">The visible child field selected by the client.</param>
    /// <param name="value">The validated value expression.</param>
    /// <param name="generation">The stop generation authorizing this assignment.</param>
    /// <param name="mutations">The assignment revision advanced before writing target storage.</param>
    /// <returns>The written field and its current expansion handles.</returns>
    internal DebugVariableInfo SetRetainedVariable(int frameId, int variablesReference, string name,
        DebugExpressionPlan value, DebugStopGeneration generation, ManagedVariableMutationState mutations)
    {
        ManagedFrameHandle frame = GetFrame(frameId, generation);
        ManagedExpressionPlanValidator.Validate(value, frame.ExpressionLanguage);
        if (!_values.TryGetValue(variablesReference, out ManagedValueHandle? retained))
        {
            throw new InvalidOperationException($"Variable reference {variablesReference} is stale or unknown.");
        }
        ValidateGeneration(variablesReference, retained.Generation, generation);
        ValidateValueLifetime(retained);
        ManagedExpressionValue source = EvaluateNode(frame, value, value.Root, generation);
        ManagedValueDisplay display = FormatRuntimeValuePair(retained.Pointer, 0, retained.TupleCustomTypeInfo).Runtime;
        ManagedExpressionValue receiver = ManagedExpressionValueFactory.FromVariable(
            new DebugVariableInfo("$result", display.Value, display.Type, variablesReference,
                retained.MemoryReference, EvaluateName: null), variablesReference, display);
        nint parent = DereferenceValue(retained.Pointer);
        try
        {
            ManagedValueTypeAssignment.ValidateFieldParent(parent);
        }
        finally
        {
            _ = ComAbi.Release(parent);
        }
        using var destination = ManagedAssignmentTarget.TakeOwnership(
            ResolveInstanceMemberValue(receiver, name, value.Language, out _, allowFieldBackedProperty: false), evaluateName: null);
        return AssignResolvedValue(frame, destination, source, name, generation, value.Language,
            value.Root.Kind == DebugExpressionNodeKind.Literal, mutations);
    }
}
