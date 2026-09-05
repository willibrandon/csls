using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies enumerable presentation availability and runtime metadata identity boundaries.
/// </summary>
public sealed partial class DapSessionTests
{
    /// <summary>
    /// Retains lazy enumeration when ordinary debugger metadata exposes a Raw View.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task ResultsViewRemainsAvailableThroughOrdinaryRawView()
    {
        string waitPath = CreateResultsViewSignalPath();
        try
        {
            DapTestClient client = await StartProxyFixtureAsync(waitPath).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable disposal = client.ConfigureAwait(false);
            JsonElement[] fields = await ReadUnproxiedLocalAsync(client, "localResultsViewRaw")
                .ConfigureAwait(false);
            Assert.DoesNotContain("_hidden", fields.Select(field => field.GetProperty("name").GetString()));
            JsonElement raw = Assert.ContainsSingle(fields.Where(field =>
                field.GetProperty("name").GetString() == "Raw View"));
            JsonElement[] rawFields = await ReadVariablesAsync(
                client, raw.GetProperty("variablesReference").GetInt32()).ConfigureAwait(false);
            Assert.AreEqual("173", rawFields.Single(field =>
                field.GetProperty("name").GetString() == "_hidden").GetProperty("value").GetString());
            JsonElement row = Assert.ContainsSingle(rawFields.Where(field =>
                field.GetProperty("name").GetString() == "Results View"));
            await AssertEnumerationCountAsync(client, "localResultsViewRaw", 0).ConfigureAwait(false);
            JsonElement[] items = await ExpandResultsViewAsync(
                client, row.GetProperty("variablesReference").GetInt32()).ConfigureAwait(false);
            Assert.AreSequenceEqual(
                ["171", "172"],
                items.Select(item => item.GetProperty("value").GetString()).ToArray());
            await AssertEnumerationCountAsync(client, "localResultsViewRaw", 1).ConfigureAwait(false);
            await FinishResultsViewSessionAsync(client).ConfigureAwait(false);
        }
        finally
        {
            File.Delete(waitPath);
        }
    }

    /// <summary>
    /// Retains Results View when construction of the target's debugger proxy throws.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task ResultsViewRemainsAvailableAfterDebuggerProxyFailure()
    {
        string waitPath = CreateResultsViewSignalPath();
        try
        {
            DapTestClient client = await StartProxyFixtureAsync(waitPath).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable disposal = client.ConfigureAwait(false);
            JsonElement[] fields = await ReadProxyLocalAsync(client, "localResultsViewFailedProxy")
                .ConfigureAwait(false);
            JsonElement row = Assert.ContainsSingle(fields.Where(field =>
                field.GetProperty("name").GetString() == "Results View"));
            Assert.AreEqual("0", fields.Single(field =>
                field.GetProperty("name").GetString() == "_enumerationCount")
                .GetProperty("value").GetString());
            JsonElement[] items = await ExpandResultsViewAsync(
                client, row.GetProperty("variablesReference").GetInt32()).ConfigureAwait(false);
            Assert.AreSequenceEqual(
                ["181", "182"],
                items.Select(item => item.GetProperty("value").GetString()).ToArray());
            JsonElement[] afterEnumeration = await ReadProxyLocalAsync(client, "localResultsViewFailedProxy")
                .ConfigureAwait(false);
            Assert.AreEqual("1", afterEnumeration.Single(field =>
                field.GetProperty("name").GetString() == "_enumerationCount")
                .GetProperty("value").GetString());
            await FinishResultsViewSessionAsync(client).ConfigureAwait(false);
        }
        finally
        {
            File.Delete(waitPath);
        }
    }

