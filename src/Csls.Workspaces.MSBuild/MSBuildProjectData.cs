using Microsoft.Build.Execution;

namespace Csls.Workspaces;

/// <summary>
/// Carries the evaluated project properties and items needed by Roslyn.
/// </summary>
internal sealed class MSBuildProjectData
{
    private static readonly string[] s_itemNames =
    [
        "AdditionalFiles",
        "Analyzer",
        "Compile",
        "CscCommandLineArgs",
        "EditorConfigFiles",
        "IntermediateAssembly",
        "ProjectReference",
        "ReferencePath"
    ];
    private static readonly string[] s_propertyNames =
    [
        "AssemblyName",
        "CompilerGeneratedFilesOutputPath",
        "RootNamespace",
        "TargetFramework",
        "TargetPath",
        "TargetRefPath"
    ];

    /// <summary>
    /// Initializes one evaluated project state.
    /// </summary>
    /// <param name="properties">The required evaluated properties.</param>
    /// <param name="items">The required evaluated item groups.</param>
    public MSBuildProjectData(
        Dictionary<string, string> properties,
        Dictionary<string, MSBuildProjectItem[]> items)
    {
        ArgumentNullException.ThrowIfNull(properties);
        ArgumentNullException.ThrowIfNull(items);
        Properties = properties;
        Items = items;
    }

    /// <summary>
    /// Gets the evaluated properties keyed by name.
    /// </summary>
    public Dictionary<string, string> Properties { get; }

    /// <summary>
    /// Gets the evaluated item groups keyed by item type.
    /// </summary>
    public Dictionary<string, MSBuildProjectItem[]> Items { get; }

    /// <summary>
    /// Creates the transport state from an MSBuild project instance.
    /// </summary>
    /// <param name="project">The completed design-time project instance.</param>
    /// <returns>The bounded project state required by Roslyn.</returns>
    internal static MSBuildProjectData Create(ProjectInstance project)
    {
        ArgumentNullException.ThrowIfNull(project);
        Dictionary<string, string> properties = s_propertyNames.ToDictionary(
            static name => name,
            project.GetPropertyValue,
            StringComparer.OrdinalIgnoreCase);
        Dictionary<string, MSBuildProjectItem[]> items = s_itemNames.ToDictionary(
            static name => name,
            name => project.GetItems(name)
                .Select(static item => new MSBuildProjectItem(
                    item.EvaluatedInclude,
                    item.Metadata.ToDictionary(
                        static metadata => metadata.Name,
                        static metadata => metadata.EvaluatedValue,
                        StringComparer.OrdinalIgnoreCase)))
                .ToArray(),
            StringComparer.OrdinalIgnoreCase);
        return new MSBuildProjectData(properties, items);
    }

    /// <summary>
    /// Gets one evaluated property or an empty string when it is absent.
    /// </summary>
    /// <param name="name">The property name.</param>
    /// <returns>The evaluated property value.</returns>
    internal string GetPropertyValue(string name) =>
        Properties.GetValueOrDefault(name) ?? string.Empty;

    /// <summary>
    /// Gets one evaluated item group or an empty collection when it is absent.
    /// </summary>
    /// <param name="name">The item type.</param>
    /// <returns>The evaluated items.</returns>
    internal IReadOnlyList<MSBuildProjectItem> GetItems(string name) =>
        Items.GetValueOrDefault(name) ?? [];
}
