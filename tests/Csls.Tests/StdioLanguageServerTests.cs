using System.Diagnostics;
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
