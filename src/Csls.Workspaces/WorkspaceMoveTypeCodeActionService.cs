using Csls.Protocol;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Text;
using LspCodeAction = Csls.Protocol.CodeAction;

namespace Csls.Workspaces;

/// <summary>
/// Produces a move-to-file refactoring for a top-level C# type declaration.
/// </summary>
internal static class WorkspaceMoveTypeCodeActionService
{
    private const string RefactorCodeActionKind = "refactor";

    /// <summary>
    /// Gets a move-to-file action when the selected type shares its source file.
    /// </summary>
    /// <param name="document">The current Roslyn document.</param>
    /// <param name="parameters">The target range and requested action context.</param>
    /// <param name="createWorkspaceEditAsync">Creates an ordered LSP workspace edit.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>The move action, or null when moving the selected declaration is unsafe.</returns>
    internal static async Task<LspCodeAction?> GetActionAsync(
        Document document,
        CodeActionParams parameters,
        Func<Solution, Solution, CancellationToken, Task<WorkspaceEdit>>
            createWorkspaceEditAsync,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(createWorkspaceEditAsync);
        if (string.IsNullOrWhiteSpace(document.FilePath))
        {
            return null;
        }

        SourceText sourceText = await document.GetTextAsync(cancellationToken)
            .ConfigureAwait(false);
        if (IsGeneratedDocument(sourceText))
        {
            return null;
        }

        SyntaxNode rootNode = await document.GetSyntaxRootAsync(cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("The code-action document has no syntax root.");
        if (rootNode is not CompilationUnitSyntax root)
        {
            return null;
        }

        int position = LspPositionConverter.GetOffset(sourceText, parameters.Range.Start);
        MemberDeclarationSyntax? declaration = FindMovableDeclaration(root, position);
        if (declaration is null ||
            declaration.ContainsDirectives ||
            !HasSiblingMember(declaration))
        {
            return null;
        }

        string typeName = GetTypeName(declaration);
        if (!IsPortableFileName(typeName))
        {
            return null;
        }

        string directory = Path.GetDirectoryName(document.FilePath)
            ?? throw new InvalidOperationException("The source document has no parent directory.");
        string targetPath = Path.Join(directory, typeName + ".cs");
        if (string.Equals(
                document.FilePath,
                targetPath,
                StringComparison.OrdinalIgnoreCase) ||
            HasPortablePathCollision(directory, targetPath) ||
            document.Project.Solution.Projects
                .SelectMany(static project => project.Documents)
                .Any(candidate => string.Equals(
                    candidate.FilePath,
                    targetPath,
                    StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        SyntaxNode? changedRootNode = root.RemoveNode(
            declaration,
            SyntaxRemoveOptions.KeepUnbalancedDirectives);
        if (changedRootNode is not CompilationUnitSyntax changedRoot)
        {
            return null;
        }

        CompilationUnitSyntax targetRoot = CreateTargetRoot(root, declaration);
        Solution changedSolution = document.Project.Solution.WithDocumentSyntaxRoot(
            document.Id,
            changedRoot);
        Document changedDocument = changedSolution.GetDocument(document.Id)
            ?? throw new InvalidOperationException("The changed source document is unavailable.");
        changedDocument = await Formatter.FormatAsync(
            changedDocument,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        changedSolution = changedDocument.Project.Solution;

        var targetDocumentId = DocumentId.CreateNewId(document.Project.Id, debugName: targetPath);
        changedSolution = changedSolution.AddDocument(
            targetDocumentId,
            Path.GetFileName(targetPath),
            targetRoot,
            filePath: targetPath);
        Document targetDocument = changedSolution.GetDocument(targetDocumentId)
            ?? throw new InvalidOperationException("The moved type document is unavailable.");
        targetDocument = await Formatter.FormatAsync(
            targetDocument,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        WorkspaceEdit edit = await createWorkspaceEditAsync(
            document.Project.Solution,
            targetDocument.Project.Solution,
            cancellationToken).ConfigureAwait(false);
        return edit.DocumentChanges.Count == 0
            ? null
            : new LspCodeAction
            {
                Title = $"Move {typeName} to {typeName}.cs",
                Kind = RefactorCodeActionKind,
                IsPreferred = true,
                Edit = edit
            };
    }

    private static MemberDeclarationSyntax? FindMovableDeclaration(
        CompilationUnitSyntax root,
        int position)
    {
        SyntaxToken token = root.FindToken(position, findInsideTrivia: true);
        return token.Parent?
            .AncestorsAndSelf()
            .OfType<MemberDeclarationSyntax>()
            .FirstOrDefault(static declaration =>
                declaration is BaseTypeDeclarationSyntax or DelegateDeclarationSyntax &&
                declaration.Parent is CompilationUnitSyntax or BaseNamespaceDeclarationSyntax);
    }

    private static bool HasSiblingMember(MemberDeclarationSyntax declaration) =>
        declaration.Parent switch
        {
            CompilationUnitSyntax compilationUnit => compilationUnit.Members.Count > 1,
            BaseNamespaceDeclarationSyntax namespaceDeclaration =>
                namespaceDeclaration.Members.Count > 1,
            _ => false
        };

    private static string GetTypeName(MemberDeclarationSyntax declaration) =>
        declaration switch
        {
            BaseTypeDeclarationSyntax typeDeclaration =>
                typeDeclaration.Identifier.ValueText,
            DelegateDeclarationSyntax delegateDeclaration =>
                delegateDeclaration.Identifier.ValueText,
            _ => throw new InvalidOperationException("The declaration is not a named C# type.")
        };

    private static CompilationUnitSyntax CreateTargetRoot(
        CompilationUnitSyntax root,
        MemberDeclarationSyntax declaration)
    {
        MemberDeclarationSyntax targetMember = declaration;
        for (SyntaxNode? parent = declaration.Parent;
            parent is BaseNamespaceDeclarationSyntax namespaceDeclaration;
            parent = parent.Parent)
        {
            targetMember = namespaceDeclaration.WithMembers([targetMember]);
        }

        return root
            .WithAttributeLists([])
            .WithMembers([targetMember]);
    }

    private static bool IsGeneratedDocument(SourceText text)
    {
        int headerLength = Math.Min(text.Length, 2_048);
        return text.ToString(new TextSpan(0, headerLength)).Contains(
            "<auto-generated",
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasPortablePathCollision(string directory, string targetPath) =>
        Directory.EnumerateFileSystemEntries(directory)
            .Any(path => string.Equals(
                path,
                targetPath,
                StringComparison.OrdinalIgnoreCase));

    private static bool IsPortableFileName(string typeName)
    {
        if (typeName.Any(static character =>
            char.IsControl(character) || character is '<' or '>' or ':' or '"' or '/' or
                '\\' or '|' or '?' or '*'))
        {
            return false;
        }

        return !typeName.Equals("CON", StringComparison.OrdinalIgnoreCase) &&
            !typeName.Equals("PRN", StringComparison.OrdinalIgnoreCase) &&
            !typeName.Equals("AUX", StringComparison.OrdinalIgnoreCase) &&
            !typeName.Equals("NUL", StringComparison.OrdinalIgnoreCase) &&
            !IsNumberedDeviceName(typeName, "COM") &&
            !IsNumberedDeviceName(typeName, "LPT");
    }

    private static bool IsNumberedDeviceName(string typeName, string prefix) =>
        typeName.Length == 4 &&
        typeName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
        typeName[3] is >= '1' and <= '9';
}
