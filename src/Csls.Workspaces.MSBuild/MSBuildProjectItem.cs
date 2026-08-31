namespace Csls.Workspaces;

/// <summary>
/// Carries one evaluated MSBuild item across the build-host process boundary.
/// </summary>
internal sealed class MSBuildProjectItem
{
    /// <summary>
    /// Initializes one evaluated item and its metadata.
    /// </summary>
    /// <param name="evaluatedInclude">The evaluated item include.</param>
    /// <param name="metadata">The evaluated metadata keyed by name.</param>
    public MSBuildProjectItem(
        string evaluatedInclude,
        Dictionary<string, string> metadata)
    {
        ArgumentNullException.ThrowIfNull(evaluatedInclude);
        ArgumentNullException.ThrowIfNull(metadata);
        EvaluatedInclude = evaluatedInclude;
        Metadata = metadata;
    }

    /// <summary>
    /// Gets the evaluated item include.
    /// </summary>
    public string EvaluatedInclude { get; }

    /// <summary>
    /// Gets the evaluated metadata keyed by name.
    /// </summary>
    public Dictionary<string, string> Metadata { get; }

    /// <summary>
    /// Gets one evaluated metadata value or an empty string when it is absent.
    /// </summary>
    /// <param name="name">The metadata name.</param>
    /// <returns>The evaluated metadata value.</returns>
    internal string GetMetadataValue(string name) =>
        Metadata.GetValueOrDefault(name) ?? string.Empty;
}
