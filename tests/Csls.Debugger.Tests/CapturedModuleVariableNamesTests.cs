using System.Diagnostics;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies language-aware parameter slots and local scope boundaries using real compiler outputs.
/// </summary>
[TestClass]
public sealed class CapturedModuleVariableNamesTests
{
    /// <summary>
    /// Gets the active framework context and cancellation token.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Accepts bounded associated PDBs and preserves parameters when the file exceeds its byte budget.
    /// </summary>
    /// <param name="fileLength">The size of the padded real Portable PDB file.</param>
    /// <param name="hasNames">Whether the bounded reader accepts the file.</param>
    [TestMethod]
    [DataRow(268435455L, true)]
    [DataRow(268435456L, true)]
    [DataRow(268435457L, false)]
    public void AssociatedNamesEnforceSymbolFileBudget(long fileLength, bool hasNames)
    {
        string original = DebuggerLanguageFixtures.GetProgramPath("Csls.Debugger.Fixtures.CSharp", "Debug");
        DirectoryInfo directory = Directory.CreateTempSubdirectory("csls-captured-symbol-");
        string program = Path.Join(directory.FullName, Path.GetFileName(original));
        string pdb = Path.ChangeExtension(program, ".pdb");
        try
        {
            File.Copy(original, program);
            File.Copy(Path.ChangeExtension(original, ".pdb"), pdb);
            using (FileStream symbols = File.Open(pdb, FileMode.Open, FileAccess.Write, FileShare.None))
            {
                symbols.SetLength(fileLength);
            }
            uint token;
            using (FileStream stream = File.OpenRead(program))
            using (var pe = new PEReader(stream))
            {
                token = checked((uint)(pe.PEHeaders.CorHeader
                    ?? throw new AssertFailedException("The fixture has no managed entry point.")).EntryPointTokenOrRelativeVirtualAddress);
            }
            IReadOnlyDictionary<int, string> names = ReadNames(program, token, 0, arguments: false);
            if (hasNames)
            {
                Assert.Contains("value", names.Values);
            }
            else
            {
                Assert.IsEmpty(names);
            }
            Assert.AreEqual("arguments", Assert.ContainsSingle(ReadNames(program, token, 0, arguments: true)).Value);
        }
        finally
        {
            File.Delete(pdb);
            File.Delete(program);
            directory.Delete();
        }
    }

