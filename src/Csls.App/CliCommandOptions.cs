using System.CommandLine;

namespace Csls.App;

/// <summary>
/// Creates shared command-line options and cross-option validation rules.
/// </summary>
internal static class CliCommandOptions
{
    /// <summary>
    /// Creates an optional positive language-server process selector.
    /// </summary>
    /// <returns>The session option.</returns>
    internal static Option<int?> CreateSessionOption()
    {
        var option = new Option<int?>("--session")
        {
            Description = "Language-server process identifier; inferred when exactly one session is live.",
            HelpName = "pid"
        };
        option.Validators.Add(static result =>
        {
            int? value = result.GetValueOrDefault<int?>();
            if (value is <= 0)
            {
                result.AddError("--session must be a positive process identifier.");
            }
        });
        return option;
    }

    /// <summary>
    /// Creates a validated zero-based document-position option.
    /// </summary>
    /// <param name="name">The option name.</param>
    /// <param name="description">The user-facing description.</param>
    /// <param name="required">Whether callers must specify the option.</param>
    /// <returns>The position option.</returns>
    internal static Option<int> CreatePositionOption(
        string name,
        string description,
        bool required = true)
    {
        var option = new Option<int>(name)
        {
            Description = description,
            HelpName = "number",
            Required = required
        };
        option.Validators.Add(static result =>
        {
            if (result.GetValueOrDefault<int>() < 0)
            {
                result.AddError("Document positions cannot be negative.");
            }
        });
        return option;
    }

    /// <summary>
    /// Creates an optional workspace path selector.
    /// </summary>
    /// <returns>The workspace option.</returns>
    internal static Option<string?> CreateWorkspaceOption() => new("--workspace")
    {
        Description = "Select this workspace or start a transient session when none is live.",
        HelpName = "path"
    };

    /// <summary>
    /// Creates an optional opaque pagination cursor.
    /// </summary>
    /// <returns>The cursor option.</returns>
    internal static Option<string?> CreateCursorOption() => new("--cursor")
    {
        Description = "Opaque continuation cursor returned by the previous JSON result page.",
        HelpName = "cursor"
    };

    /// <summary>
    /// Creates a bounded result-page size option.
    /// </summary>
    /// <returns>The result limit option.</returns>
    internal static Option<int> CreateLimitOption()
    {
        var option = new Option<int>("--limit")
        {
            Description = "Maximum number of result items from 1 through 200.",
            HelpName = "count",
            DefaultValueFactory = static _ => 100
        };
        option.Validators.Add(static result =>
        {
            if (result.GetValueOrDefault<int>() is < 1 or > 200)
            {
                result.AddError("--limit must be between 1 and 200.");
            }
        });
        return option;
    }

    /// <summary>
    /// Rejects simultaneous process and workspace session selection.
    /// </summary>
    /// <param name="command">The command receiving validation.</param>
    /// <param name="sessionOption">The process selector.</param>
    /// <param name="workspaceOption">The workspace selector.</param>
    internal static void AddSessionWorkspaceValidator(
        Command command,
        Option<int?> sessionOption,
        Option<string?> workspaceOption)
    {
        command.Validators.Add(result =>
        {
            if (result.GetValue(sessionOption).HasValue &&
                !string.IsNullOrWhiteSpace(result.GetValue(workspaceOption)))
            {
                result.AddError("Specify --session or --workspace, but not both.");
            }
        });
    }

    /// <summary>
    /// Converts an optional path into the worker protocol representation.
    /// </summary>
    /// <param name="path">The optional path.</param>
    /// <returns>An empty value or an absolute path.</returns>
    internal static string NormalizeWorkspacePath(string? path) =>
        string.IsNullOrWhiteSpace(path) ? string.Empty : Path.GetFullPath(path);
}
