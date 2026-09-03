using Csls.Debugger.Contracts;

namespace Csls.Debugger;

/// <summary>
/// Carries metadata and symbol identity for one managed instruction pointer.
/// </summary>
internal sealed class ManagedFrameLocation
{
    /// <summary>
    /// Gets or initializes the language-neutral method display name.
    /// </summary>
    internal required string Name { get; init; }

    /// <summary>
    /// Gets or initializes the source document path when available.
    /// </summary>
    internal string? SourcePath { get; init; }

    /// <summary>
    /// Gets or initializes the one-based source line, or zero when unavailable.
    /// </summary>
    internal int Line { get; init; }

    /// <summary>
    /// Gets or initializes the one-based source column, or zero when unavailable.
    /// </summary>
    internal int Column { get; init; }

    /// <summary>
    /// Gets or initializes the loaded module path used for metadata lookup.
    /// </summary>
    internal string? ModulePath { get; init; }

    /// <summary>
    /// Gets or initializes the stable session-local module identifier when available.
    /// </summary>
    internal int? ModuleId { get; init; }

    /// <summary>
    /// Gets or initializes the immutable in-memory PE image when applicable.
    /// </summary>
    internal byte[]? ModuleImage { get; init; }

    /// <summary>
    /// Gets or initializes the immutable in-memory Portable PDB image when applicable.
    /// </summary>
    internal byte[]? SymbolImage { get; init; }

    /// <summary>
    /// Gets or initializes the source-language evaluator grammar.
    /// </summary>
    internal DebugExpressionLanguage ExpressionLanguage { get; init; }
}