    /// <summary>
    /// Reads embedded local names and rejects hostile declared expansion sizes before decompression.
    /// </summary>
    /// <param name="declaredSize">The replacement size, or null to preserve the compiled symbol image.</param>
    [TestMethod]
    [DataRow(null)]
    [DataRow(-1)]
    [DataRow(0)]
    [DataRow(268435457)]
    [DataRow(int.MaxValue)]
    public void EmbeddedNamesRejectOversizedSymbolExpansion(int? declaredSize)
    {
        string original = Path.GetFullPath(Path.Join(AppContext.BaseDirectory, "..", "..",
            "Csls.Debugger.Fixtures.Embedded", "debug", "csls-debugger-fixture-embedded.dll"));
        DirectoryInfo directory = Directory.CreateTempSubdirectory("csls-captured-symbol-");
        string program = Path.Join(directory.FullName, Path.GetFileName(original));
        try
        {
            File.Copy(original, program);
            uint token;
            using (FileStream stream = File.Open(program, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            using (var pe = new PEReader(stream, PEStreamOptions.LeaveOpen))
            {
                token = checked((uint)(pe.PEHeaders.CorHeader
                    ?? throw new AssertFailedException("The fixture has no managed entry point.")).EntryPointTokenOrRelativeVirtualAddress);
                DebugDirectoryEntry embedded = Assert.ContainsSingle(pe.ReadDebugDirectory()
                    .Where(entry => entry.Type == DebugDirectoryEntryType.EmbeddedPortablePdb));
                if (declaredSize is int size)
                {
                    stream.Position = embedded.DataPointer + sizeof(uint);
                    using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
                    writer.Write(size);
                }
            }

            IReadOnlyDictionary<int, string> locals = ReadNames(program, token, 0, arguments: false);
            if (declaredSize is null)
            {
                Assert.Contains("embeddedNumber", locals.Values);
            }
            else
            {
                Assert.IsEmpty(locals);
            }
            Assert.AreEqual("args", Assert.ContainsSingle(ReadNames(program, token, 0, arguments: true)).Value);
        }
        finally
        {
            File.Delete(program);
            directory.Delete();
        }
    }

    /// <summary>
    /// Preserves parameter inspection when an adjacent PDB is an unconnected Unix FIFO.
    /// </summary>
    [TestMethod]
    [OSCondition(ConditionMode.Exclude, OperatingSystems.Windows)]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task CapturedNamesRejectNonSeekableSymbols()
    {
        string original = DebuggerLanguageFixtures.GetProgramPath("Csls.Debugger.Fixtures.CSharp", "Debug");
        DirectoryInfo directory = Directory.CreateTempSubdirectory("csls-captured-symbol-");
        string program = Path.Join(directory.FullName, Path.GetFileName(original));
        string pdb = Path.ChangeExtension(program, ".pdb");
        try
        {
            File.Copy(original, program);
            var command = new ProcessStartInfo("mkfifo") { ArgumentList = { pdb } };
            (int exitCode, string output, string error) = await DebuggerTestProcess.RunAsync(command,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(0, exitCode, output + error);
            uint token;
            using (FileStream stream = File.OpenRead(program))
            using (var pe = new PEReader(stream))
            {
                token = checked((uint)(pe.PEHeaders.CorHeader
                    ?? throw new AssertFailedException("The fixture has no managed entry point.")).EntryPointTokenOrRelativeVirtualAddress);
            }
            Assert.IsEmpty(ReadNames(program, token, 0, arguments: false));
            Assert.AreEqual("arguments", Assert.ContainsSingle(ReadNames(program, token, 0, arguments: true)).Value);
        }
        finally
        {
            File.Delete(pdb);
            File.Delete(program);
            directory.Delete();
        }
    }

    /// <summary>
    /// Preserves instance and static parameter indexes and the compiler's half-open local scopes.
    /// </summary>
    /// <param name="language">The compiler fixture language.</param>
    /// <param name="configuration">The compiler optimization configuration.</param>
    [TestMethod]
    [DataRow("CSharp", "Debug")]
    [DataRow("CSharp", "Release")]
    [DataRow("VisualBasic", "Debug")]
    [DataRow("VisualBasic", "Release")]
    [DataRow("FSharp", "Debug")]
    [DataRow("FSharp", "Release")]
    public void CapturedNamesFollowLanguageAndLocalScope(string language, string configuration)
    {
        string program = DebuggerLanguageFixtures.GetProgramPath($"Csls.Debugger.Fixtures.{language}", configuration);
        using FileStream image = File.OpenRead(program);
        using var pe = new PEReader(image, PEStreamOptions.LeaveOpen);
        MetadataReader metadata = pe.GetMetadataReader();
        MethodDefinitionHandle instance = Assert.ContainsSingle(metadata.MethodDefinitions.Where(handle =>
        {
            MethodDefinition method = metadata.GetMethodDefinition(handle);
            return metadata.GetString(method.Name) == "AddNumber" &&
                metadata.GetString(metadata.GetTypeDefinition(method.GetDeclaringType()).Name) == "DebuggerFixtureValue";
        }));
        uint instanceToken = checked((uint)MetadataTokens.GetToken(instance));
        IReadOnlyDictionary<int, string> instanceNames = ReadNames(program, instanceToken, 0, arguments: true);
        Assert.HasCount(2, instanceNames);
        Assert.AreEqual(language == "VisualBasic" ? "Me" : "this", instanceNames[0]);
        Assert.AreEqual("value", instanceNames[1]);

        MethodDefinitionHandle entry = Assert.ContainsSingle(metadata.MethodDefinitions.Where(handle =>
            string.Equals(metadata.GetString(metadata.GetMethodDefinition(handle).Name), "Main", StringComparison.OrdinalIgnoreCase)));
        uint entryToken = checked((uint)MetadataTokens.GetToken(entry));
        IReadOnlyDictionary<int, string> entryNames = ReadNames(program, entryToken, 0, arguments: true);
        Assert.HasCount(1, entryNames);
        Assert.AreEqual("arguments", entryNames[0]);

        using FileStream pdbStream = File.OpenRead(Path.ChangeExtension(program, ".pdb"));
        using var provider = MetadataReaderProvider.FromPortablePdbStream(pdbStream);
        MetadataReader symbols = provider.GetMetadataReader();
        (LocalScope Scope, LocalVariable Variable) selected = Assert.ContainsSingle(
            from handle in symbols.GetLocalScopes(entry)
            let scope = symbols.GetLocalScope(handle)
            from variableHandle in scope.GetLocalVariables()
            let variable = symbols.GetLocalVariable(variableHandle)
            where symbols.GetString(variable.Name) == "value"
            select (scope, variable));
        Assert.IsGreaterThan(0, selected.Scope.Length);
        uint start = checked((uint)selected.Scope.StartOffset);
        uint end = checked(start + (uint)selected.Scope.Length);
        Assert.AreEqual("value", ReadNames(program, entryToken, start, arguments: false)[selected.Variable.Index]);
        Assert.AreEqual("value", ReadNames(program, entryToken, end - 1, arguments: false)[selected.Variable.Index]);
        Assert.DoesNotContain("value", ReadNames(program, entryToken, end, arguments: false).Values);
        if (start > 0)
        {
            Assert.DoesNotContain("value", ReadNames(program, entryToken, start - 1, arguments: false).Values);
        }
    }

    private static IReadOnlyDictionary<int, string> ReadNames(string program, uint token, uint offset, bool arguments)
    {
        using FileStream image = File.OpenRead(program);
        IReadOnlyDictionary<int, string> names = CapturedModuleVariableNames.Read(image, false, program, token, offset, arguments);
        Assert.IsTrue(image.CanRead, "Name inspection must preserve the caller-owned image stream.");
        return names;
    }
}
