using Csls.Debugger.Contracts;
using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Rejects malformed type-operation plans crossing a physical evaluator-payload file boundary.
/// </summary>
[TestClass]
public sealed class DebuggerTypeOperationPlanTests
{
    /// <summary>
    /// Gets the framework-owned cancellation token for payload I/O.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Enumerates invalid fields independently for every type-operation discriminator.
    /// </summary>
    public static IEnumerable<(DebugExpressionNodeKind Kind, string Partition, string ExpectedMessage)> InvalidOperations
    {
        get
        {
            DebugExpressionNodeKind[] kinds =
            [
                DebugExpressionNodeKind.Conversion,
                DebugExpressionNodeKind.TypeTest,
                DebugExpressionNodeKind.TryCast,
                DebugExpressionNodeKind.ReferenceCast,
                DebugExpressionNodeKind.ReferenceUpcast
            ];
            foreach (DebugExpressionNodeKind kind in kinds)
            {
                foreach (string partition in new[] { "missing-type", "empty-type", "white-type", "long-type", "text" })
                {
                    yield return (kind, partition, "A type operation requires a bounded type name and no value text.");
                }

                yield return (kind, "missing-child", $"Expression node {kind} has 0 children; expected 1.");
                yield return (kind, "extra-child", $"Expression node {kind} has 2 children; expected 1.");
                yield return (kind, "operator", $"Operator Add is invalid for expression node {kind}.");
            }
        }
    }

    /// <summary>
    /// Rejects forged destination, value, operand, or operator fields with the precise validation diagnostic.
    /// </summary>
    /// <param name="kind">The forged type-operation discriminator.</param>
    /// <param name="partition">The malformed field to pass through a real file.</param>
    /// <param name="expectedMessage">The exact diagnostic required before any runtime binding.</param>
    [TestMethod]
    [DynamicData(nameof(InvalidOperations))]
    public async Task MalformedTypeOperationIsRejected(
        DebugExpressionNodeKind kind, string partition, string expectedMessage)
    {
        var operand = new DebugExpressionNode(DebugExpressionNodeKind.Identifier, DebugExpressionOperator.None,
            "source", TypeName: null, []);
        string? typeName = partition switch
        {
            "missing-type" => null,
            "empty-type" => string.Empty,
            "white-type" => " \t",
            "long-type" => new string('T', 4097),
            _ => "System.Object"
        };
        var node = new DebugExpressionNode(kind,
            partition == "operator" ? DebugExpressionOperator.Add : DebugExpressionOperator.None,
            partition == "text" ? "forged value" : null, typeName,
            partition == "missing-child" ? [] : partition == "extra-child" ? [operand, operand] : [operand]);
        var plan = new DebugExpressionPlan(DebuggerEvaluatorProtocol.CurrentPlanVersion, DebugExpressionLanguage.CSharp, node);
        string path = Path.Join(Path.GetTempPath(), $"csls-type-operation-plan-{Guid.NewGuid():N}.json");
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
