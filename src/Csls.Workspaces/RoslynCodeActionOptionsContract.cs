using System.Reflection;
using Microsoft.CodeAnalysis.CodeActions;

namespace Csls.Workspaces;

/// <summary>
/// Exposes Roslyn's host-capability check for option-dependent code actions.
/// </summary>
internal static class RoslynCodeActionOptionsContract
{
    private static readonly MethodInfo s_isOptionServiceAvailableMethod =
        typeof(CodeActionWithOptions).GetMethod(
            "IsOptionServiceAvailable",
            BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException(
            "Roslyn's code-action option capability check was not found.");

    /// <summary>
    /// Determines whether the workspace can execute an option-dependent action.
    /// </summary>
    internal static bool IsOptionServiceAvailable(CodeActionWithOptions action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return s_isOptionServiceAvailableMethod.Invoke(action, null) is true;
    }
}
