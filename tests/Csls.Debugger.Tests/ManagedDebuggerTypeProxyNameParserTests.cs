namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies bounded reflection-name parsing for debugger type proxies.
/// </summary>
[TestClass]
public sealed class ManagedDebuggerTypeProxyNameParserTests
{
    /// <summary>
    /// Separates an open proxy definition from its declaring assembly identity.
    /// </summary>
    [TestMethod]
    public void ParsesOpenGenericProxyDefinition()
    {
        bool parsed = ManagedDebuggerTypeProxyNameParser.TryParse(
            "Example.Proxy`1, Example.Library, Version=1.0.0.0, Culture=neutral",
            out ManagedDebuggerTypeProxyName? result);

        Assert.IsTrue(parsed);
        Assert.IsNotNull(result);
        Assert.AreEqual("Example.Proxy`1", result.MetadataName);
        Assert.AreEqual("Example.Library", result.AssemblyName);
        Assert.IsFalse(result.IsConstructed);
    }

    /// <summary>
    /// Ignores nested assembly separators when parsing a constructed proxy identity.
    /// </summary>
    [TestMethod]
    public void ParsesConstructedGenericProxyDefinition()
    {
        bool parsed = ManagedDebuggerTypeProxyNameParser.TryParse(
            "Example.Proxy`1[[System.Int32, System.Private.CoreLib]], Example.Library",
            out ManagedDebuggerTypeProxyName? result);

        Assert.IsTrue(parsed);
        Assert.IsNotNull(result);
        Assert.AreEqual("Example.Proxy`1", result.MetadataName);
        Assert.AreEqual("Example.Library", result.AssemblyName);
        Assert.IsTrue(result.IsConstructed);
    }

    /// <summary>
    /// Rejects malformed generic brackets before metadata resolution begins.
    /// </summary>
    [TestMethod]
    public void RejectsUnbalancedGenericBrackets()
    {
        bool parsed = ManagedDebuggerTypeProxyNameParser.TryParse(
            "Example.Proxy`1[[System.Int32, System.Private.CoreLib], Example.Library",
            out ManagedDebuggerTypeProxyName? result);

        Assert.IsFalse(parsed);
        Assert.IsNull(result);
    }
}
