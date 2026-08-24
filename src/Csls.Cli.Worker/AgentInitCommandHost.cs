using System.Text;

namespace Csls.Cli.Worker;

/// <summary>
/// Writes the reusable csls agent skill to a file or standard output.
/// </summary>
internal static class AgentInitCommandHost
{
    /// <summary>
    /// Executes one normalized agent skill initialization request.
    /// </summary>
    /// <param name="arguments">The normalized launcher arguments.</param>
    /// <param name="writeJson">Whether to write a machine-readable envelope.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>The process exit code.</returns>
    internal static async Task<int> RunAsync(
        IReadOnlyList<string> arguments,
        bool writeJson,
        CancellationToken cancellationToken)
    {
        if (arguments.Count != 5 ||
            !bool.TryParse(arguments[2], out bool force) ||
            !bool.TryParse(arguments[3], out bool writeStdout))
        {
            CliOutputWriter.WriteError(
                "invalid-request",
                "The launcher supplied an invalid agent initialization request.",
                writeJson);
            return 1;
        }

        if (writeStdout)
        {
            await Console.Out.WriteAsync(AgentSkillContent.Value.AsMemory(), cancellationToken)
                .ConfigureAwait(false);
            return 0;
        }

        string outputPath = arguments[1];
        string? directory = Path.GetDirectoryName(outputPath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new InvalidDataException(
                "The launcher supplied an agent skill path without a containing directory.");
        }

        Directory.CreateDirectory(directory);
        string temporaryPath = Path.Join(
            directory,
            $".{Path.GetFileName(outputPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporaryPath, new FileStreamOptions
            {
                Access = FileAccess.Write,
                Mode = FileMode.CreateNew,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
                Share = FileShare.None
            }))
            using (var writer = new StreamWriter(
                stream,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                bufferSize: 4096,
                leaveOpen: true))
            {
                await writer.WriteAsync(AgentSkillContent.Value.AsMemory(), cancellationToken)
                    .ConfigureAwait(false);
                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            try
            {
                File.Move(temporaryPath, outputPath, overwrite: force);
            }
            catch (IOException) when (!force && File.Exists(outputPath))
            {
                CliOutputWriter.WriteError(
                    "file-exists",
                    $"The agent skill file already exists: {outputPath}. Use --force to replace it.",
                    writeJson);
                return 1;
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }

        CliOutputWriter.WriteAgentInit(
            new AgentInitResult { OutputPath = outputPath },
            writeJson);
        return 0;
    }
}
