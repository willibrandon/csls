using Csls.Debugger.Contracts;

namespace Csls.Debugger;

/// <summary>
/// Identifies a managed frame across one debugger-owned target execution.
/// </summary>
/// <param name="ThreadId">The runtime thread identifier.</param>
/// <param name="FrameIndex">The zero-based managed stack position.</param>
/// <param name="MethodToken">The method-definition metadata token.</param>
/// <param name="ModuleId">The stable session-local module identifier when available.</param>
/// <param name="ModulePath">The loaded module path when available.</param>
/// <param name="Name">The language-neutral managed method name.</param>
/// <param name="Language">The source-language evaluator grammar.</param>
internal sealed record ManagedFrameSelection(
    int ThreadId,
    int FrameIndex,
    uint MethodToken,
    int? ModuleId,
    string? ModulePath,
    string Name,
    DebugExpressionLanguage Language);
