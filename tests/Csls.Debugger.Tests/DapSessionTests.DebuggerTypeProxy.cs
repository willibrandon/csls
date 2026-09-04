using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies debugger type-proxy presentation over real CoreCLR function evaluation.
/// </summary>
public sealed partial class DapSessionTests
{
    /// <summary>
    /// Constructs a private proxy, flattens its public fields, and preserves Raw View.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task DebuggerTypeProxyShapesDefaultAndRawViews()
    {
        string waitPath = Path.Join(
            Path.GetTempPath(),
            $"csls-debugger-type-proxy-{Guid.NewGuid():N}.signal");
        try
        {
            DapTestClient client = await StartProxyFixtureAsync(waitPath).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable disposal = client.ConfigureAwait(false);
            JsonElement[] proxyFields = await ReadProxyLocalAsync(client, "localProxy")
                .ConfigureAwait(false);
            Assert.AreSequenceEqual(
                [
                    "ArrayValue",
                    "BoxedValue",
                    "ComputedValue",
                    "[0]",
                    "[1]",
                    "ProtectedValue",
                    "[0]",
                    "[1]",
                    "ThrowingValue",
                    "Value",
                    "_attributedProperty",
                    "_attributedValue",
                    "Static members",
                    "Raw View"
                ],
                proxyFields.Select(field => field.GetProperty("name").GetString()).ToArray(),
                client.Diagnostics.ToString());
            Assert.AreEqual("{int[2]}", proxyFields[0].GetProperty("value").GetString());
            Assert.AreEqual("55", proxyFields[1].GetProperty("value").GetString());
            Assert.AreEqual("52", proxyFields[2].GetProperty("value").GetString());
            Assert.AreEqual("43", proxyFields[3].GetProperty("value").GetString());
            Assert.AreEqual("44", proxyFields[4].GetProperty("value").GetString());
            Assert.AreEqual("45", proxyFields[5].GetProperty("value").GetString());
            Assert.AreEqual("48", proxyFields[6].GetProperty("value").GetString());
            Assert.AreEqual("49", proxyFields[7].GetProperty("value").GetString());
            Assert.StartsWith(
                "<error: System.InvalidOperationException:",
                proxyFields[8].GetProperty("value").GetString());
            Assert.AreEqual("42", proxyFields[9].GetProperty("value").GetString());
            Assert.AreEqual("47", proxyFields[10].GetProperty("value").GetString());
            Assert.AreEqual("46", proxyFields[11].GetProperty("value").GetString());
            Assert.DoesNotContain(
                "_privateValue",
                proxyFields.Select(field => field.GetProperty("name").GetString()));
            Assert.DoesNotContain(
                "HiddenValue",
                proxyFields.Select(field => field.GetProperty("name").GetString()));

            JsonElement[] propertyArray = await ReadVariablesAsync(
                client,
                proxyFields[0].GetProperty("variablesReference").GetInt32())
                .ConfigureAwait(false);
            Assert.AreSequenceEqual(
                ["43", "44"],
                propertyArray.Select(element => element.GetProperty("value").GetString())
                    .ToArray());

            JsonElement staticView = proxyFields[12];
            Assert.AreEqual(
                "virtual",
                staticView.GetProperty("presentationHint").GetProperty("kind").GetString());
            JsonElement[] staticMembers = await ReadVariablesAsync(
                client,
                staticView.GetProperty("variablesReference").GetInt32()).ConfigureAwait(false);
            Assert.AreSequenceEqual(
                [
                    "StaticProperty",
                    "[0]",
                    "[1]",
                    "ThrowingStatic",
                    "s_attributedStatic",
                    "s_staticField",
                    "[0]",
                    "[1]"
                ],
                staticMembers.Select(field => field.GetProperty("name").GetString()).ToArray());
            Assert.AreSequenceEqual(
                ["63", "64", "65"],
                staticMembers.Take(3)
                    .Select(field => field.GetProperty("value").GetString()).ToArray());
            Assert.StartsWith(
                "<error: System.InvalidOperationException:",
                staticMembers[3].GetProperty("value").GetString());
            Assert.AreSequenceEqual(
                ["66", "60", "61", "62"],
                staticMembers.Skip(4)
                    .Select(field => field.GetProperty("value").GetString()).ToArray());
            int staticReference = staticView.GetProperty("variablesReference").GetInt32();
            JsonElement[] indexedStatics = await ReadVariablesPageAsync(
                client, staticReference, start: 1, count: 2, filter: "indexed").ConfigureAwait(false);
            Assert.AreSequenceEqual(
                ["65", "61"],
                indexedStatics.Select(field => field.GetProperty("value").GetString()).ToArray());
            JsonElement[] namedStatics = await ReadVariablesPageAsync(
                client, staticReference, start: 0, count: 0, filter: "named").ConfigureAwait(false);
            Assert.AreSequenceEqual(
                ["StaticProperty", "ThrowingStatic", "s_attributedStatic", "s_staticField"],
                namedStatics.Select(field => field.GetProperty("name").GetString()).ToArray());

            JsonElement rawView = proxyFields[13];
            Assert.AreEqual(
                "virtual",
                rawView.GetProperty("presentationHint").GetProperty("kind").GetString());
            JsonElement rawField = Assert.ContainsSingle(await ReadVariablesAsync(
                client,
                rawView.GetProperty("variablesReference").GetInt32()).ConfigureAwait(false));
            Assert.AreEqual("_rawValue", rawField.GetProperty("name").GetString());
            Assert.AreEqual("41", rawField.GetProperty("value").GetString());

            JsonElement[] firstPage = await ReadProxyLocalPageAsync(
                client,
                "localProxy",
                start: 0,
                count: 2).ConfigureAwait(false);
            Assert.AreSequenceEqual(
                ["ArrayValue", "BoxedValue"],
                firstPage.Select(field => field.GetProperty("name").GetString()).ToArray());
            JsonElement[] secondPage = await ReadProxyLocalPageAsync(
                client,
                "localProxy",
                start: 2,
                count: 2).ConfigureAwait(false);
            Assert.AreSequenceEqual(
                ["ComputedValue", "[0]"],
                secondPage.Select(field => field.GetProperty("name").GetString()).ToArray());
            JsonElement[] thirdPage = await ReadProxyLocalPageAsync(
                client,
                "localProxy",
                start: 4,
                count: 2).ConfigureAwait(false);
            Assert.AreSequenceEqual(
                ["[1]", "ProtectedValue"],
                thirdPage.Select(field => field.GetProperty("name").GetString()).ToArray());
            JsonElement[] fourthPage = await ReadProxyLocalPageAsync(
                client,
                "localProxy",
                start: 6,
                count: 2).ConfigureAwait(false);
            Assert.AreSequenceEqual(
                ["[0]", "[1]"],
                fourthPage.Select(field => field.GetProperty("name").GetString()).ToArray());
            JsonElement[] fifthPage = await ReadProxyLocalPageAsync(
                client,
                "localProxy",
                start: 8,
                count: 2).ConfigureAwait(false);
            Assert.AreSequenceEqual(
                ["ThrowingValue", "Value"],
                fifthPage.Select(field => field.GetProperty("name").GetString()).ToArray());
            JsonElement[] sixthPage = await ReadProxyLocalPageAsync(
                client,
                "localProxy",
                start: 10,
                count: 2).ConfigureAwait(false);
            Assert.AreSequenceEqual(
                ["_attributedProperty", "_attributedValue"],
                sixthPage.Select(field => field.GetProperty("name").GetString()).ToArray());
            JsonElement[] finalPage = await ReadProxyLocalPageAsync(
                client,
                "localProxy",
                start: 12,
                count: 2).ConfigureAwait(false);
            Assert.AreSequenceEqual(
                ["Static members", "Raw View"],
                finalPage.Select(field => field.GetProperty("name").GetString()).ToArray());
            Assert.IsEmpty(await ReadProxyLocalPageAsync(
                client,
                "localProxy",
                start: 14,
                count: 2).ConfigureAwait(false));
            JsonElement[] indexedProxy = await ReadProxyLocalPageAsync(
                client, "localProxy", start: 1, count: 2, filter: "indexed").ConfigureAwait(false);
            Assert.AreSequenceEqual(
                ["44", "48"],
                indexedProxy.Select(field => field.GetProperty("value").GetString()).ToArray());
            JsonElement[] namedProxy = await ReadProxyLocalPageAsync(
                client, "localProxy", start: 2, count: 2, filter: "named").ConfigureAwait(false);
            Assert.AreSequenceEqual(
                ["ComputedValue", "ProtectedValue"],
                namedProxy.Select(field => field.GetProperty("name").GetString()).ToArray());

            await DisconnectStoppedSessionAsync(client).ConfigureAwait(false);
            Assert.AreEqual(
                0,
                await client.WaitForExitAsync(TestContext.CancellationToken)
                    .ConfigureAwait(false));
            Assert.AreEqual(string.Empty, client.Diagnostics.ToString());
        }
        finally
        {
            File.Delete(waitPath);
        }
    }

