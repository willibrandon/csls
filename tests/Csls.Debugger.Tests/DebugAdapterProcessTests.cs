using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies the shipping debugger command through real standard streams and a process boundary.
/// </summary>
[TestClass]
public sealed class DebugAdapterProcessTests
{
    /// <summary>
    /// Gets the active MSTest context and its framework-managed cancellation token.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Initializes and disconnects the debugger command without contaminating protocol output.
    /// </summary>
    [TestMethod]
    public async Task DebuggerCommandKeepsStandardOutputProtocolOnly()
    {
        string repositoryRoot = FindRepositoryRoot();
        string applicationPath = Path.Join(
            repositoryRoot,
            "artifacts",
            "bin",
            "Csls.App",
            "debug",
            "csls.dll");
        var startInfo = new ProcessStartInfo
        {
            FileName = ResolveDotNetHost(),
            WorkingDirectory = repositoryRoot,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add(applicationPath);
        startInfo.ArgumentList.Add("debugger");
        startInfo.ArgumentList.Add("dap");
        startInfo.Environment["CSLS_DEBUGGER_WORKER_PATH"] = Path.Join(
            repositoryRoot,
            "artifacts",
            "bin",
            "Csls.Debugger.Worker",
            "debug",
            "csls-debugger-worker.dll");
        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The csls debugger command did not start.");
        Task<string> standardErrorTask = process.StandardError.ReadToEndAsync(
            TestContext.CancellationToken);
        try
        {
            await WriteRequestAsync(
                process.StandardInput.BaseStream,
                sequence: 1,
                "initialize",
                TestContext.CancellationToken).ConfigureAwait(false);
            using JsonDocument initialize = await ReadMessageAsync(
                process.StandardOutput.BaseStream,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual("response", initialize.RootElement.GetProperty("type").GetString());
            Assert.AreEqual(1, initialize.RootElement.GetProperty("request_seq").GetInt32());
            Assert.IsTrue(initialize.RootElement.GetProperty("success").GetBoolean());

            await WriteRequestAsync(
                process.StandardInput.BaseStream,
                sequence: 2,
                "disconnect",
                TestContext.CancellationToken).ConfigureAwait(false);
            using JsonDocument disconnect = await ReadMessageAsync(
                process.StandardOutput.BaseStream,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual("response", disconnect.RootElement.GetProperty("type").GetString());
            Assert.AreEqual(2, disconnect.RootElement.GetProperty("request_seq").GetInt32());
            Assert.IsTrue(disconnect.RootElement.GetProperty("success").GetBoolean());

            process.StandardInput.Close();
            await process.WaitForExitAsync(TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(0, process.ExitCode);
            byte[] extraOutput = new byte[1];
            Assert.AreEqual(
                0,
                await process.StandardOutput.BaseStream
                    .ReadAsync(extraOutput, TestContext.CancellationToken)
                    .ConfigureAwait(false));
            Assert.AreEqual(string.Empty, await standardErrorTask.ConfigureAwait(false));
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

    private static async Task WriteRequestAsync(
        Stream stream,
        int sequence,
        string command,
        CancellationToken cancellationToken)
    {
        ArrayBufferWriter<byte> payload = new();
        using (var writer = new Utf8JsonWriter(payload))
        {
            writer.WriteStartObject();
            writer.WriteNumber("seq", sequence);
            writer.WriteString("type", "request");
            writer.WriteString("command", command);
            writer.WriteStartObject("arguments");
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        byte[] header = Encoding.ASCII.GetBytes(string.Create(
            CultureInfo.InvariantCulture,
            $"Content-Length: {payload.WrittenCount}\r\n\r\n"));
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload.WrittenMemory, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<JsonDocument> ReadMessageAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        List<byte> header = [];
        byte[] oneByte = new byte[1];
        while (true)
        {
            await stream.ReadExactlyAsync(oneByte, cancellationToken).ConfigureAwait(false);
            header.Add(oneByte[0]);
            if (header.Count >= 4 &&
                header[^4] == (byte)'\r' &&
                header[^3] == (byte)'\n' &&
                header[^2] == (byte)'\r' &&
                header[^1] == (byte)'\n')
            {
                break;
            }
        }

        string headerText = Encoding.ASCII.GetString([.. header]);
        string lengthText = headerText["Content-Length: ".Length..^4];
        int payloadLength = int.Parse(lengthText, CultureInfo.InvariantCulture);
        byte[] payload = GC.AllocateUninitializedArray<byte>(payloadLength);
        await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        return JsonDocument.Parse(payload);
    }

    private static string ResolveDotNetHost()
    {
        string? configured = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        return string.IsNullOrWhiteSpace(configured) ? "dotnet" : configured;
    }

    private static string FindRepositoryRoot([CallerFilePath] string sourcePath = "")
        => DebuggerTestEnvironment.FindRepositoryRoot(sourcePath);
}
