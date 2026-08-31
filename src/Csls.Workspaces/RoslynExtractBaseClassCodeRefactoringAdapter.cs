using Csls.Protocol;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeRefactorings;
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
    private const string ProviderTypeName =
        "Microsoft.CodeAnalysis.CSharp.CodeRefactorings.ExtractClass." +
        "CSharpExtractClassCodeRefactoringProvider";

    private static readonly Lazy<(
        ConstructorInfo MemberAnalysisConstructor,
        ConstructorInfo OptionsConstructor,
        ConstructorInfo ActionConstructor,
        MethodInfo FormattingOptionsMethod,
        MethodInfo ImmutableArrayCreateMethod,
        MethodInfo SelectedNodesMethod,
        MethodInfo SelectedClassMethod,
        MethodInfo IsMemberValidMethod)> s_contract =
        new(CreateContract, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// Returns whether a provider is Roslyn's C# Extract Base Class provider.
    /// </summary>
    internal static bool IsProvider(CodeRefactoringProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        return string.Equals(
            provider.GetType().FullName,
            ProviderTypeName,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Gets Roslyn's Extract Base Class action for one applicable type or member selection.
    /// </summary>
    internal static async Task<LspCodeAction?> GetActionAsync(
        Document document,
        TextSpan span,
        CodeRefactoringProvider provider,
        Func<Solution, Solution, CancellationToken, Task<WorkspaceEdit>>
            createWorkspaceEditAsync,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(createWorkspaceEditAsync);
        if (document.Project.Language != LanguageNames.CSharp || !IsProvider(provider))
        {
            return null;
        }

        (
            ConstructorInfo memberAnalysisConstructor,
            ConstructorInfo optionsConstructor,
            ConstructorInfo actionConstructor,
            MethodInfo formattingOptionsMethod,
            MethodInfo immutableArrayCreateMethod,
            MethodInfo selectedNodesMethod,
            MethodInfo selectedClassMethod,
            MethodInfo isMemberValidMethod) = s_contract.Value;
        var context = new CodeRefactoringContext(
            document,
            span,
            static _ => { },
            cancellationToken);
        Task<ImmutableArray<SyntaxNode>> selectedNodesTask =
            selectedNodesMethod.Invoke(provider, [context]) as
                Task<ImmutableArray<SyntaxNode>>
            ?? throw new InvalidOperationException(
                "Roslyn's Extract Base Class member selection returned no task.");
        ImmutableArray<SyntaxNode> selectedNodes = await selectedNodesTask
            .ConfigureAwait(false);
        SemanticModel semanticModel = await document.GetSemanticModelAsync(cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"Roslyn returned no semantic model for {document.Name}.");
        (SyntaxNode Node, ISymbol Symbol)[] selectedMemberPairs =
        [
            .. selectedNodes
                .Select(node => (
                    Node: node,
                    Symbol: semanticModel.GetDeclaredSymbol(node, cancellationToken)))
                .Where(pair => (bool)(isMemberValidMethod.Invoke(
                    null,
                    [pair.Symbol]) ?? false))
                .Select(static pair => (pair.Node, pair.Symbol!))
        ];
        ISymbol[] selectedMembers =
            [.. selectedMemberPairs.Select(static pair => pair.Symbol)];
        ClassDeclarationSyntax? declaration;
        INamedTypeSymbol? selectedType;
        TextSpan actionSpan;
        if (selectedMembers.Length != 0)
        {
            selectedType = selectedMembers[0].ContainingType;
            declaration = selectedMemberPairs[0].Node
                .AncestorsAndSelf()
                .OfType<ClassDeclarationSyntax>()
                .FirstOrDefault();
            if (declaration is null || selectedMembers.Any(member =>
                !SymbolEqualityComparer.Default.Equals(
                    member.ContainingType,
                    selectedType) ||
                member.DeclaringSyntaxReferences.All(reference =>
                    !declaration.FullSpan.Contains(reference.Span))))
            {
                return null;
            }

            actionSpan = TextSpan.FromBounds(
                selectedMemberPairs[0].Node.FullSpan.Start,
                selectedMemberPairs[^1].Node.FullSpan.End);
        }
        else
        {
            Task<SyntaxNode?> selectedClassTask = selectedClassMethod.Invoke(
                provider,
                [context]) as Task<SyntaxNode?>
                ?? throw new InvalidOperationException(
                    "Roslyn's Extract Base Class type selection returned no task.");
            declaration = await selectedClassTask.ConfigureAwait(false) as
                ClassDeclarationSyntax;
            selectedType = declaration is null
                ? null
                : semanticModel.GetDeclaredSymbol(declaration, cancellationToken) as
                    INamedTypeSymbol;
            actionSpan = span;
        }

        if (declaration is null ||
            selectedType is null ||
            selectedType.IsStatic ||
            selectedType.BaseType?.SpecialType != SpecialType.System_Object)
        {
            return null;
        }

        if (selectedMembers.Length == 0)
        {
            selectedMembers =
            [
                .. selectedType.GetMembers().Where(static member => member switch
                {
                    IMethodSymbol method => method.MethodKind == MethodKind.Ordinary,
                    IFieldSymbol field => !field.IsImplicitlyDeclared,
                    _ => member.Kind is Microsoft.CodeAnalysis.SymbolKind.Property or
                        Microsoft.CodeAnalysis.SymbolKind.Event
                })
            ];
        }

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
                actionSpan,
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
        MethodInfo ImmutableArrayCreateMethod,
        MethodInfo SelectedNodesMethod,
        MethodInfo SelectedClassMethod,
        MethodInfo IsMemberValidMethod) CreateContract()
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
        Type providerType = ResolveType(ProviderTypeName);
        Type memberValidatorType = ResolveType(
            "Microsoft.CodeAnalysis.PullMemberUp.MemberAndDestinationValidator");
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
        MethodInfo selectedNodesMethod = providerType.GetMethod(
            "GetSelectedNodesAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                "Roslyn's Extract Base Class member-selection method was not found.");
        MethodInfo selectedClassMethod = providerType.GetMethod(
            "GetSelectedClassDeclarationAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                "Roslyn's Extract Base Class type-selection method was not found.");
        MethodInfo isMemberValidMethod = memberValidatorType.GetMethod(
            "IsMemberValid",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
            [typeof(ISymbol)])
            ?? throw new InvalidOperationException(
                "Roslyn's Extract Base Class member validator was not found.");
        return (
            memberAnalysisConstructor,
            optionsConstructor,
            actionConstructor,
            formattingOptionsMethod,
            immutableArrayCreateMethod,
            selectedNodesMethod,
            selectedClassMethod,
            isMemberValidMethod);
    }
}
