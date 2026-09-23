using System.Reflection.Metadata;

namespace Csls.Debugger;

/// <summary>
/// Borrows one exact runtime declaration and its metadata for the duration of an instance-field visitor.
/// </summary>
/// <param name="Instance">The object value owned by the visitor's caller.</param>
/// <param name="Type">The constructed declaring runtime type.</param>
/// <param name="Class">The declaring class used to resolve field tokens.</param>
/// <param name="Module">The declaring module.</param>
/// <param name="Metadata">The metadata reader owned by the active visit.</param>
/// <param name="TypeToken">The declaring type definition token.</param>
/// <param name="Depth">The base-type distance from the object's exact type.</param>
internal readonly record struct ManagedRuntimeTypeDeclaration(nint Instance, nint Type, nint Class,
    nint Module, MetadataReader Metadata, uint TypeToken, int Depth);
