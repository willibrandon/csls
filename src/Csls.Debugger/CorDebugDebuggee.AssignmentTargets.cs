using Csls.Debugger.Contracts;

namespace Csls.Debugger;

/// <summary>
/// Maps variable containers back to source-level assignment targets.
/// </summary>
internal sealed partial class CorDebugDebuggee
{
    /// <summary>
    /// Gets the frame and source expression represented by one container child.
    /// </summary>
    /// <param name="variablesReference">The generation-bound parent container.</param>
    /// <param name="name">The child name supplied by the debugger client.</param>
    /// <param name="generation">The current stopped generation.</param>
    /// <returns>The owning frame and canonical source target expression.</returns>
    internal (int FrameId, string Expression) GetVariableAssignmentTarget(
        int variablesReference,
        string name,
        DebugStopGeneration generation)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(variablesReference);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        ManagedScopeHandle? scope = _scopes.Values.FirstOrDefault(
            candidate => candidate.Id == variablesReference);
        int frameId;
        if (scope is not null)
        {
            ValidateGeneration(variablesReference, scope.Generation, generation);
            frameId = scope.FrameId;
        }
        else
        {
            if (!_values.TryGetValue(variablesReference, out ManagedValueHandle? value))
            {
                throw new InvalidOperationException(
                    $"Variable reference {variablesReference} is stale or unknown.");
            }

            ValidateGeneration(variablesReference, value.Generation, generation);
            frameId = value.FrameId ?? throw new InvalidOperationException(
                "The selected value has no source frame in which an assignment can be " +
                "evaluated.");
        }

        ManagedFrameHandle frame = GetFrame(frameId, generation);
        StringComparison comparison = frame.ExpressionLanguage ==
            DebugExpressionLanguage.VisualBasic
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
        DebugVariableInfo[] matches =
        [
            .. GetVariables(
                    variablesReference,
                    generation,
                    start: 0,
                    count: 0)
                .Where(variable => string.Equals(variable.Name, name, comparison))
        ];
        if (matches.Length == 0)
        {
            throw new InvalidOperationException(
                $"Variable container {variablesReference} has no child named '{name}'.");
        }

        if (matches.Length > 1)
        {
            throw new InvalidOperationException(
                $"Variable name '{name}' is ambiguous in container {variablesReference}.");
        }

        string expression = matches[0].EvaluateName ?? throw new InvalidOperationException(
            $"Variable '{name}' has no valid source expression and cannot be assigned.");
        return (frame.Id, expression);
    }
}
