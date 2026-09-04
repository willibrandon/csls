using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies debugger display metadata over real stopped runtime values.
/// </summary>
public sealed partial class DapSessionTests
{
    /// <summary>
    /// Applies safe display templates without changing source identity or executing properties.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task DebuggerDisplayMetadataShapesValuesAndFallsBackWithoutExecution()
    {
        string waitPath = Path.Join(
            Path.GetTempPath(),
            $"csls-debugger-display-{Guid.NewGuid():N}.signal");
        try
        {
            DapTestClient client = await StartStoppedFixtureAsync(waitPath).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable disposal = client.ConfigureAwait(false);
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
                .EnumerateArray().Single(scope =>
                    scope.GetProperty("name").GetString() == "Locals");
            JsonElement[] localValues = await ReadVariablesAsync(
                client,
                locals.GetProperty("variablesReference").GetInt32()).ConfigureAwait(false);

            JsonElement localDisplay = localValues.Single(variable =>
                variable.GetProperty("name").GetString() == "localDisplay");
            Assert.AreEqual(
                "{id}=54; label=alpha\\nbeta; nested=55",
                localDisplay.GetProperty("value").GetString());
            Assert.AreEqual("display-7", localDisplay.GetProperty("type").GetString());
            Assert.AreEqual(
                "localDisplay",
                localDisplay.GetProperty("evaluateName").GetString());

            JsonElement displayArray = localValues.Single(variable =>
                variable.GetProperty("name").GetString() == "localDisplayArray");
            JsonElement arrayElement = Assert.ContainsSingle(await ReadVariablesAsync(
                client,
                displayArray.GetProperty("variablesReference").GetInt32()).ConfigureAwait(false));
            Assert.AreEqual("child-54", arrayElement.GetProperty("name").GetString());
            Assert.AreEqual(
                "localDisplayArray[0]",
                arrayElement.GetProperty("evaluateName").GetString());
            Assert.AreEqual(
                "{id}=54; label=alpha\\nbeta; nested=55",
                arrayElement.GetProperty("value").GetString());

            JsonElement container = localValues.Single(variable =>
                variable.GetProperty("name").GetString() == "localDisplays");
            JsonElement[] children = await ReadVariablesAsync(
                client,
                container.GetProperty("variablesReference").GetInt32()).ConfigureAwait(false);

            JsonElement member = FindByEvaluateName(children, "localDisplays._member");
            Assert.AreEqual("member-73", member.GetProperty("name").GetString());
            Assert.AreEqual("member=73", member.GetProperty("value").GetString());
            Assert.AreEqual("member-display", member.GetProperty("type").GetString());
            Assert.IsGreaterThan(0, member.GetProperty("variablesReference").GetInt32());

            JsonElement memberPrimitive = FindByEvaluateName(
                children,
                "localDisplays._memberPrimitive");
            Assert.AreEqual("member-number", memberPrimitive.GetProperty("name").GetString());
            Assert.AreEqual("member-int=54", memberPrimitive.GetProperty("value").GetString());
            Assert.AreEqual(
                "member-int-type",
                memberPrimitive.GetProperty("type").GetString());
            Assert.AreEqual(
                0,
                memberPrimitive.GetProperty("variablesReference").GetInt32());

            JsonElement unsafeMember = FindByEvaluateName(
                children,
                "localDisplays._unsafeMember");
            Assert.AreEqual("_unsafeMember", unsafeMember.GetProperty("name").GetString());
            Assert.AreEqual("74", unsafeMember.GetProperty("value").GetString());
            Assert.AreEqual("int", unsafeMember.GetProperty("type").GetString());
            Assert.AreEqual(
                "0",
                FindByEvaluateName(children, "localDisplays._memberDisplayAccessCount")
                    .GetProperty("value")
                    .GetString());

            JsonElement direct = FindByEvaluateName(children, "localDisplays._direct");
            Assert.AreEqual("child-54", direct.GetProperty("name").GetString());
            Assert.AreEqual(
                "{id}=54; label=alpha\\nbeta; nested=55",
                direct.GetProperty("value").GetString());
            Assert.AreEqual("display-7", direct.GetProperty("type").GetString());
            Assert.IsGreaterThan(0, direct.GetProperty("variablesReference").GetInt32());

            JsonElement assembly = FindByEvaluateName(children, "localDisplays._assembly");
            Assert.AreEqual("assembly-61", assembly.GetProperty("name").GetString());
            Assert.AreEqual("assembly=61", assembly.GetProperty("value").GetString());
            Assert.AreEqual("assembly-target", assembly.GetProperty("type").GetString());

            JsonElement assemblyNamed = FindByEvaluateName(
                children,
                "localDisplays._assemblyNamed");
            Assert.AreEqual(
                "named-target-62",
                assemblyNamed.GetProperty("name").GetString());
            Assert.AreEqual(
                "named-target=62",
                assemblyNamed.GetProperty("value").GetString());
            Assert.AreEqual(
                "assembly-named-target",
                assemblyNamed.GetProperty("type").GetString());

            JsonElement inherited = FindByEvaluateName(children, "localDisplays._inherited");
            Assert.AreEqual("inherited-63", inherited.GetProperty("name").GetString());
            Assert.AreEqual("base=63", inherited.GetProperty("value").GetString());
            Assert.AreEqual("inherited-base", inherited.GetProperty("type").GetString());
            JsonElement[] inheritedFields = await ReadVariablesAsync(
                client,
                inherited.GetProperty("variablesReference").GetInt32()).ConfigureAwait(false);
            Assert.AreSequenceEqual(
                ["_derivedValue", "_baseValue"],
                inheritedFields.Select(field => field.GetProperty("name").GetString()).ToArray());

            JsonElement overridden = FindByEvaluateName(
                children,
                "localDisplays._overridden");
            Assert.AreEqual("derived-65", overridden.GetProperty("name").GetString());
            Assert.AreEqual("derived=65", overridden.GetProperty("value").GetString());
            Assert.AreEqual("derived-display", overridden.GetProperty("type").GetString());

            JsonElement unsafeDisplay = FindByEvaluateName(children, "localDisplays._unsafe");
            Assert.AreEqual("_unsafe", unsafeDisplay.GetProperty("name").GetString());
            Assert.AreEqual(
                "{Csls.TestProcessHost.UnsafeDebuggerDisplayFixture}",
                unsafeDisplay.GetProperty("value").GetString());
            JsonElement[] unsafeFields = await ReadVariablesAsync(
                client,
                unsafeDisplay.GetProperty("variablesReference").GetInt32()).ConfigureAwait(false);
            Assert.AreEqual(
                "0",
                unsafeFields.Single(field => field.GetProperty("name").GetString() == "_accessCount")
                    .GetProperty("value")
                    .GetString());

            JsonElement partiallyUnsafe = FindByEvaluateName(
                children,
                "localDisplays._partiallyUnsafe");
            Assert.AreEqual(
                "_partiallyUnsafe",
                partiallyUnsafe.GetProperty("name").GetString());
            Assert.AreEqual("safe=66", partiallyUnsafe.GetProperty("value").GetString());
            Assert.AreEqual(
                "Csls.TestProcessHost.PartiallyUnsafeDebuggerDisplayFixture",
                partiallyUnsafe.GetProperty("type").GetString());
            JsonElement[] partiallyUnsafeFields = await ReadVariablesAsync(
                client,
                partiallyUnsafe.GetProperty("variablesReference").GetInt32())
                .ConfigureAwait(false);
            Assert.AreEqual(
                "0",
                partiallyUnsafeFields.Single(field =>
                        field.GetProperty("name").GetString() == "_accessCount")
                    .GetProperty("value")
                    .GetString());

            Assert.AreEqual(
                "{Csls.TestProcessHost.MalformedDebuggerDisplayFixture}",
                FindByEvaluateName(children, "localDisplays._malformed")
                    .GetProperty("value")
                    .GetString());
            Assert.AreEqual(
                "{Csls.TestProcessHost.MissingDebuggerDisplayFixture}",
                FindByEvaluateName(children, "localDisplays._missing")
                    .GetProperty("value")
                    .GetString());
            Assert.AreEqual(
                "{Csls.TestProcessHost.NullPathDebuggerDisplayFixture}",
                FindByEvaluateName(children, "localDisplays._nullPath")
                    .GetProperty("value")
                    .GetString());
            Assert.AreEqual(
                "{Csls.TestProcessHost.CyclicDebuggerDisplayFixture}",
                FindByEvaluateName(children, "localDisplays._cyclic")
                    .GetProperty("value")
                    .GetString());

            JsonElement empty = FindByEvaluateName(children, "localDisplays._empty");
            Assert.AreEqual(string.Empty, empty.GetProperty("name").GetString());
            Assert.AreEqual(string.Empty, empty.GetProperty("value").GetString());
            Assert.AreEqual(string.Empty, empty.GetProperty("type").GetString());
            JsonElement nullDisplay = FindByEvaluateName(children, "localDisplays._null");
            Assert.AreEqual("_null", nullDisplay.GetProperty("name").GetString());
            Assert.AreEqual(
                "{Csls.TestProcessHost.NullDebuggerDisplayFixture}",
                nullDisplay.GetProperty("value").GetString());
            Assert.AreEqual(
                "Csls.TestProcessHost.NullDebuggerDisplayFixture",
                nullDisplay.GetProperty("type").GetString());
            Assert.AreEqual(
                "first",
                FindByEvaluateName(children, "localDisplays._multiple")
                    .GetProperty("value")
                    .GetString());

            await ResumeAndReleaseFixtureAsync(client, waitPath).ConfigureAwait(false);
            Assert.AreEqual(string.Empty, client.Diagnostics.ToString());
        }
        finally
        {
            File.Delete(waitPath);
        }
    }

    private static JsonElement FindByEvaluateName(
        IEnumerable<JsonElement> variables,
        string evaluateName) => variables.Single(variable =>
            variable.GetProperty("evaluateName").GetString() == evaluateName);
}
