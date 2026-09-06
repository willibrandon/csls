using Microsoft.Diagnostics.NETCore.Client;
using System.Diagnostics;
using System.Text.Json;

namespace Csls.Tests;

/// <summary>
/// Captures an independently owned managed target for offline editor inspection.
/// </summary>
internal static class VsCodeDumpFixture
{
    /// <summary>
    /// Writes a real dump and its identity into the test workspace after observing target termination.
    /// </summary>
    internal static async Task PrepareAsync(string workspacePath, CancellationToken cancellationToken)
    {
        string repositoryRoot = EditorToolResolver.FindRepositoryRoot();
        string fixtureDirectory = Path.Join(workspacePath, ".vscode");
        Directory.CreateDirectory(fixtureDirectory);
        string dumpPath = Path.Join(fixtureDirectory, "managed-target.dmp");
        var startInfo = new ProcessStartInfo
        {
            FileName = EditorToolResolver.ResolveAbsoluteDotNetHost(),
            WorkingDirectory = workspacePath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add(EditorToolResolver.ResolveTestProcessHost(repositoryRoot));
        startInfo.ArgumentList.Add("--debugger-fixture");
        startInfo.ArgumentList.Add(Path.Join(fixtureDirectory, "finish.signal"));
        using Process target = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The editor dump target did not start.");
        Task<string> diagnostics = target.StandardError.ReadToEndAsync(CancellationToken.None);
        Task<string>? output = null;
        try
        {
            char[] ready = new char[5];
            int read = await target.StandardOutput.ReadBlockAsync(ready, cancellationToken)
                .ConfigureAwait(false);
            Assert.AreEqual(ready.Length, read, "The real dump fixture must announce readiness.");
            Assert.AreEqual("ready", new string(ready));
            output = target.StandardOutput.ReadToEndAsync(CancellationToken.None);
            var client = new DiagnosticsClient(target.Id);
            await client.WriteDumpAsync(DumpType.Triage, dumpPath, logDumpGeneration: false,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (!target.HasExited)
            {
                target.Kill(entireProcessTree: true);
            }
            await target.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            await diagnostics.ConfigureAwait(false);
            if (output is not null)
            {
                await output.ConfigureAwait(false);
            }
        }

        Assert.IsTrue(target.HasExited, "VS Code must inspect the dump after its original process exits.");
        Assert.IsGreaterThan(0L, new FileInfo(dumpPath).Length);
        using var manifest = new MemoryStream();
        using (var writer = new Utf8JsonWriter(manifest))
        {
            writer.WriteStartObject();
            writer.WriteString("dumpPath", dumpPath);
            writer.WriteNumber("processId", target.Id);
            writer.WriteString("moduleName", "csls-test-process-host.dll");
            writer.WriteEndObject();
        }
        await File.WriteAllBytesAsync(Path.Join(fixtureDirectory, "dump-fixture.json"),
            manifest.ToArray(), cancellationToken).ConfigureAwait(false);
    }
}
