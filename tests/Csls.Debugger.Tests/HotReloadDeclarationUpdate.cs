namespace Csls.Debugger.Tests;

/// <summary>
/// Carries one real compiler update that changes the inspected local declarations.
/// </summary>
/// <param name="Source">The updated source text.</param>
/// <param name="Metadata">The ECMA-335 metadata delta.</param>
/// <param name="Il">The method-body delta.</param>
/// <param name="Pdb">The Portable PDB delta.</param>
/// <param name="Types">The changed aggregate type tokens.</param>
/// <param name="Methods">The changed aggregate method tokens.</param>
internal sealed record HotReloadDeclarationUpdate(
    string Source, byte[] Metadata, byte[] Il, byte[] Pdb, int[] Types, int[] Methods);
