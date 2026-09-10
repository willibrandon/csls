namespace Csls.Debugger;

/// <summary>
/// Carries one validated old-to-current managed instruction remap.
/// </summary>
/// <param name="MethodToken">The method-definition metadata token.</param>
/// <param name="MethodVersion">The positive old method version.</param>
/// <param name="OldIlOffset">The old active managed IL offset.</param>
/// <param name="NewIlOffset">The current-generation managed IL offset.</param>
internal sealed record HotReloadActiveStatementRemap(
    uint MethodToken,
    int MethodVersion,
    uint OldIlOffset,
    uint NewIlOffset);
