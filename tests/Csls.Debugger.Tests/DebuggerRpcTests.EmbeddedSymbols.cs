using Csls.Debugger.Contracts;
using Csls.Debugger.Control;
using System.Runtime.CompilerServices;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies private debugger RPC behavior for embedded Portable PDB modules.
/// </summary>
public sealed partial class DebuggerRpcTests
{
    /// <summary>
    /// Binds source and resolves local names without a sidecar Portable PDB.
    /// </summary>
    [TestMethod]
    [OSCondition(ConditionMode.Exclude, OperatingSystems.Windows)]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task PrivateRpcInspectsEmbeddedPortablePdbTarget()
    {
        string testDirectory = Path.Join(
            Path.GetTempPath(),
            $"csls-debugger-embedded-{Guid.NewGuid():N}");
        Directory.CreateDirectory(testDirectory);
        try
        {
            await ExerciseEmbeddedSymbolsAsync(testDirectory, TestContext.CancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            if (Directory.Exists(testDirectory))
            {
                Directory.Delete(testDirectory, recursive: true);
            }
        }
    }

    private static async Task ExerciseEmbeddedSymbolsAsync(
        string testDirectory,
        CancellationToken cancellationToken)
    {
        string repositoryRoot = FindRepositoryRoot();
        string sourcePath = Path.Join(
            repositoryRoot,
            "test-assets",
            "Csls.Debugger.Fixtures.Embedded",
            "Program.cs");
        string[] sourceLines = await File.ReadAllLinesAsync(sourcePath, cancellationToken)
            .ConfigureAwait(false);
        int breakpointLine = sourceLines
            .Select(static (line, index) => (Line: line, Number: index + 1))
            .Single(static item => item.Line.Contains(
                "int embeddedNumber = number + 1;",
                StringComparison.Ordinal))
            .Number;
        string programPath = Path.Join(
            repositoryRoot,
            "artifacts",
            "bin",
            "Csls.Debugger.Fixtures.Embedded",
            "debug",
            "csls-debugger-fixture-embedded.dll");
        Assert.IsFalse(
            File.Exists(Path.ChangeExtension(programPath, ".pdb")),
            "The embedded-symbol fixture unexpectedly emitted a sidecar PDB.");

        var service = new DebuggerControlService();
        await using ConfiguredAsyncDisposable serviceDisposal = service.ConfigureAwait(false);
        string socketPath = Path.Join(testDirectory, "debugger.sock");
        var server = new DebuggerRpcServer(socketPath, service);
        await using ConfiguredAsyncDisposable serverDisposal = server.ConfigureAwait(false);
        server.Start();
        var client = new DebuggerRpcClient(socketPath);
        await using ConfiguredAsyncDisposable clientDisposal = client.ConfigureAwait(false);
        await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
        _ = await client.SetSourceBreakpointsAsync(
            new DebugSourceBreakpointSetRequest(
                sourcePath,
                [new DebugSourceBreakpointRequest(breakpointLine, null)]),
            cancellationToken).ConfigureAwait(false);
        _ = await client.LaunchAsync(
            new DebugLaunchRequest
            {
                Program = programPath,
                WorkingDirectory = testDirectory,
                Arguments = [Path.Join(testDirectory, "continue.signal")],
                SourceFileMap = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["C:\\agent\\_work\\Csls.Debugger.Fixtures.Embedded"] =
                        Path.GetDirectoryName(sourcePath)!
                }
            },
            cancellationToken).ConfigureAwait(false);

        DebugSessionSnapshot stopped = await WaitForStoppedAsync(client, cancellationToken)
            .ConfigureAwait(false);
        Assert.IsNotNull(stopped.StoppedThreadId);
        DebugStackTrace stack = await client.GetStackAsync(
            new DebugStackRequest(stopped.StoppedThreadId.Value, 0, 64),
            cancellationToken).ConfigureAwait(false);
        DebugStackFrameInfo frame = stack.StackFrames.Single(candidate =>
            string.Equals(candidate.Source?.Path, sourcePath, StringComparison.Ordinal) &&
            candidate.Line == breakpointLine);
        Assert.IsNotNull(frame.Source);
        Assert.IsGreaterThan(0, frame.Source.SourceReference);
        Assert.AreEqual("embedded source", frame.Source.Origin);
        Assert.IsNotNull(frame.Source.Checksum);
        Assert.AreEqual("SHA256", frame.Source.Checksum.Algorithm);
        DebugSourceContent source = await client.GetSourceContentAsync(
            new DebugSourceRequest(frame.Source.SourceReference),
            cancellationToken).ConfigureAwait(false);
        Assert.AreEqual("text/x-csharp", source.MimeType);
        Assert.Contains(
            "int embeddedNumber = number + 1;",
            source.Content,
            StringComparison.Ordinal);
        IReadOnlyList<DebugScopeInfo> scopes = await client.GetScopesAsync(
            new DebugScopesRequest(frame.Id),
            cancellationToken).ConfigureAwait(false);
        DebugScopeInfo locals = scopes.Single(static scope => scope.Name == "Locals");
        IReadOnlyList<DebugVariableInfo> variables = await client.GetVariablesAsync(
            new DebugVariablesRequest(locals.VariablesReference, 0, 0),
            cancellationToken).ConfigureAwait(false);
        DebugVariableInfo embeddedNumber = variables.Single(
            static variable => variable.Name == "embeddedNumber");
        Assert.AreEqual("0", embeddedNumber.Value);
        Assert.AreEqual("int", embeddedNumber.Type);
        _ = await client.TerminateAsync(cancellationToken).ConfigureAwait(false);
    }
}
