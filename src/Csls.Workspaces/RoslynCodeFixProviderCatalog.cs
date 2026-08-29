using System.Composition;
using System.Reflection;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.Host;
using Microsoft.CodeAnalysis.Host.Mef;

namespace Csls.Workspaces;

/// <summary>
/// Discovers the real Roslyn code-fix providers composed into a workspace host.
/// </summary>
internal sealed class RoslynCodeFixProviderCatalog
{
    private readonly FieldInfo _compositionContextField;

    private RoslynCodeFixProviderCatalog(FieldInfo compositionContextField)
    {
        _compositionContextField = compositionContextField;
    }

    /// <summary>
    /// Creates and validates the provider catalog for the installed Roslyn version.
    /// </summary>
    internal static RoslynCodeFixProviderCatalog Create()
    {
        FieldInfo compositionContextField = typeof(MefHostServices)
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .SingleOrDefault(static field =>
                typeof(CompositionContext).IsAssignableFrom(field.FieldType))
            ?? throw new InvalidOperationException(
                "Roslyn's MEF composition context was not found.");
        return new RoslynCodeFixProviderCatalog(compositionContextField);
    }

    /// <summary>
    /// Gets the code-fix providers exported for a source language.
    /// </summary>
    internal IReadOnlyList<CodeFixProvider> GetProviders(
        HostServices hostServices,
        string language)
    {
        if (hostServices is not MefHostServices mefHostServices)
        {
            throw new InvalidOperationException(
                "Roslyn code fixes require a MEF-backed workspace.");
        }

        CompositionContext compositionContext =
            _compositionContextField.GetValue(mefHostServices) as CompositionContext
            ?? throw new InvalidOperationException(
                "Roslyn's MEF composition context is unavailable.");
        return
        [
            .. compositionContext
                .GetExports<CodeFixProvider>()
                .Where(provider => IsProviderForLanguage(provider, language))
                .OrderBy(static provider => provider.GetType().FullName, StringComparer.Ordinal)
        ];
    }

    private static bool IsProviderForLanguage(
        CodeFixProvider provider,
        string language) =>
        provider.GetType()
            .GetCustomAttributes<ExportCodeFixProviderAttribute>(inherit: false)
            .Any(attribute => attribute.Languages.Contains(language, StringComparer.Ordinal));
}
