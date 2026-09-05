using System.Reflection.Metadata;

namespace Csls.Debugger;

/// <summary>
/// Describes one metadata signature type with unresolved generic parameters.
/// </summary>
/// <param name="MetadataName">The CLR metadata name, or null for a generic parameter.</param>
/// <param name="AssemblyName">The defining assembly name when metadata identifies one.</param>
/// <param name="GenericTypeParameterIndex">The containing type parameter index, or null.</param>
/// <param name="TypeArguments">The recursively decoded generic arguments.</param>
/// <param name="ArrayShapes">The array kinds and ranks applied from innermost to outermost.</param>
/// <param name="IsValueType">Whether the metadata signature declares a value type.</param>
/// <param name="SourceModule">The borrowed runtime module containing this signature.</param>
/// <param name="DefinitionToken">The exact local type-definition token, or zero for references.</param>
/// <param name="AssemblyReferenceToken">The source module's assembly-reference token, or zero.</param>
/// <param name="GenericMethodParameterIndex">The method parameter index, or null.</param>
/// <param name="PrimitiveType">The intrinsic signature code, independent of similarly named user types.</param>
/// <param name="UnsupportedKind">A decoded shape that cannot be resolved as an ordinary runtime type.</param>
internal sealed record ManagedMetadataTypeSignature(
    string? MetadataName,
    string? AssemblyName,
    int? GenericTypeParameterIndex,
    IReadOnlyList<ManagedMetadataTypeSignature> TypeArguments,
    IReadOnlyList<ManagedMetadataArrayShape> ArrayShapes,
    bool IsValueType,
    nint SourceModule = 0,
    uint DefinitionToken = 0,
    uint AssemblyReferenceToken = 0,
    int? GenericMethodParameterIndex = null,
    PrimitiveTypeCode? PrimitiveType = null,
    string? UnsupportedKind = null);
