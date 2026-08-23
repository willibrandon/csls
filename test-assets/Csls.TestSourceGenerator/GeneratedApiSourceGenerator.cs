using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using System.Text;

namespace Csls.Testing;

/// <summary>
/// Emits one real C# API into projects that load this test generator.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class GeneratedApiSourceGenerator : IIncrementalGenerator
{
    /// <summary>
    /// Registers the generated source emitted during project compilation.
    /// </summary>
    /// <param name="context">The real Roslyn incremental-generator context.</param>
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        context.RegisterPostInitializationOutput(static output => output.AddSource(
            "GeneratedApi.g.cs",
            SourceText.From(Source, Encoding.UTF8)));
    }

    private const string Source = """
        namespace Fixture;

        public static class GeneratedApi
        {
            public const string Message = "generated";
        }
        """;
}
