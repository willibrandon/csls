namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies bounded side-effect-free debugger display template rendering.
/// </summary>
[TestClass]
public sealed class ManagedDebuggerDisplayTemplateTests
{
    /// <summary>
    /// Renders escaped braces, ordinary values, and quote-suppressed strings.
    /// </summary>
    [TestMethod]
    public void TryRenderEscapedAndFieldSegmentsProducesExpectedDisplay()
    {
        bool rendered = ManagedDebuggerDisplayTemplate.TryRender(
            "{{label}}={label,nq}; count={count}",
            expression => expression switch
            {
                "label" => new ManagedValueDisplay("\"alpha\\nbeta\"", "string"),
                "count" => new ManagedValueDisplay("42", "int"),
                _ => null
            },
            out string result);

        Assert.IsTrue(rendered);
        Assert.AreEqual("{label}=alpha\\nbeta; count=42", result);
    }

    /// <summary>
    /// Rejects malformed syntax, unresolved fields, and unsupported format specifiers.
    /// </summary>
    /// <param name="template">The invalid template to validate.</param>
    [TestMethod]
    [DataRow("broken {")]
    [DataRow("broken }")]
    [DataRow("{missing}")]
    [DataRow("{value,raw}")]
    [DataRow("{a{value}}")]
    public void TryRenderInvalidTemplateReturnsFalse(string template)
    {
        bool rendered = ManagedDebuggerDisplayTemplate.TryRender(
            template,
            expression => expression == "value"
                ? new ManagedValueDisplay("42", "int")
                : null,
            out string result);

        Assert.IsFalse(rendered);
        Assert.AreEqual(string.Empty, result);
    }

    /// <summary>
    /// Rejects templates whose input, expression count, or rendered output exceeds bounds.
    /// </summary>
    [TestMethod]
    public void TryRenderResourceLimitsReturnFalse()
    {
        bool oversizedTemplate = ManagedDebuggerDisplayTemplate.TryRender(
            new string('x', 16 * 1024 + 1),
            _ => null,
            out string templateResult);
        bool excessiveExpressions = ManagedDebuggerDisplayTemplate.TryRender(
            string.Concat(Enumerable.Repeat("{value}", 65)),
            _ => new ManagedValueDisplay("1", "int"),
            out string expressionResult);
        bool oversizedOutput = ManagedDebuggerDisplayTemplate.TryRender(
            "{value}",
            _ => new ManagedValueDisplay(new string('x', 1024 * 1024 + 1), "object"),
            out string outputResult);

        Assert.IsFalse(oversizedTemplate);
        Assert.IsFalse(excessiveExpressions);
        Assert.IsFalse(oversizedOutput);
        Assert.AreEqual(string.Empty, templateResult);
        Assert.AreEqual(string.Empty, expressionResult);
        Assert.AreEqual(string.Empty, outputResult);
    }
}