    /// <summary>
    /// Supports generic, inherited, assembly-targeted, and throwing proxy declarations.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task DebuggerTypeProxyHonorsRuntimeBindingAndFailureIsolation()
    {
        string waitPath = Path.Join(
            Path.GetTempPath(),
            $"csls-debugger-type-proxy-binding-{Guid.NewGuid():N}.signal");
        try
        {
            DapTestClient client = await StartProxyFixtureAsync(waitPath).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable disposal = client.ConfigureAwait(false);

            JsonElement[] generic = await ReadProxyLocalAsync(client, "localGenericProxy")
                .ConfigureAwait(false);
            Assert.AreSequenceEqual(
                ["Value", "Raw View"],
                generic.Select(field => field.GetProperty("name").GetString()).ToArray());
            Assert.AreEqual("49", generic[0].GetProperty("value").GetString());
            Assert.AreEqual("int", generic[0].GetProperty("type").GetString());

            JsonElement[] closedGeneric = await ReadProxyLocalAsync(
                client,
                "localClosedGenericProxy").ConfigureAwait(false);
            Assert.AreSequenceEqual(
                ["Value", "Raw View"],
                closedGeneric.Select(field => field.GetProperty("name").GetString()).ToArray());
            Assert.AreEqual("int[]", closedGeneric[0].GetProperty("type").GetString());
            JsonElement element = Assert.ContainsSingle(await ReadVariablesAsync(
                client,
                closedGeneric[0].GetProperty("variablesReference").GetInt32())
                .ConfigureAwait(false));
            Assert.AreEqual("52", element.GetProperty("value").GetString());

            JsonElement[] inherited = await ReadProxyLocalAsync(client, "localInheritedProxy")
                .ConfigureAwait(false);
            Assert.AreSequenceEqual(
                ["Value", "Raw View"],
                inherited.Select(field => field.GetProperty("name").GetString()).ToArray());
            Assert.AreEqual("45", inherited[0].GetProperty("value").GetString());

            JsonElement[] assembly = await ReadProxyLocalAsync(client, "localAssemblyProxy")
                .ConfigureAwait(false);
            Assert.AreSequenceEqual(
                ["Value", "Raw View"],
                assembly.Select(field => field.GetProperty("name").GetString()).ToArray());
            Assert.AreEqual("47", assembly[0].GetProperty("value").GetString());

            JsonElement[] arityMismatch = await ReadUnproxiedLocalAsync(
                client,
                "localArityMismatchProxy").ConfigureAwait(false);
            JsonElement ordinaryValue = Assert.ContainsSingle(arityMismatch);
            Assert.AreEqual("Value", ordinaryValue.GetProperty("name").GetString());
            Assert.AreEqual("51", ordinaryValue.GetProperty("value").GetString());

            JsonElement[] assemblyNamed = await ReadProxyLocalAsync(
                client,
                "localAssemblyNamedProxy").ConfigureAwait(false);
            Assert.AreSequenceEqual(
                ["Value", "Raw View"],
                assemblyNamed.Select(field => field.GetProperty("name").GetString()).ToArray());
            Assert.AreEqual("50", assemblyNamed[0].GetProperty("value").GetString());

            JsonElement[] throwing = await ReadProxyLocalAsync(client, "localThrowingProxy")
                .ConfigureAwait(false);
            JsonElement fallback = Assert.ContainsSingle(throwing);
            Assert.AreEqual("_rawValue", fallback.GetProperty("name").GetString());
            Assert.AreEqual("48", fallback.GetProperty("value").GetString());

            await DisconnectStoppedSessionAsync(client).ConfigureAwait(false);
            Assert.AreEqual(
                0,
                await client.WaitForExitAsync(TestContext.CancellationToken)
                    .ConfigureAwait(false));
            Assert.AreEqual(string.Empty, client.Diagnostics.ToString());
        }
        finally
        {
            File.Delete(waitPath);
        }
    }

