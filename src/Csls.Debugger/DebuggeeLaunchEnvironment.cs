namespace Csls.Debugger;

/// <summary>
/// Combines inherited settings, environment-file assignments, and explicit launch overrides.
/// </summary>
internal static class DebuggeeLaunchEnvironment
{
    /// <summary>
    /// Builds one complete target environment immediately before process creation.
    /// </summary>
    /// <param name="options">The concrete launch options.</param>
    /// <param name="cancellationToken">Cancels environment-file reads.</param>
    /// <returns>The complete target environment.</returns>
    internal static async Task<Dictionary<string, string>> CreateAsync(
        DebuggeeLaunchOptions options, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Dictionary<string, string> environment = DebuggerWorkerEnvironment.CreateTargetEnvironment();
        if (options.EnvironmentFilePath is string file)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(file);
            string path = Path.GetFullPath(file, options.WorkingDirectory);
            Dictionary<string, string> assignments = await DebuggerEnvironmentFile.ReadAsync(path, cancellationToken)
                .ConfigureAwait(false);
            foreach ((string name, string value) in assignments)
            {
                environment[name] = value;
            }
        }

        foreach ((string name, string? value) in options.Environment)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            if (name.Contains('=', StringComparison.Ordinal) || name.Contains('\0', StringComparison.Ordinal) ||
                value?.Contains('\0', StringComparison.Ordinal) == true)
            {
                throw new ArgumentException("A target environment entry contains an invalid name or null character.");
            }

            if (value is null)
            {
                environment.Remove(name);
            }
            else
            {
                environment[name] = value;
            }
        }

        return environment;
    }
}
