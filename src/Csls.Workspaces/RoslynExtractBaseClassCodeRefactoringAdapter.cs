using Csls.Protocol;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.Text;
using System.Collections.Immutable;
using System.Reflection;
using LspCodeAction = Csls.Protocol.CodeAction;

namespace Csls.Workspaces;

/// <summary>
/// Applies Roslyn's option-bearing Extract Base Class refactoring with its LSP defaults.
/// </summary>
internal static class RoslynExtractBaseClassCodeRefactoringAdapter
{
    private const string RefactorCodeActionKind = "refactor";

    private static readonly Lazy<(
        ConstructorInfo MemberAnalysisConstructor,
        ConstructorInfo OptionsConstructor,
        ConstructorInfo ActionConstructor,
        MethodInfo FormattingOptionsMethod,
        MethodInfo ImmutableArrayCreateMethod)> s_contract =
        new(CreateContract, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// Gets Roslyn's Extract Base Class action for one applicable class declaration.
    /// </summary>
    internal static async Task<LspCodeAction?> GetActionAsync(
        Document document,
        TextSpan span,
        Func<Solution, Solution, CancellationToken, Task<WorkspaceEdit>>
            createWorkspaceEditAsync,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(createWorkspaceEditAsync);
        if (document.Project.Language != LanguageNames.CSharp)
        {
            return null;
        }

        SyntaxNode root = await document.GetSyntaxRootAsync(cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"Roslyn returned no syntax root for {document.Name}.");
        int tokenPosition = Math.Min(span.Start, Math.Max(0, root.FullSpan.End - 1));
        ClassDeclarationSyntax? declaration = root
            .FindToken(tokenPosition, findInsideTrivia: true)
            .Parent?
            .AncestorsAndSelf()
            .OfType<ClassDeclarationSyntax>()
            .FirstOrDefault();
        if (declaration is null)
        {
            return null;
        }

        SemanticModel semanticModel = await document.GetSemanticModelAsync(cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"Roslyn returned no semantic model for {document.Name}.");
        if (semanticModel.GetDeclaredSymbol(declaration, cancellationToken) is not
                INamedTypeSymbol selectedType)
        {
            return null;
        }

        if (selectedType.BaseType?.SpecialType != SpecialType.System_Object)
        {
            return null;
        }

        ISymbol[] selectedMembers =
        [
            .. selectedType.GetMembers().Where(static member => member switch
            {
                IMethodSymbol method => method.MethodKind == MethodKind.Ordinary,
                IFieldSymbol field => !field.IsImplicitlyDeclared,
                _ => member.Kind is Microsoft.CodeAnalysis.SymbolKind.Property or
                    Microsoft.CodeAnalysis.SymbolKind.Event
            })
        ];
        if (selectedMembers.Length == 0)
        {
            return null;
        }

        (
            ConstructorInfo memberAnalysisConstructor,
            ConstructorInfo optionsConstructor,
            ConstructorInfo actionConstructor,
            MethodInfo formattingOptionsMethod,
            MethodInfo immutableArrayCreateMethod) = s_contract.Value;
        Type memberAnalysisType = memberAnalysisConstructor.DeclaringType
            ?? throw new InvalidOperationException(
                "Roslyn's extract-class member-analysis type is unavailable.");
        var memberAnalysisResults = Array.CreateInstance(
            memberAnalysisType,
            selectedMembers.Length);
        for (int index = 0; index < selectedMembers.Length; index++)
        {
            memberAnalysisResults.SetValue(
                memberAnalysisConstructor.Invoke([selectedMembers[index], false]),
                index);
        }

        object immutableMemberAnalysisResults = immutableArrayCreateMethod
            .MakeGenericMethod(memberAnalysisType)
            .Invoke(null, [memberAnalysisResults])
            ?? throw new InvalidOperationException(
                "Roslyn's extract-class member analysis could not be materialized.");
        object options = optionsConstructor.Invoke(
            ["NewBaseType.cs", "NewBaseType", true, immutableMemberAnalysisResults]);
        object formattingOptionsValueTask = formattingOptionsMethod.Invoke(
            null,
            [document, cancellationToken])
            ?? throw new InvalidOperationException(
                "Roslyn's syntax-formatting options request returned no result.");
        Task formattingOptionsTask = formattingOptionsValueTask
            .GetType()
            .GetMethod(nameof(ValueTask<>.AsTask), BindingFlags.Instance | BindingFlags.Public)?
            .Invoke(formattingOptionsValueTask, null) as Task
            ?? throw new InvalidOperationException(
                "Roslyn's syntax-formatting options task is unavailable.");
        await formattingOptionsTask.ConfigureAwait(false);
        object formattingOptions = formattingOptionsTask
            .GetType()
            .GetProperty("Result", BindingFlags.Instance | BindingFlags.Public)?
            .GetValue(formattingOptionsTask)
            ?? throw new InvalidOperationException(
                "Roslyn's syntax-formatting options were unavailable.");

        CodeActionWithOptions action = actionConstructor.Invoke(
            [
                document,
                span,
                null,
                selectedType,
                declaration,
                ImmutableArray<ISymbol>.Empty,
                formattingOptions
            ]) as CodeActionWithOptions
            ?? throw new InvalidOperationException(
                "Roslyn's Extract Base Class action could not be created.");
        IEnumerable<CodeActionOperation>? operations = await action
            .GetOperationsAsync(options, cancellationToken)
            .ConfigureAwait(false);
        ApplyChangesOperation[] applyChanges =
            [.. (operations ?? []).OfType<ApplyChangesOperation>()];
        if (applyChanges.Length != 1)
        {
            return null;
        }

        WorkspaceEdit edit = await createWorkspaceEditAsync(
            document.Project.Solution,
            applyChanges[0].ChangedSolution,
            cancellationToken).ConfigureAwait(false);
        return edit.DocumentChanges.Count == 0
            ? null
            : new LspCodeAction
            {
                Title = action.Title,
                Kind = RefactorCodeActionKind,
                Edit = edit
            };
    }

    private static (
        ConstructorInfo MemberAnalysisConstructor,
        ConstructorInfo OptionsConstructor,
        ConstructorInfo ActionConstructor,
        MethodInfo FormattingOptionsMethod,
        MethodInfo ImmutableArrayCreateMethod) CreateContract()
    {
        Assembly[] assemblies =
        [
            .. MefHostServices.DefaultAssemblies
                .Append(typeof(CodeActionWithOptions).Assembly)
                .DistinctBy(static assembly => assembly.FullName)
        ];
        Type ResolveType(string fullName) => assemblies
            .Select(assembly => assembly.GetType(fullName, throwOnError: false))
            .SingleOrDefault(static type => type is not null)
            ?? throw new InvalidOperationException(
                $"Roslyn type {fullName} was not found.");

        const BindingFlags constructorFlags =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        Type memberAnalysisType = ResolveType(
            "Microsoft.CodeAnalysis.ExtractClass.ExtractClassMemberAnalysisResult");
        Type optionsType = ResolveType(
            "Microsoft.CodeAnalysis.ExtractClass.ExtractClassOptions");
        Type actionType = ResolveType(
            "Microsoft.CodeAnalysis.ExtractClass.ExtractClassWithDialogCodeAction");
        Type formattingOptionsProviderType = ResolveType(
            "Microsoft.CodeAnalysis.Formatting.SyntaxFormattingOptionsProviders");
        ConstructorInfo memberAnalysisConstructor = memberAnalysisType
            .GetConstructors(constructorFlags)
            .Single(static constructor => constructor.GetParameters().Length == 2);
        ConstructorInfo optionsConstructor = optionsType
            .GetConstructors(constructorFlags)
            .Single(static constructor => constructor.GetParameters().Length == 4);
        ConstructorInfo actionConstructor = actionType
            .GetConstructors(constructorFlags)
            .Single(static constructor => constructor.GetParameters().Length == 7);
        MethodInfo formattingOptionsMethod = formattingOptionsProviderType.GetMethod(
            "GetSyntaxFormattingOptionsAsync",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
            [typeof(Document), typeof(CancellationToken)])
            ?? throw new InvalidOperationException(
                "Roslyn's syntax-formatting options method was not found.");
        MethodInfo immutableArrayCreateMethod = typeof(ImmutableArray)
            .GetMethods(BindingFlags.Static | BindingFlags.Public)
            .Single(static method =>
                method.Name == nameof(ImmutableArray.Create) &&
                method.IsGenericMethodDefinition &&
                method.GetGenericArguments().Length == 1 &&
                method.GetParameters() is [{ ParameterType.IsArray: true }]);
        return (
            memberAnalysisConstructor,
            optionsConstructor,
            actionConstructor,
            formattingOptionsMethod,
            immutableArrayCreateMethod);
    }
}