    /// <summary>
    /// Omits unavailable Results View without sacrificing ordinary field and array inspection.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task ResultsViewOmitsUnavailableRuntimeDebugView()
    {
        string waitPath = CreateResultsViewSignalPath();
        try
        {
            DapTestClient client = await StartPresentationFixtureAsync(
                waitPath,
                "ResultsViewAvailabilityDebuggerFixture.cs",
                "Console.Write(announcement);",
                "--debugger-results-view-unavailable-fixture").ConfigureAwait(false);
            await using ConfiguredAsyncDisposable disposal = client.ConfigureAwait(false);
            int modulesSequence = await client.SendRequestAsync(
                "modules", WriteEmptyObject, TestContext.CancellationToken).ConfigureAwait(false);
            using JsonDocument modules = await client.ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            AssertResponse(modules.RootElement, modulesSequence, "modules", success: true);
            Assert.DoesNotContain(
                "System.Linq.dll",
                modules.RootElement.GetProperty("body").GetProperty("modules").EnumerateArray()
                    .Where(module => module.TryGetProperty("path", out _))
                    .Select(module => Path.GetFileName(module.GetProperty("path").GetString())),
                StringComparer.OrdinalIgnoreCase);
            JsonElement[] fields = await ReadUnproxiedLocalAsync(client, "localResultsViewUnavailable")
                .ConfigureAwait(false);
            Assert.DoesNotContain("Results View", fields.Select(field => field.GetProperty("name").GetString()));
            Assert.AreEqual("0", fields.Single(field =>
                field.GetProperty("name").GetString() == "_enumerationCount")
                .GetProperty("value").GetString());
            JsonElement array = Assert.ContainsSingle(fields.Where(field =>
                field.GetProperty("name").GetString() == "_items"));
            JsonElement[] items = await ReadVariablesAsync(
                client, array.GetProperty("variablesReference").GetInt32()).ConfigureAwait(false);
            Assert.AreSequenceEqual(
                ["161", "162"],
                items.Select(item => item.GetProperty("value").GetString()).ToArray());
            await AssertEnumerationCountAsync(client, "localResultsViewUnavailable", 0)
                .ConfigureAwait(false);
            await FinishResultsViewSessionAsync(client).ConfigureAwait(false);
        }
        finally
        {
            File.Delete(waitPath);
        }
    }

    /// <summary>
    /// Rejects a file-backed user exception that imitates the runtime empty-sentinel type name.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task ResultsViewDoesNotTrustForgedEmptySentinelIdentity()
    {
        DirectoryInfo directory = Directory.CreateTempSubdirectory("csls-results-view-sentinel-");
        string waitPath = Path.Join(directory.FullName, "wait.signal");
        string assemblyPath = Path.Join(directory.FullName, "Csls.ResultsViewHostileException.dll");
        try
        {
            EmitResultsViewHostileException(assemblyPath);
            DapTestClient client = await StartPresentationFixtureAsync(
                waitPath,
                "DebuggerFixture.cs",
                "Console.Write(announcement);",
                "--debugger-results-view-spoof-fixture",
                assemblyPath).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable disposal = client.ConfigureAwait(false);
            JsonElement row = await ReadResultsViewRowAsync(client, "localResultsViewSpoofedException")
                .ConfigureAwait(false);
            int sequence = await client.SendRequestAsync(
                "variables",
                writer => WriteResultsViewReference(writer, row.GetProperty("variablesReference").GetInt32()),
                TestContext.CancellationToken).ConfigureAwait(false);
            using JsonDocument response = await client.ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            AssertResponse(response.RootElement, sequence, "variables", success: false);
            string? message = response.RootElement.GetProperty("message").GetString();
            Assert.IsNotNull(message);
            Assert.Contains("System.Linq.SystemCore_EnumerableDebugViewEmptyException", message);
            Assert.Contains("forged empty sentinel", message);
            await ReadResultsViewInvalidationAsync(client).ConfigureAwait(false);
            await AssertEnumerationCountAsync(client, "localResultsViewSpoofedException", 1)
                .ConfigureAwait(false);
            JsonElement number = await ReadResultsViewLocalAsync(client, "localNumber")
                .ConfigureAwait(false);
            Assert.AreEqual("43", number.GetProperty("value").GetString());
            await FinishResultsViewSessionAsync(client).ConfigureAwait(false);
        }
        finally
        {
            File.Delete(waitPath);
            File.Delete(assemblyPath);
            directory.Delete();
        }
    }

    private static void EmitResultsViewHostileException(string assemblyPath)
    {
        var assembly = new PersistedAssemblyBuilder(
            new AssemblyName("System.Linq"), typeof(object).Assembly);
        ModuleBuilder module = assembly.DefineDynamicModule("System.Linq");
        TypeBuilder exceptionType = module.DefineType(
            "System.Linq.SystemCore_EnumerableDebugViewEmptyException",
            TypeAttributes.Public | TypeAttributes.Sealed,
            typeof(Exception));
        ConstructorBuilder constructor = exceptionType.DefineConstructor(
            MethodAttributes.Public, CallingConventions.Standard, Type.EmptyTypes);
        ILGenerator body = constructor.GetILGenerator();
        body.Emit(OpCodes.Ldarg_0);
        body.Emit(OpCodes.Ldstr, "forged empty sentinel");
        body.Emit(OpCodes.Call, typeof(Exception).GetConstructor([typeof(string)])
            ?? throw new InvalidOperationException("The runtime exception constructor is unavailable."));
        body.Emit(OpCodes.Ret);
        _ = exceptionType.CreateType();
        assembly.Save(assemblyPath);
    }
}
