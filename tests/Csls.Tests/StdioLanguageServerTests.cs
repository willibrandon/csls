using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Csls.Protocol;
using StreamJsonRpc;

namespace Csls.Tests;

/// <summary>
/// Verifies the production worker through a real out-of-process stdio LSP session.
/// </summary>
[TestClass]
public sealed class StdioLanguageServerTests
{
    /// <summary>
    /// Gets the active MSTest context and its framework-managed cancellation token.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Initializes a real project, opens a document, and resolves Roslyn hover information.
    /// </summary>
    [TestMethod]
    public async Task WorkerServesHoverAndShutsDownCleanly()
    {
        string repositoryRoot = FindRepositoryRoot();
        string workerPath = Path.Combine(
            repositoryRoot,
            "artifacts",
            "bin",
            "Csls.Worker",
            "debug",
            "csls-worker.dll");
        Assert.IsTrue(File.Exists(workerPath), $"Worker not found at {workerPath}.");

        string workspacePath = Path.Combine(
            Path.GetTempPath(),
            $"csls-lsp-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workspacePath);
        try
        {
            string projectPath = Path.Combine(workspacePath, "Fixture.csproj");
            string documentPath = Path.Combine(workspacePath, "Program.cs");
            const string ProjectText = """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <ImplicitUsings>enable</ImplicitUsings>
                  </PropertyGroup>
                </Project>
                """;
            const string DocumentText = """
                namespace Fixture;

                public static class Program
                {
                    public static void Main()
                    {
                        Console.WriteLine("hello");
                    }
                }
                """;
            await File.WriteAllTextAsync(
                projectPath,
                ProjectText,
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                documentPath,
                DocumentText,
                TestContext.CancellationToken).ConfigureAwait(false);

            var startInfo = new ProcessStartInfo
            {
                FileName = ResolveDotNetHost(),
                RedirectStandardError = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                UseShellExecute = false
            };
            startInfo.ArgumentList.Add(workerPath);
            using Process process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("The csls worker did not start.");
            Task<string> standardErrorTask = process.StandardError.ReadToEndAsync(
                TestContext.CancellationToken);
            using var formatter = new SystemTextJsonFormatter
            {
                JsonSerializerOptions = LspJson.CreateSerializerOptions()
            };
            using var messageHandler = new HeaderDelimitedMessageHandler(
                process.StandardInput.BaseStream,
                process.StandardOutput.BaseStream,
                formatter);
            using var rpc = new JsonRpc(messageHandler)
            {
                CancelLocallyInvokedMethodsWhenConnectionIsClosed = true,
                DisplayName = "csls-tests"
            };
            rpc.StartListening();

            using var capabilities = JsonDocument.Parse("{}");
            InitializeResult initializeResult = await rpc
                .InvokeWithParameterObjectAsync<InitializeResult>(
                    "initialize",
                    new InitializeParams
                    {
                        ProcessId = Environment.ProcessId,
                        ClientInfo = new ClientInfo { Name = "Csls.Tests" },
                        RootUri = DocumentUri.FromFileSystemPath(workspacePath),
                        Capabilities = capabilities.RootElement
                    },
                    TestContext.CancellationToken)
                .ConfigureAwait(false);
            Assert.AreEqual("csls", initializeResult.ServerInfo.Name);
            Assert.IsTrue(initializeResult.Capabilities.HoverProvider);

            await rpc.NotifyWithParameterObjectAsync(
                "initialized",
                new InitializedParams()).ConfigureAwait(false);
            var documentUri = DocumentUri.FromFileSystemPath(documentPath);
            await rpc.NotifyWithParameterObjectAsync(
                "textDocument/didOpen",
                new DidOpenTextDocumentParams
                {
                    TextDocument = new TextDocumentItem
                    {
                        Uri = documentUri,
                        LanguageId = "csharp",
                        Version = 1,
                        Text = DocumentText
                    }
                }).ConfigureAwait(false);

            Hover? hover = await rpc.InvokeWithParameterObjectAsync<Hover?>(
                "textDocument/hover",
                new TextDocumentPositionParams
                {
                    TextDocument = new TextDocumentIdentifier { Uri = documentUri },
                    Position = new Position(6, 10)
                },
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.IsNotNull(hover);
            Assert.Contains("System.Console", hover.Contents.Value);
            Assert.Contains(
                "Represents the standard input, output, and error streams for console applications.",
                hover.Contents.Value);

            object? shutdownResult = await rpc.InvokeWithCancellationAsync<object?>(
                "shutdown",
                [],
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.IsNull(shutdownResult);
            await rpc.NotifyAsync("exit").ConfigureAwait(false);
            await process.WaitForExitAsync(TestContext.CancellationToken).ConfigureAwait(false);
            ValueTask<string> standardError = new(standardErrorTask);
            string diagnostics = await standardError.ConfigureAwait(false);

            Assert.AreEqual(0, process.ExitCode, diagnostics);
        }
        finally
        {
            Directory.Delete(workspacePath, recursive: true);
        }
    }

    /// <summary>
    /// Accepts the JSON-RPC null parameter form used by standards-compliant LSP shutdown clients.
    /// </summary>
    [TestMethod]
    public async Task WorkerAcceptsNullShutdownParameters()
    {
        string repositoryRoot = FindRepositoryRoot();
        string workerPath = Path.Combine(
            repositoryRoot,
            "artifacts",
            "bin",
            "Csls.Worker",
            "debug",
            "csls-worker.dll");
        Assert.IsTrue(File.Exists(workerPath), $"Worker not found at {workerPath}.");

        string workspacePath = Path.Combine(
            Path.GetTempPath(),
            $"csls-null-params-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workspacePath);
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(workspacePath, "Fixture.csproj"),
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                </Project>
                """,
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                Path.Combine(workspacePath, "Program.cs"),
                """Console.WriteLine("null parameters");""",
                TestContext.CancellationToken).ConfigureAwait(false);

            var startInfo = new ProcessStartInfo
            {
                FileName = ResolveDotNetHost(),
                WorkingDirectory = workspacePath,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                UseShellExecute = false
            };
            startInfo.ArgumentList.Add(workerPath);
            using Process process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("The csls worker did not start.");
            Task<string> standardErrorTask = process.StandardError.ReadToEndAsync(
                TestContext.CancellationToken);
            try
            {
                string rootUri = new Uri(
                    workspacePath + Path.DirectorySeparatorChar).AbsoluteUri;
                string initializeRequest =
                    "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{" +
                    $"\"processId\":{Environment.ProcessId.ToString(CultureInfo.InvariantCulture)}," +
                    $"\"rootUri\":\"{JsonEncodedText.Encode(rootUri)}\",\"capabilities\":{{}}}}}}";
                await WriteRawMessageAsync(
                    process.StandardInput.BaseStream,
                    initializeRequest,
                    TestContext.CancellationToken).ConfigureAwait(false);
                using var initialize = JsonDocument.Parse(await ReadRawMessageAsync(
                    process.StandardOutput.BaseStream,
                    TestContext.CancellationToken).ConfigureAwait(false));
                Assert.AreEqual(
                    "csls",
                    initialize.RootElement
                        .GetProperty("result")
                        .GetProperty("serverInfo")
                        .GetProperty("name")
                        .GetString());

                await WriteRawMessageAsync(
                    process.StandardInput.BaseStream,
                    """{"jsonrpc":"2.0","method":"initialized","params":{}}""",
                    TestContext.CancellationToken).ConfigureAwait(false);
                await WriteRawMessageAsync(
                    process.StandardInput.BaseStream,
                    """{"jsonrpc":"2.0","id":2,"method":"shutdown","params":null}""",
                    TestContext.CancellationToken).ConfigureAwait(false);
                using var shutdown = JsonDocument.Parse(await ReadRawMessageAsync(
                    process.StandardOutput.BaseStream,
                    TestContext.CancellationToken).ConfigureAwait(false));
                Assert.AreEqual(2, shutdown.RootElement.GetProperty("id").GetInt32());
                Assert.AreEqual(
                    JsonValueKind.Null,
                    shutdown.RootElement.GetProperty("result").ValueKind);

                await WriteRawMessageAsync(
                    process.StandardInput.BaseStream,
                    """{"jsonrpc":"2.0","method":"exit","params":null}""",
                    TestContext.CancellationToken).ConfigureAwait(false);
                process.StandardInput.Close();
                await process.WaitForExitAsync(TestContext.CancellationToken).ConfigureAwait(false);
                string standardError = await standardErrorTask.ConfigureAwait(false);
                Assert.AreEqual(0, process.ExitCode, standardError);
            }
            finally
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            Directory.Delete(workspacePath, recursive: true);
        }
    }

    private static async Task WriteRawMessageAsync(
        Stream stream,
        string json,
        CancellationToken cancellationToken)
    {
        byte[] payload = Encoding.UTF8.GetBytes(json);
        byte[] header = Encoding.ASCII.GetBytes(
            $"Content-Length: {payload.Length.ToString(CultureInfo.InvariantCulture)}\r\n\r\n");
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string> ReadRawMessageAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        var header = new List<byte>(capacity: 128);
        while (header.Count < 8_192)
        {
            byte[] oneByte = new byte[1];
            int read = await stream.ReadAsync(oneByte, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException("The worker closed standard output.");
            }

            header.Add(oneByte[0]);
            int count = header.Count;
            if (count >= 4 &&
                header[count - 4] == '\r' &&
                header[count - 3] == '\n' &&
                header[count - 2] == '\r' &&
                header[count - 1] == '\n')
            {
                string headerText = Encoding.ASCII.GetString([.. header]);
                string contentLengthHeader = headerText
                    .Split("\r\n", StringSplitOptions.RemoveEmptyEntries)
                    .Single(static line => line.StartsWith(
                        "Content-Length:",
                        StringComparison.OrdinalIgnoreCase));
                int contentLength = int.Parse(
                    contentLengthHeader["Content-Length:".Length..].Trim(),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture);
                byte[] payload = new byte[contentLength];
                await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
                return Encoding.UTF8.GetString(payload);
            }
        }

        throw new InvalidDataException("The worker returned oversized LSP headers.");
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Csls.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("The csls repository root was not found.");
    }

    private static string ResolveDotNetHost()
    {
        string? configuredHost = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        return string.IsNullOrWhiteSpace(configuredHost) ? "dotnet" : configuredHost;
    }
}
