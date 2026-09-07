using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Text;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies generic argument limits through real compiler-produced targets and the DAP transport.
/// </summary>
public sealed partial class DapSessionTests
{
    /// <summary>
    /// Preserves exact boundary types and stopped inspection after an over-budget generic value.
    /// </summary>
    /// <param name="arity">The number of real generic arguments in the emitted type.</param>
    /// <param name="namedTuple">Whether the final argument carries authored tuple element names.</param>
    [TestMethod]
    [DataRow(255, false)]
    [DataRow(256, false)]
    [DataRow(257, false)]
    [DataRow(255, true)]
    [DataRow(256, true)]
    [DataRow(257, true)]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task RuntimeGenericArityBoundaryPreservesInspection(int arity, bool namedTuple)
    {
        string directory = Directory.CreateTempSubdirectory("csls-runtime-type-arity-").FullName;
        try
        {
            (string program, string source, int line, string expectedType) = await EmitGenericArityTargetAsync(
                directory, arity, namedTuple).ConfigureAwait(false);
            DapTestClient client = await DapTestClient.CreateAsync(TestContext.CancellationToken).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable cleanup = client.ConfigureAwait(false);
            await InitializeAndLaunchAsync(client, program, Path.Join(directory, "unused.signal")).ConfigureAwait(false);
            int thread = await ConfigureBreakpointAsync(client, source, line).ConfigureAwait(false);
            int frame = await AssertStoppedFrameAsync(client, thread, source, line).ConfigureAwait(false);
            JsonElement result = await ReadEvaluationAsync(client, frame, "values", arity <= 256,
                TestContext.CancellationToken).ConfigureAwait(false);
            if (arity <= 256)
            {
                Assert.AreEqual(expectedType + "[]", result.GetProperty("type").GetString());
                int reference = result.GetProperty("variablesReference").GetInt32();
                Assert.IsGreaterThan(0, reference);
                JsonElement[] children = await ReadVariablesAsync(client, reference).ConfigureAwait(false);
                Assert.HasCount(2, children);
                Assert.AreSequenceEqual([expectedType, expectedType],
                    children.Select(child => child.GetProperty("type").GetString()));
                Assert.AreEqual("null", children[1].GetProperty("value").GetString());
                Assert.AreEqual(0, children[1].GetProperty("variablesReference").GetInt32());
                Assert.AreSequenceEqual(children.Select(Presentation),
                    (await ReadVariablesAsync(client, reference).ConfigureAwait(false))
                        .Select(Presentation));
                Assert.IsEmpty(await ReadVariablesAsync(client,
                    children[0].GetProperty("variablesReference").GetInt32()).ConfigureAwait(false));
            }
            else
            {
                string? message = result.GetProperty("message").GetString();
                Assert.IsNotNull(message);
                Assert.Contains("The runtime type exceeds the generic argument limit of 256.",
                    message);
            }

            JsonElement sentinel = await ReadEvaluationAsync(client, frame, "sentinel", true,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual("42", sentinel.GetProperty("result").GetString());
            Assert.AreEqual("int", sentinel.GetProperty("type").GetString());
            Assert.AreEqual(frame, await AssertStoppedFrameAsync(client, thread, source, line).ConfigureAwait(false));
            int targetProcessId = client.TargetProcessId
                ?? throw new InvalidOperationException("The target process was not reported.");
            await DisconnectAsync(client).ConfigureAwait(false);
            await AssertProcessExitedAsync(targetProcessId, TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(string.Empty, client.Diagnostics.ToString());
        }
        finally
        {
            await DebuggerTestDirectoryReleaseWaiter.DeleteAsync(directory, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }

        Assert.IsFalse(Directory.Exists(directory));

        static (string? Name, string? Value, string? Type, string? Expression) Presentation(JsonElement value) =>
            (value.GetProperty("name").GetString(), value.GetProperty("value").GetString(),
                value.GetProperty("type").GetString(), value.GetProperty("evaluateName").GetString());
    }

    private async Task<(string Program, string Source, int Line, string ExpectedType)> EmitGenericArityTargetAsync(
        string directory, int arity, bool namedTuple)
    {
        string parameters = string.Join(", ", Enumerable.Range(0, arity).Select(index => $"T{index}"));
        string arguments = string.Join(", ", Enumerable.Repeat("int", arity - 1)
            .Append(namedTuple ? "(int left, string right)" : "string"));
        string valueSource = $$"""
            namespace Csls.RuntimeTypeArity;

            /// <summary>
            /// Carries a real runtime generic instantiation at the inspection budget boundary.
            /// </summary>
            internal sealed class Value<{{parameters}}>;
            """;
        string programSource = $$"""
            using System;

            namespace Csls.RuntimeTypeArity;

            /// <summary>
            /// Retains generic array values and a scalar for source-level inspection.
            /// </summary>
            internal static class Program
            {
                /// <summary>
                /// Holds independently typed array elements at an authored breakpoint.
                /// </summary>
                internal static void Main()
                {
                    Value<{{arguments}}>?[] values = [new(), null];
                    int sentinel = 42;
                    Console.WriteLine("ready");
                    GC.KeepAlive(values);
                    GC.KeepAlive(sentinel);
                }
            }
            """;
        string sourcePath = Path.Join(directory, "Program.cs");
        string valuePath = Path.Join(directory, "Value.cs");
        string programPath = Path.Join(directory, "Csls.RuntimeTypeArity.dll");
        await File.WriteAllTextAsync(sourcePath, programSource, Encoding.UTF8, TestContext.CancellationToken)
            .ConfigureAwait(false);
        await File.WriteAllTextAsync(valuePath, valueSource, Encoding.UTF8, TestContext.CancellationToken)
            .ConfigureAwait(false);
        string platformAssemblies = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string
            ?? throw new InvalidOperationException("The trusted platform assembly list is unavailable.");
        var compilation = CSharpCompilation.Create("Csls.RuntimeTypeArity",
            [Parse(programSource, sourcePath), Parse(valueSource, valuePath)],
            platformAssemblies.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                .Select(static path => MetadataReference.CreateFromFile(path)),
            new CSharpCompilationOptions(OutputKind.ConsoleApplication,
                optimizationLevel: OptimizationLevel.Debug, deterministic: true,
                nullableContextOptions: NullableContextOptions.Enable,
                generalDiagnosticOption: ReportDiagnostic.Error));
        using (var pe = new FileStream(programPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (var pdb = new FileStream(Path.ChangeExtension(programPath, ".pdb"), FileMode.CreateNew,
            FileAccess.Write, FileShare.None))
        {
            EmitResult emitted = compilation.Emit(pe, pdb,
                options: new EmitOptions(debugInformationFormat: DebugInformationFormat.PortablePdb),
                cancellationToken: TestContext.CancellationToken);
            Assert.IsTrue(emitted.Success, string.Join(Environment.NewLine, emitted.Diagnostics));
        }
        File.Copy(Path.ChangeExtension(ResolveTestProcessHost(), ".runtimeconfig.json"),
            Path.ChangeExtension(programPath, ".runtimeconfig.json"));
        return (programPath, sourcePath, FindSourceLine(programSource.Split('\n'), "Console.WriteLine(\"ready\");"),
            $"Csls.RuntimeTypeArity.Value<{arguments}>");

        SyntaxTree Parse(string source, string path) => CSharpSyntaxTree.ParseText(
            SourceText.From(source, Encoding.UTF8, SourceHashAlgorithm.Sha256),
            new CSharpParseOptions(LanguageVersion.CSharp14), path, TestContext.CancellationToken);
    }
}
