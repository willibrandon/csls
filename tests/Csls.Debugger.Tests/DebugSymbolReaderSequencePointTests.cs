using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies hidden instruction boundaries against compiler-produced Portable PDB files.
/// </summary>
[TestClass]
public sealed class DebugSymbolReaderSequencePointTests
{
    /// <summary>
    /// Preserves every hidden compiler boundary while keeping breakpoint locations visible by default.
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
    public void HiddenSequencePointsPreserveCompilerInstructionBoundaries(string language, string configuration)
    {
        string program = DebuggerLanguageFixtures.GetProgramPath($"Csls.Debugger.Fixtures.{language}", configuration);
        using FileStream stream = File.OpenRead(Path.ChangeExtension(program, ".pdb"));
        using var provider = MetadataReaderProvider.FromPortablePdbStream(stream, MetadataStreamOptions.LeaveOpen);
        MetadataReader metadata = provider.GetMetadataReader();
        var expected = new List<(uint Token, int Offset)>();
        foreach (MethodDebugInformationHandle handle in metadata.MethodDebugInformation)
        {
            uint token = checked((uint)MetadataTokens.GetToken(handle.ToDefinitionHandle()));
            foreach (SequencePoint point in metadata.GetMethodDebugInformation(handle).GetSequencePoints()
                .Where(point => point.IsHidden))
            {
                expected.Add((token, point.Offset));
            }
        }

        Assert.IsNotEmpty(expected);
        using DebugSymbolReader symbols = DebugSymbolReader.TryOpen(program)
            ?? throw new AssertFailedException("The real compiler fixture has no matching symbols.");
        ManagedSequencePoint[] hidden = [.. symbols.GetSequencePoints(null, includeHidden: true)
            .Where(point => point.IsHidden)];
        Assert.HasCount(expected.Count, hidden);
        for (int index = 0; index < expected.Count; index++)
        {
            Assert.AreEqual(expected[index].Token, hidden[index].MethodToken);
            Assert.AreEqual(expected[index].Offset, hidden[index].IlOffset);
            Assert.AreEqual(string.Empty, hidden[index].SourcePath);
        }

        IReadOnlyList<ManagedSequencePoint> visible = symbols.GetSequencePoints(null);
        Assert.IsNotEmpty(visible);
        Assert.IsEmpty(visible.Where(point => point.IsHidden));
    }
}
