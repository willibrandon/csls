using Csls.Debugger.Contracts;
using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Rejects malformed default-literal plans read through a physical evaluator-payload file.
/// </summary>
[TestClass]
public sealed class DebuggerDefaultExpressionPlanTests
{
    /// <summary>
    /// Gets the framework-owned cancellation token for source-payload I/O.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Rejects payload fields that would turn an untyped default into a forged typed or executable expression.
    /// </summary>
    /// <param name="partition">The malformed default-node field.</param>
    /// <param name="expectedMessage">The precise validation failure for that field.</param>
    [TestMethod]
    [DataRow("text", "A default literal cannot carry text or an inferred type.")]
    [DataRow("type", "A default literal cannot carry text or an inferred type.")]
    [DataRow("child", "Expression node DefaultLiteral has 1 children; expected 0.")]
    [DataRow("operator", "Operator Add is invalid for expression node DefaultLiteral.")]
    public async Task MalformedDefaultLiteralIsRejected(string partition, string expectedMessage)
    {
        var node = new DebugExpressionNode(DebugExpressionNodeKind.DefaultLiteral,
            partition == "operator" ? DebugExpressionOperator.Add : DebugExpressionOperator.None,
            partition == "text" ? "default" : null,
            partition == "type" ? "int" : null,
            partition == "child"
                ? [new DebugExpressionNode(DebugExpressionNodeKind.Literal, DebugExpressionOperator.None, "1", "int", [])]
                : []);
        var plan = new DebugExpressionPlan(DebuggerEvaluatorProtocol.CurrentPlanVersion, DebugExpressionLanguage.CSharp, node);
        string path = Path.Join(Path.GetTempPath(), $"csls-default-plan-{Guid.NewGuid():N}.json");
        try
        {
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(plan), TestContext.CancellationToken)
                .ConfigureAwait(false);
            string payload = await File.ReadAllTextAsync(path, TestContext.CancellationToken).ConfigureAwait(false);
            DebugExpressionPlan? deserialized = JsonSerializer.Deserialize<DebugExpressionPlan>(payload);
            Assert.IsNotNull(deserialized);
            InvalidDataException exception = Assert.ThrowsExactly<InvalidDataException>(() =>
                ManagedExpressionPlanValidator.Validate(deserialized, DebugExpressionLanguage.CSharp));
            Assert.AreEqual(expectedMessage, exception.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