    private async Task<JsonElement[]> ReadProxyLocalAsync(
        DapTestClient client,
        string localName)
    {
        JsonElement frame = await GetFixtureFrameAsync(client).ConfigureAwait(false);
        int scopesSequence = await client.SendRequestAsync(
            "scopes",
            writer => WriteFrameArguments(writer, frame.GetProperty("id").GetInt32()),
            TestContext.CancellationToken).ConfigureAwait(false);
        using JsonDocument scopes = await client
            .ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        AssertResponse(scopes.RootElement, scopesSequence, "scopes", success: true);
        JsonElement locals = scopes.RootElement.GetProperty("body").GetProperty("scopes")
            .EnumerateArray().Single(scope => scope.GetProperty("name").GetString() == "Locals");
        JsonElement local = (await ReadVariablesAsync(
            client,
            locals.GetProperty("variablesReference").GetInt32()).ConfigureAwait(false))
            .Single(variable => variable.GetProperty("name").GetString() == localName);
        JsonElement[] fields = await ReadVariablesAsync(
            client,
            local.GetProperty("variablesReference").GetInt32()).ConfigureAwait(false);
        using JsonDocument invalidated = await client
            .ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        AssertEvent(invalidated.RootElement, "invalidated");
        return fields;
    }

    private async Task<JsonElement[]> ReadProxyLocalPageAsync(
        DapTestClient client,
        string localName,
        int start,
        int count,
        string? filter = null)
    {
        JsonElement frame = await GetFixtureFrameAsync(client).ConfigureAwait(false);
        int scopesSequence = await client.SendRequestAsync(
            "scopes",
            writer => WriteFrameArguments(writer, frame.GetProperty("id").GetInt32()),
            TestContext.CancellationToken).ConfigureAwait(false);
        using JsonDocument scopes = await client
            .ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        AssertResponse(scopes.RootElement, scopesSequence, "scopes", success: true);
        JsonElement locals = scopes.RootElement.GetProperty("body").GetProperty("scopes")
            .EnumerateArray().Single(scope => scope.GetProperty("name").GetString() == "Locals");
        JsonElement local = (await ReadVariablesAsync(
            client,
            locals.GetProperty("variablesReference").GetInt32()).ConfigureAwait(false))
            .Single(variable => variable.GetProperty("name").GetString() == localName);
        JsonElement[] fields = await ReadVariablesPageAsync(
            client,
            local.GetProperty("variablesReference").GetInt32(),
            start,
            count,
            filter).ConfigureAwait(false);
        using JsonDocument invalidated = await client
            .ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        AssertEvent(invalidated.RootElement, "invalidated");
        return fields;
    }

