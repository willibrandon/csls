using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies compound generic construction across supported .NET source languages.
/// </summary>
public sealed partial class DapSessionTests
{
    private async Task AssertCompoundGenericConstructionAsync(
        DapTestClient client,
        int stoppedThreadId,
        string sourcePath,
        int breakpointLine,
        string genericType,
        string sourceExtension,
        string expectedGenericType)
    {
        string[] typeArguments = sourceExtension switch
        {
            "cs" =>
            [
                "int",
                "string",
                "System.Collections.Generic.List<int>",
                "int[]",
                "int[,]",
                "int?"
            ],
            "vb" =>
            [
                "Integer",
                "String",
                "System.Collections.Generic.List(Of Integer)",
                "Integer()",
                "Integer(,)",
                "System.Nullable(Of Integer)"
            ],
            _ =>
            [
                "int",
                "string",
                "System.Collections.Generic.List<int>",
                "int[]",
                "int[,]",
                "System.Nullable<int>"
            ]
        };
        string[] expectedRuntimeTypes =
        [
            "int",
            "string",
            "System.Collections.Generic.List<int>",
            "int[]",
            "int[,]",
            "int?"
        ];
        for (int index = 0; index < typeArguments.Length; index++)
        {
            string typeArgument = typeArguments[index];
            int frameId = await AssertStoppedFrameAsync(
                client,
                stoppedThreadId,
                sourcePath,
                breakpointLine).ConfigureAwait(false);
            string expression = sourceExtension == "vb"
                ? $"New {genericType}(Of {typeArgument})()"
                : $"new {genericType}<{typeArgument}>()";
            JsonElement constructed = await ReadEvaluationAsync(
                client,
                frameId,
                expression,
                success: true,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(
                index == 0 ? "generic=0" : "generic=null",
                constructed.GetProperty("result").GetString(),
                $"Unexpected compound generic construction value for '{expression}'.");
            Assert.AreEqual(
                expectedGenericType,
                constructed.GetProperty("type").GetString(),
                $"Unexpected compound generic construction type for '{expression}'.");
            Assert.IsGreaterThan(
                0,
                constructed.GetProperty("variablesReference").GetInt32(),
                $"The compound generic construction '{expression}' is not expandable.");
            using JsonDocument invalidated = await client
                .ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            AssertEvent(invalidated.RootElement, "invalidated");
            JsonElement valueField = (await ReadVariablesAsync(
                client,
                constructed.GetProperty("variablesReference").GetInt32())
                .ConfigureAwait(false)).Single();
            Assert.AreEqual(
                expectedRuntimeTypes[index],
                valueField.GetProperty("type").GetString(),
                $"The construction '{expression}' used the wrong closed runtime type.");
        }

        int invalidFrameId = await AssertStoppedFrameAsync(
            client,
            stoppedThreadId,
            sourcePath,
            breakpointLine).ConfigureAwait(false);
        string invalidExpression = sourceExtension == "vb"
            ? $"New {genericType}(Of Integer, String)()"
            : $"new {genericType}<int, string>()";
        JsonElement rejected = await ReadEvaluationAsync(
            client,
            invalidFrameId,
            invalidExpression,
            success: false,
            TestContext.CancellationToken).ConfigureAwait(false);
        Assert.Contains(
            "No loaded runtime type named",
            rejected.GetProperty("message").GetString()!,
            StringComparison.Ordinal,
            $"Unexpected generic-arity failure for '{invalidExpression}'.");
        JsonElement afterFailure = await ReadEvaluationAsync(
            client,
            invalidFrameId,
            "answer + 1",
            success: true,
            TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(
            "42",
            afterFailure.GetProperty("result").GetString(),
            "Generic construction failure made the stopped target uninspectable.");
    }
}
