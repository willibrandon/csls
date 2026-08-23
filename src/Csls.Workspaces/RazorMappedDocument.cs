using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Csls.Workspaces;

/// <summary>
/// Describes one Razor source position mapped into an SDK-generated C# document.
/// </summary>
internal sealed class RazorMappedDocument
{
    /// <summary>
    /// Initializes a mapped Razor source position.
    /// </summary>
    /// <param name="document">The SDK-generated C# document.</param>
    /// <param name="syntaxTree">The generated C# syntax tree.</param>
    /// <param name="razorText">The current Razor source text.</param>
    /// <param name="razorPath">The absolute Razor source path.</param>
    /// <param name="generatedOffset">The zero-based generated C# offset.</param>
    internal RazorMappedDocument(
        SourceGeneratedDocument document,
        SyntaxTree syntaxTree,
        SourceText razorText,
        string razorPath,
        int generatedOffset)
    {
        Document = document;
        SyntaxTree = syntaxTree;
        RazorText = razorText;
        RazorPath = razorPath;
        GeneratedOffset = generatedOffset;
    }

    /// <summary>
    /// Gets the SDK-generated C# document.
    /// </summary>
    internal SourceGeneratedDocument Document { get; }

    /// <summary>
    /// Gets the zero-based offset within the generated C# document.
    /// </summary>
    internal int GeneratedOffset { get; }

    /// <summary>
    /// Gets the absolute Razor source path.
    /// </summary>
    internal string RazorPath { get; }

    /// <summary>
    /// Gets the current Razor source text.
    /// </summary>
    internal SourceText RazorText { get; }

    /// <summary>
    /// Gets the generated C# syntax tree and its source line mappings.
    /// </summary>
    internal SyntaxTree SyntaxTree { get; }
}
