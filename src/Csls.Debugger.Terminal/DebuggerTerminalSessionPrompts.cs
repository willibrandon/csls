using Hex1b;
using System.Globalization;
using System.Text.Json;

namespace Csls.Debugger.Terminal;

/// <summary>
/// Collects explicit target selections for the terminal-owned session browser.
/// </summary>
internal static class DebuggerTerminalSessionPrompts
{
    /// <summary>
    /// Opens a versioned browser whose rows refer only to terminal-owned sessions.
    /// </summary>
    internal static void OpenBrowser(
        WindowManager windows,
        DebuggerTerminalSessions sessions,
        CancellationToken cancellationToken)
    {
        (long version, IReadOnlyList<DebuggerTerminalSessionOption> options) = sessions.CaptureBrowser();
        windows.Window(window => window.SelectionPrompt(options)
            .ItemText(static option => option.Label)
            .FilterText(static option => option.Label)
            .Prompt("Session:")
            .MaxVisibleItems(8)
            .OnSelected(async option =>
            {
                window.Window.CloseWithResult(option);
                await RunAsync(async () =>
                {
                    _ = await sessions.SelectAsync(option.Id, version, cancellationToken)
                        .ConfigureAwait(false);
                }, sessions, cancellationToken).ConfigureAwait(false);
            }))
            .Title("Terminal sessions")
            .Size(80, 14)
            .Modal()
            .Open(windows);
    }

    /// <summary>
    /// Prompts for an absolute managed program and its ordered arguments.
    /// </summary>
    internal static void OpenLaunch(
        WindowManager windows,
        DebuggerTerminalSessions sessions,
        CancellationToken cancellationToken)
    {
        string program = string.Empty;
        string argumentsJson = "[]";
        windows.Window(window => window.VStack(vertical =>
        [
            vertical.Text(""),
            vertical.Text("  Absolute .NET assembly or apphost path; stops at entry."),
            vertical.TextBox(program)
                .OnTextChanged(change => program = change.NewText)
                .OnSubmit(async _ =>
                {
                    window.Window.CloseWithResult(program);
                    await LaunchAsync(program, argumentsJson, sessions, cancellationToken)
                        .ConfigureAwait(false);
                }),
            vertical.Text(""),
            vertical.Text("  Ordered arguments as a JSON string array, for example [\"--verbose\"]."),
            vertical.TextBox(argumentsJson)
                .OnTextChanged(change => argumentsJson = change.NewText)
                .OnSubmit(async _ =>
                {
                    window.Window.CloseWithResult(program);
                    await LaunchAsync(program, argumentsJson, sessions, cancellationToken)
                        .ConfigureAwait(false);
                }),
            vertical.Text(""),
            vertical.Button("Launch").OnClick(async _ =>
                {
                    window.Window.CloseWithResult(program);
                    await LaunchAsync(program, argumentsJson, sessions, cancellationToken)
                        .ConfigureAwait(false);
                }),
            vertical.Text("  Enter Launch  Escape Cancel")
        ]))
            .Title("Launch managed target")
            .Size(80, 12)
            .Modal()
            .Open(windows);
    }

    /// <summary>
    /// Prompts for a positive process identifier to attach independently.
    /// </summary>
    internal static void OpenAttach(
        WindowManager windows,
        DebuggerTerminalSessions sessions,
        CancellationToken cancellationToken)
    {
        string processIdText = string.Empty;
        windows.Window(window => window.VStack(vertical =>
        [
            vertical.Text(""),
            vertical.Text("  Process ID of a running managed target."),
            vertical.TextBox(processIdText)
                .OnTextChanged(change => processIdText = change.NewText)
                .OnSubmit(async _ =>
                {
                    window.Window.CloseWithResult(processIdText);
                    if (!int.TryParse(processIdText, NumberStyles.None,
                            CultureInfo.InvariantCulture, out int processId) || processId <= 0)
                    {
                        sessions.ReportError("Enter a positive process ID.");
                        return;
                    }

                    await RunAsync(() => sessions.AddAttachAsync(
                        new DebuggerTerminalAttachOptions(processId), cancellationToken),
                        sessions, cancellationToken).ConfigureAwait(false);
                }),
            vertical.Text(""),
            vertical.Text("  Enter Attach  Escape Cancel")
        ]))
            .Title("Attach managed process")
            .Size(72, 8)
            .Modal()
            .Open(windows);
    }

    private static Task LaunchAsync(
        string program,
        string argumentsJson,
        DebuggerTerminalSessions sessions,
        CancellationToken cancellationToken)
        => RunAsync(async () =>
        {
            IReadOnlyList<string> arguments = ParseArguments(argumentsJson);
            await sessions.AddLaunchAsync(new DebuggerTerminalLaunchOptions
            {
                Program = program,
                WorkingDirectory = Path.GetDirectoryName(program) ?? string.Empty,
                Arguments = arguments,
                StopAtEntry = true
            }, cancellationToken).ConfigureAwait(false);
        }, sessions, cancellationToken);

    private static List<string> ParseArguments(string text)
    {
        if (text.Length > 65536)
        {
            throw new ArgumentException("The argument list is too large.", nameof(text));
        }

        using var document = JsonDocument.Parse(text);
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() > 64)
        {
            throw new ArgumentException("Arguments must be a JSON array of at most 64 strings.", nameof(text));
        }

        var arguments = new List<string>(root.GetArrayLength());
        foreach (JsonElement element in root.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.String || element.GetString() is not string argument)
            {
                throw new ArgumentException("Every launch argument must be a string.", nameof(text));
            }

            arguments.Add(argument);
        }

        return arguments;
    }

    private static async Task RunAsync(
        Func<Task> action,
        DebuggerTerminalSessions sessions,
        CancellationToken cancellationToken)
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception) when (exception is
            ArgumentException or
            IOException or
            InvalidDataException or
            InvalidOperationException or
            JsonException or
            ObjectDisposedException or
            StreamJsonRpc.RemoteInvocationException)
        {
            sessions.ReportError(exception.Message);
        }
    }
}
