using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;

namespace Csls.Web.Worker;

/// <summary>
/// Hosts the production csls language server inside a browser Web Worker.
/// </summary>
[SupportedOSPlatform("browser")]
internal static partial class BrowserLanguageServerHost
{
    private const string ReferenceRoot = "/references";
    private const string WorkspaceRoot = "/workspace";
    private static readonly Lock s_gate = new();
    private static BrowserLanguageServerSession? s_session;

    /// <summary>
    /// Writes one synchronized workspace file into the browser virtual filesystem.
    /// </summary>
    /// <param name="path">The absolute virtual path under the workspace root.</param>
    /// <param name="content">The complete UTF-16 text file content.</param>
    [JSExport]
    internal static void SynchronizeFile(string path, string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        string normalizedPath = ValidateWorkspacePath(path);
        string directoryPath = Path.GetDirectoryName(normalizedPath)
            ?? throw new InvalidDataException($"The workspace file has no parent: {path}");
        Directory.CreateDirectory(directoryPath);
        File.WriteAllText(normalizedPath, content);
    }

    /// <summary>
    /// Creates one synchronized workspace directory.
    /// </summary>
    /// <param name="path">The absolute virtual path under the workspace root.</param>
    [JSExport]
    internal static void SynchronizeDirectory(string path)
    {
        Directory.CreateDirectory(ValidateWorkspacePath(path));
    }

    /// <summary>
    /// Deletes one synchronized workspace file or directory.
    /// </summary>
    /// <param name="path">The absolute virtual path under the workspace root.</param>
    [JSExport]
    internal static void DeletePath(string path)
    {
        string normalizedPath = ValidateWorkspacePath(path);
        if (File.Exists(normalizedPath))
        {
            File.Delete(normalizedPath);
        }
        else if (Directory.Exists(normalizedPath))
        {
            Directory.Delete(normalizedPath, recursive: true);
        }
    }

    /// <summary>
    /// Writes one runtime assembly used as Roslyn compilation metadata.
    /// </summary>
    /// <param name="fileName">The assembly file name.</param>
    /// <param name="content">The complete portable executable image.</param>
    [JSExport]
    internal static void SynchronizeReference(string fileName, byte[] content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(content);
        if (!string.Equals(fileName, Path.GetFileName(fileName), StringComparison.Ordinal) ||
            !string.Equals(Path.GetExtension(fileName), ".dll", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Invalid browser reference name: {fileName}");
        }

        Directory.CreateDirectory(ReferenceRoot);
        File.WriteAllBytes(Path.Join(ReferenceRoot, fileName), content);
    }

    /// <summary>
    /// Starts the browser LSP session after the workspace snapshot is synchronized.
    /// </summary>
    [JSExport]
    internal static void Start()
    {
        lock (s_gate)
        {
            if (s_session is not null)
            {
                throw new InvalidOperationException("The browser language server is already running.");
            }

            s_session = new BrowserLanguageServerSession(SendMessageAsync);
        }
    }

    /// <summary>
    /// Delivers one complete LSP JSON message from the browser language client.
    /// </summary>
    /// <param name="method">The request or notification method, when present.</param>
    /// <param name="requestId">The serialized request identifier, when present.</param>
    /// <param name="parameterObject">The JavaScript parameter object, when present.</param>
    /// <param name="parameters">The serialized parameters, when present.</param>
    /// <param name="result">The serialized response result, when present.</param>
    /// <param name="error">The serialized response error, when present.</param>
    /// <returns>A task that completes after the bounded transport accepts the message.</returns>
    [JSExport]
    internal static Task ReceiveAsync(
        string? method,
        string? requestId,
        JSObject? parameterObject,
        string? parameters,
        string? result,
        string? error)
    {
        BrowserLanguageServerSession session = Volatile.Read(ref s_session)
            ?? throw new InvalidOperationException("The browser language server is not running.");
        return session.ReceiveAsync(
            method,
            requestId,
            parameterObject,
            parameters,
            result,
            error).AsTask();
    }

    /// <summary>
    /// Stops the browser session and waits for all server resources to be released.
    /// </summary>
    /// <returns>A task that completes after the RPC session stops.</returns>
    [JSExport]
    internal static async Task StopAsync()
    {
        BrowserLanguageServerSession? session;
        lock (s_gate)
        {
            session = s_session;
            s_session = null;
        }

        if (session is null)
        {
            return;
        }

        await session.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Sends one serialized LSP response or notification to the browser client.
    /// </summary>
    /// <param name="message">The complete JSON-RPC message.</param>
    [JSImport("transport.send", "cslsBrowserWorker.js")]
    internal static partial void SendMessage(string message);

    /// <summary>
    /// Sends one serialized successful response to the browser client.
    /// </summary>
    /// <param name="requestId">The serialized JSON-RPC request identifier.</param>
    /// <param name="result">The serialized response result.</param>
    [JSImport("transport.sendResult", "cslsBrowserWorker.js")]
    internal static partial void SendResult(string requestId, string result);

    /// <summary>
    /// Sends the server initialization response to the browser client.
    /// </summary>
    /// <param name="requestId">The serialized JSON-RPC request identifier.</param>
    /// <param name="supportsRefactor">Whether file-creating refactors were negotiated.</param>
    /// <param name="version">The server assembly version, when available.</param>
    [JSImport("transport.sendInitializeResult", "cslsBrowserWorker.js")]
    internal static partial void SendInitializeResult(
        string requestId,
        bool supportsRefactor,
        string? version);

    /// <summary>
    /// Sends one optional hover response to the browser client.
    /// </summary>
    /// <param name="requestId">The serialized JSON-RPC request identifier.</param>
    /// <param name="hasHover">Whether the server returned hover content.</param>
    /// <param name="kind">The markup kind when hover content is present.</param>
    /// <param name="value">The markup value when hover content is present.</param>
    /// <param name="hasRange">Whether the hover includes a source range.</param>
    /// <param name="startLine">The range start line.</param>
    /// <param name="startCharacter">The range start character.</param>
    /// <param name="endLine">The range end line.</param>
    /// <param name="endCharacter">The range end character.</param>
    [JSImport("transport.sendHoverResult", "cslsBrowserWorker.js")]
    internal static partial void SendHoverResult(
        string requestId,
        bool hasHover,
        string? kind,
        string? value,
        bool hasRange,
        int startLine,
        int startCharacter,
        int endLine,
        int endCharacter);

    /// <summary>
    /// Reports one managed startup stage to the browser extension host.
    /// </summary>
    /// <param name="stage">The stable startup stage name.</param>
    [JSImport("status.report", "cslsBrowserWorker.js")]
    internal static partial void ReportStatus(string stage);

    private static ValueTask SendMessageAsync(
        string message,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SendMessage(message);
        return ValueTask.CompletedTask;
    }

    private static string ValidateWorkspacePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string normalizedRoot = Path.GetFullPath(WorkspaceRoot);
        string normalizedPath = Path.GetFullPath(path);
        if (!normalizedPath.StartsWith(
            normalizedRoot + Path.DirectorySeparatorChar,
            StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"The browser workspace path is outside {WorkspaceRoot}: {path}");
        }

        return normalizedPath;
    }
}
