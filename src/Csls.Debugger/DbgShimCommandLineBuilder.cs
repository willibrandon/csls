using System.Text;

namespace Csls.Debugger;

/// <summary>
/// Builds the mutable command line consumed by the cross-platform debugger shim.
/// </summary>
internal static class DbgShimCommandLineBuilder
{
    /// <summary>
    /// Builds a command line for one concrete managed program invocation.
    /// </summary>
    /// <param name="options">The validated target launch options.</param>
    /// <returns>A command line with every argument boundary preserved.</returns>
    internal static string Build(DebuggeeLaunchOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        bool managedAssembly = string.Equals(
            Path.GetExtension(options.Program),
            ".dll",
            StringComparison.OrdinalIgnoreCase);
        string executable = managedAssembly
            ? options.RuntimeHostPath ?? "dotnet"
            : options.Program;
        var commandLine = new StringBuilder();
        AppendArgument(commandLine, executable);
        if (managedAssembly)
        {
            commandLine.Append(' ');
            AppendArgument(commandLine, options.Program);
        }

        foreach (string argument in options.Arguments)
        {
            commandLine.Append(' ');
            AppendArgument(commandLine, argument);
        }

        return commandLine.ToString();
    }

    private static void AppendArgument(StringBuilder commandLine, string argument)
    {
        ArgumentNullException.ThrowIfNull(argument);
        if (argument.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Debugger target arguments cannot contain a null character.",
                nameof(argument));
        }

        commandLine.Append('"');
        int backslashes = 0;
        foreach (char character in argument)
        {
            if (character == '\\')
            {
                backslashes++;
                continue;
            }

            if (character == '"')
            {
                commandLine.Append('\\', checked((backslashes * 2) + 1));
                commandLine.Append('"');
                backslashes = 0;
                continue;
            }

            commandLine.Append('\\', backslashes);
            commandLine.Append(character);
            backslashes = 0;
        }

        commandLine.Append('\\', checked(backslashes * 2));
        commandLine.Append('"');
    }
}
