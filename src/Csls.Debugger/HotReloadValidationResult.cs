namespace Csls.Debugger;

/// <summary>
/// Carries a validated compiler update and its exact runtime remap decisions.
/// </summary>
/// <param name="UpdatedMethods">The aggregate method tokens updated by the generation.</param>
/// <param name="UpdatedTypes">The validated aggregate type-definition tokens.</param>
/// <param name="ActiveStatementRemaps">The exact old-to-current active instruction maps.</param>
internal sealed record HotReloadValidationResult(
    IReadOnlyList<uint> UpdatedMethods,
    IReadOnlyList<uint> UpdatedTypes,
    IReadOnlyList<HotReloadActiveStatementRemap> ActiveStatementRemaps);