    private async Task<JsonElement[]> ReadUnproxiedLocalAsync(
        DapTestClient client,
        string localName)
    {
        JsonElement frame = await GetFixtureFrameAsync(client).ConfigureAwait(false);
        int scopesSequence = await client.SendRequestAsync(
            "scopes",
            writer => WriteFrameArguments(writer, frame.GetProperty("id").GetInt32()),
            TestContext.CancellationToken).ConfigureAwait(false);
        using JsonDocument scopes = await client
            .ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        AssertResponse(scopes.RootElement, scopesSequence, "scopes", success: true);
        JsonElement locals = scopes.RootElement.GetProperty("body").GetProperty("scopes")
            .EnumerateArray().Single(scope => scope.GetProperty("name").GetString() == "Locals");
        JsonElement local = (await ReadVariablesAsync(
            client,
            locals.GetProperty("variablesReference").GetInt32()).ConfigureAwait(false))
            .Single(variable => variable.GetProperty("name").GetString() == localName);
        return await ReadVariablesAsync(
            client,
            local.GetProperty("variablesReference").GetInt32()).ConfigureAwait(false);
    }

    private Task<DapTestClient> StartProxyFixtureAsync(
        string waitPath,
        bool isolateResultsViewAssembly = false,
        bool supportsVariablePaging = true) => StartPresentationFixtureAsync(
            waitPath,
            "DebuggerFixture.cs",
            "Console.Write(announcement);",
            isolateResultsViewAssembly
                ? "--debugger-results-view-context-fixture" : "--debugger-fixture",
            supportsVariablePaging: supportsVariablePaging);

