using Csls.Debugger.Contracts;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies bounded language-aware managed runtime type-name parsing.
/// </summary>
[TestClass]
public sealed class ManagedRuntimeTypeNameParserTests
{
    /// <summary>
    /// Parses nested C# generic and array type syntax without losing CLR identity.
    /// </summary>
    [TestMethod]
    public void ParsesNestedCSharpType()
    {
        ManagedRuntimeTypeReference result = ManagedRuntimeTypeNameParser.Parse(
            "Example.Container<System.Collections.Generic.List<System.Int32[]>>",
            DebugExpressionLanguage.CSharp);

        Assert.AreEqual("Example.Container`1", result.MetadataName);
        Assert.HasCount(1, result.TypeArguments);
        ManagedRuntimeTypeReference list = result.TypeArguments[0];
        Assert.AreEqual("System.Collections.Generic.List`1", list.MetadataName);
        Assert.HasCount(1, list.TypeArguments);
        ManagedRuntimeTypeReference array = list.TypeArguments[0];
        Assert.AreEqual("System.Int32", array.MetadataName);
        Assert.AreEqual(1, array.ArrayRanks.Single());
        Assert.AreEqual(
            "Example.Container<System.Collections.Generic.List<int[]>>",
            result.DebuggerTypeName);
    }

    /// <summary>
    /// Parses Visual Basic generic and multidimensional-array type syntax.
    /// </summary>
    [TestMethod]
    public void ParsesVisualBasicType()
    {
        ManagedRuntimeTypeReference result = ManagedRuntimeTypeNameParser.Parse(
            "gLoBaL.Example.Container(Of iNtEgEr(,))",
            DebugExpressionLanguage.VisualBasic);

        Assert.AreEqual("Example.Container`1", result.MetadataName);
        Assert.HasCount(1, result.TypeArguments);
        ManagedRuntimeTypeReference array = result.TypeArguments[0];
        Assert.AreEqual("System.Int32", array.MetadataName);
        Assert.AreEqual(2, array.ArrayRanks.Single());
        Assert.AreEqual("Example.Container<int[,]>", result.DebuggerTypeName);
    }

    /// <summary>
    /// Resolves fully qualified CLR primitive names with Visual Basic casing rules.
    /// </summary>
    [TestMethod]
    public void ParsesVisualBasicClrPrimitiveCaseInsensitively()
    {
        ManagedRuntimeTypeReference result = ManagedRuntimeTypeNameParser.Parse(
            "gLoBaL.sYsTeM.iNt32",
            DebugExpressionLanguage.VisualBasic);

        Assert.AreEqual("System.Int32", result.MetadataName);
        Assert.AreEqual("int", result.DebuggerTypeName);
    }

    /// <summary>
    /// Parses F# nullable type syntax into its exact generic CLR definition.
    /// </summary>
    [TestMethod]
    public void ParsesFSharpNullableType()
    {
        ManagedRuntimeTypeReference result = ManagedRuntimeTypeNameParser.Parse(
            "Example.Container<System.Nullable<int>>",
            DebugExpressionLanguage.FSharp);

        Assert.AreEqual("Example.Container`1", result.MetadataName);
        ManagedRuntimeTypeReference nullable = result.TypeArguments.Single();
        Assert.AreEqual("System.Nullable`1", nullable.MetadataName);
        Assert.AreEqual("System.Int32", nullable.TypeArguments.Single().MetadataName);
    }

    /// <summary>
    /// Preserves the language-specific CLR meaning of the float keyword.
    /// </summary>
    [TestMethod]
    public void ParsesLanguageSpecificFloatAliases()
    {
        ManagedRuntimeTypeReference csharp = ManagedRuntimeTypeNameParser.Parse(
            "float",
            DebugExpressionLanguage.CSharp);
        ManagedRuntimeTypeReference fsharp = ManagedRuntimeTypeNameParser.Parse(
            "float",
            DebugExpressionLanguage.FSharp);

        Assert.AreEqual("System.Single", csharp.MetadataName);
        Assert.AreEqual("System.Double", fsharp.MetadataName);
    }

    /// <summary>
    /// Rejects type names beyond the parser's bounded input limit.
    /// </summary>
    [TestMethod]
    public void RejectsUnboundedTypeName()
    {
        string typeName = new('A', 4097);

        NotSupportedException exception = Assert.ThrowsExactly<NotSupportedException>(() =>
            ManagedRuntimeTypeNameParser.Parse(typeName, DebugExpressionLanguage.CSharp));

        Assert.Contains("at most 4096 characters", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Rejects generic type names beyond the recursive nesting limit.
    /// </summary>
    [TestMethod]
    public void RejectsUnboundedGenericDepth()
    {
        string typeName = string.Concat(
            Enumerable.Repeat("Example.Container<", 33)) + "int" + new string('>', 33);

        NotSupportedException exception = Assert.ThrowsExactly<NotSupportedException>(() =>
            ManagedRuntimeTypeNameParser.Parse(typeName, DebugExpressionLanguage.CSharp));

        Assert.Contains("at most 32 nested levels", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Rejects generic type names beyond the total argument limit.
    /// </summary>
    [TestMethod]
    public void RejectsUnboundedGenericArgumentCount()
    {
        string typeName = $"Example.Container<{string.Join(',', Enumerable.Repeat("int", 65))}>";

        NotSupportedException exception = Assert.ThrowsExactly<NotSupportedException>(() =>
            ManagedRuntimeTypeNameParser.Parse(typeName, DebugExpressionLanguage.CSharp));

        Assert.Contains("at most 64 generic arguments", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Rejects managed arrays beyond CoreCLR's supported rank limit.
    /// </summary>
    [TestMethod]
    public void RejectsUnboundedArrayRank()
    {
        string typeName = $"int[{new string(',', 32)}]";

        NotSupportedException exception = Assert.ThrowsExactly<NotSupportedException>(() =>
            ManagedRuntimeTypeNameParser.Parse(typeName, DebugExpressionLanguage.CSharp));

        Assert.Contains("at most 32 dimensions", exception.Message, StringComparison.Ordinal);
    }
}
