using Microsoft.CodeAnalysis.CodeRefactorings;
using Microsoft.CodeAnalysis.Host;
using Microsoft.CodeAnalysis.Host.Mef;
using System.Composition;
using System.Reflection;

namespace Csls.Workspaces;

/// <summary>
/// Discovers the real Roslyn code-refactoring providers composed into a workspace host.
/// </summary>
internal sealed class RoslynCodeRefactoringProviderCatalog
{
    private readonly FieldInfo _compositionContextField;

    private RoslynCodeRefactoringProviderCatalog(FieldInfo compositionContextField)
    {
        _compositionContextField = compositionContextField;
    }

    /// <summary>
    /// Creates and validates the provider catalog for the installed Roslyn version.
    /// </summary>
    internal static RoslynCodeRefactoringProviderCatalog Create()
    {
        FieldInfo compositionContextField = typeof(MefHostServices)
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .SingleOrDefault(static field =>
                typeof(CompositionContext).IsAssignableFrom(field.FieldType))
            ?? throw new InvalidOperationException(
                "Roslyn's MEF composition context was not found.");
        return new RoslynCodeRefactoringProviderCatalog(compositionContextField);
    }

    /// <summary>
    /// Gets the code-refactoring providers exported for a source language.
    /// </summary>
    internal IReadOnlyList<CodeRefactoringProvider> GetProviders(
        HostServices hostServices,
        string language)
    {
        if (hostServices is not MefHostServices mefHostServices)
        {
            throw new InvalidOperationException(
                "Roslyn code refactorings require a MEF-backed workspace.");
        }

        CompositionContext compositionContext =
            _compositionContextField.GetValue(mefHostServices) as CompositionContext
            ?? throw new InvalidOperationException(
                "Roslyn's MEF composition context is unavailable.");
        return
        [
            .. compositionContext
                .GetExports<CodeRefactoringProvider>()
                .Where(provider => IsProviderForLanguage(provider, language))
                .OrderBy(static provider => provider.GetType().FullName, StringComparer.Ordinal)
        ];
    }

    private static bool IsProviderForLanguage(
        CodeRefactoringProvider provider,
        string language) =>
        provider.GetType()
            .GetCustomAttributes<ExportCodeRefactoringProviderAttribute>(inherit: false)
            .Any(attribute => attribute.Languages.Contains(language, StringComparer.Ordinal));
}
