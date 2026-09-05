using System.Runtime.CompilerServices;

namespace Csls.Tests;

/// <summary>
/// Captures bounded language-server and extension-host logs before an editor fixture is removed.
/// </summary>
internal static class VsCodeTestDiagnostics
{
    /// <summary>
    /// Writes the tail of each relevant fixture log into the retained test report.
    /// </summary>
    /// <param name="context">The test report destination.</param>
    /// <param name="dataPaths">The owned local and remote editor profile directories.</param>
    internal static async Task WriteAsync(TestContext context, params string[] dataPaths)
    {
        foreach (string dataPath in dataPaths.Where(Directory.Exists))
        {
            foreach (string path in Directory.EnumerateFiles(dataPath, "*.log", SearchOption.AllDirectories)
                .Where(path => Path.GetFileName(path) is "exthost.log" or "remoteexthost.log" or
                    "renderer.log" or "main.log" or "sharedprocess.log" ||
                    Path.GetFileName(path).EndsWith("csls Integration Tests.log", StringComparison.Ordinal) ||
                    path.Contains("willibrandon.csls", StringComparison.OrdinalIgnoreCase))
                .Order(StringComparer.Ordinal))
            {
                try
                {
                    var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete);
                    await using ConfiguredAsyncDisposable disposal = stream.ConfigureAwait(false);
                    _ = stream.Seek(Math.Max(0, stream.Length - 65536), SeekOrigin.Begin);
                    using var reader = new StreamReader(stream, leaveOpen: true);
                    string text = await reader.ReadToEndAsync(CancellationToken.None).ConfigureAwait(false);
                    context.WriteLine($"Editor failure log: {path}{Environment.NewLine}{text}");
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    context.WriteLine($"Could not capture editor log {path}: {exception.Message}");
                }
            }
        }
    }
}