    private async Task<DapTestClient> StartPresentationFixtureAsync(
        string waitPath,
        string sourceFileName,
        string breakpointText,
        string fixtureCommand,
        string? fixtureAssemblyPath = null,
        bool supportsVariablePaging = true)
    {
        string sourcePath = Path.Join(
            FindRepositoryRoot(),
            "tests",
            "Csls.TestProcessHost",
            sourceFileName);
        int breakpointLine = FindSourceLine(
            await File.ReadAllLinesAsync(
                sourcePath,
                TestContext.CancellationToken).ConfigureAwait(false),
            breakpointText);
        DapTestClient client = await DapTestClient
            .CreateAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        _ = await client.SendRequestAsync(
            "initialize",
            writer =>
            {
                writer.WriteStartObject();
                writer.WriteBoolean("supportsVariablePaging", supportsVariablePaging);
                writer.WriteEndObject();
            },
            TestContext.CancellationToken).ConfigureAwait(false);
        using JsonDocument initialize = await client
            .ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        string[] fixtureArguments = fixtureAssemblyPath is null
            ? [fixtureCommand, waitPath] : [fixtureCommand, waitPath, fixtureAssemblyPath];
        int launchSequence = await client.SendRequestAsync(
            "launch",
            writer => WriteLaunchArguments(
                writer,
                ResolveTestProcessHost(),
                fixtureArguments,
                wait: true,
                noDebug: false,
                suppressJitOptimizations: true),
            TestContext.CancellationToken).ConfigureAwait(false);
        using JsonDocument initialized = await client
            .ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        AssertEvent(initialized.RootElement, "initialized");
        int breakpointSequence = await client.SendRequestAsync(
            "setBreakpoints",
            writer => WriteSourceBreakpointArguments(writer, sourcePath, breakpointLine),
            TestContext.CancellationToken).ConfigureAwait(false);
        using JsonDocument breakpointResponse = await client
            .ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        AssertResponse(
            breakpointResponse.RootElement,
            breakpointSequence,
            "setBreakpoints",
            success: true);
        int configurationSequence = await client.SendRequestAsync(
            "configurationDone",
            WriteEmptyObject,
            TestContext.CancellationToken).ConfigureAwait(false);
        await ReadFunctionEvaluationStopAsync(
            client,
            configurationSequence,
            launchSequence).ConfigureAwait(false);
        return client;
    }
}
