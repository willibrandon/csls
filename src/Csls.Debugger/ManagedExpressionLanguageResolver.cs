using Csls.Debugger.Contracts;

namespace Csls.Debugger;

/// <summary>
/// Maps managed symbol document language identifiers to evaluator grammars.
/// </summary>
internal static class ManagedExpressionLanguageResolver
{
    private static readonly Guid s_csharp = new("3f5162f8-07c6-11d3-9053-00c04fa302a1");
    private static readonly Guid s_visualBasic = new("3a12d0b8-c26c-11d0-b442-00a0244a1dd2");
    private static readonly Guid s_fsharp = new("ab4f38c9-b6e6-43ba-be3b-58080b2ccce3");

    /// <summary>
    /// Resolves a Portable or Windows PDB document language identifier.
    /// </summary>
    /// <param name="languageId">The symbol document language identifier.</param>
    /// <returns>The corresponding evaluator language.</returns>
    internal static DebugExpressionLanguage Resolve(Guid languageId) =>
        languageId == s_csharp
            ? DebugExpressionLanguage.CSharp
            : languageId == s_visualBasic
                ? DebugExpressionLanguage.VisualBasic
                : languageId == s_fsharp
                    ? DebugExpressionLanguage.FSharp
                    : DebugExpressionLanguage.Common;
}
