using Csls.Protocol;
using Microsoft.CodeAnalysis.Text;

namespace Csls.Workspaces;

/// <summary>
/// Describes the name, protocol kind, and source selection for one document symbol.
/// </summary>
/// <param name="Name">The user-facing symbol name.</param>
/// <param name="Kind">The language-server symbol kind.</param>
/// <param name="SelectionSpan">The source span that identifies the symbol itself.</param>
internal readonly record struct DocumentSymbolIdentity(
    string Name,
    SymbolKind Kind,
    TextSpan SelectionSpan);
