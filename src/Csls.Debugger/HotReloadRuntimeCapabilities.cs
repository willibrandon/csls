namespace Csls.Debugger;

/// <summary>
/// Maps CoreCLR product generations to compiler-facing Hot Reload capabilities.
/// </summary>
internal static class HotReloadRuntimeCapabilities
{
    private static readonly string[] s_net5Capabilities =
    [
        "Baseline",
        "AddMethodToExistingType",
        "AddStaticFieldToExistingType",
        "AddInstanceFieldToExistingType",
        "NewTypeDefinition",
        "AddExplicitInterfaceImplementation"
    ];

    private static readonly string[] s_net6Capabilities =
    [
        .. s_net5Capabilities,
        "ChangeCustomAttributes",
        "UpdateParameters"
    ];

    private static readonly string[] s_net8Capabilities =
    [
        .. s_net6Capabilities,
        "GenericAddMethodToExistingType",
        "GenericUpdateMethod",
        "GenericAddFieldToExistingType"
    ];

    private static readonly string[] s_net10Capabilities =
    [
        .. s_net8Capabilities,
        "AddFieldRva"
    ];

    /// <summary>
    /// Gets the compiler capability names supported by one exact CoreCLR generation.
    /// </summary>
    /// <param name="runtimeVersion">The version returned by ICorDebugProcess2.</param>
    /// <returns>An immutable-by-contract ordered capability snapshot.</returns>
    internal static IReadOnlyList<string> Get(Version? runtimeVersion)
    {
        if (runtimeVersion is null || runtimeVersion.Major < 5)
        {
            return [];
        }

        string[] capabilities = runtimeVersion.Major switch
        {
            >= 10 => s_net10Capabilities,
            >= 8 => s_net8Capabilities,
            >= 6 => s_net6Capabilities,
            _ => s_net5Capabilities
        };
        return Array.AsReadOnly(capabilities);
    }
}
