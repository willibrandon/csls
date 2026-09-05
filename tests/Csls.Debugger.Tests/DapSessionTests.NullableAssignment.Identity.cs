using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Rejects nullable lookalikes loaded from actual hostile PE files through the debugger transport.
/// </summary>
public sealed partial class DapSessionTests
{
    /// <summary>
    /// Does not authorize null conversion from a generic type name and matching nullable field layout.
    /// </summary>
    /// <param name="setVariable">Whether to assign through the hostile carrier's field container.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task NullableAssignmentRejectsLookalikeRuntimeType(bool setVariable)
    {
        DirectoryInfo directory = Directory.CreateTempSubdirectory("csls-nullable-identity-");
        string waitPath = Path.Join(directory.FullName, "continue.signal");
        string assemblyPath = Path.Join(directory.FullName, "NullableIdentityFixture.dll");
        try
        {
            EmitNullableLookalike(assemblyPath);
            DapTestClient client = await StartNullableAssignmentFixtureAsync(waitPath, assemblyPath)
                .ConfigureAwait(false);
            await using ConfiguredAsyncDisposable disposal = client.ConfigureAwait(false);
            JsonElement frame = await GetFixtureFrameAsync(client).ConfigureAwait(false);
            int frameId = frame.GetProperty("id").GetInt32();
            JsonElement carrier = await ReadEvaluationAsync(client, frameId, "hostile", success: true,
                TestContext.CancellationToken).ConfigureAwait(false);
            int container = carrier.GetProperty("variablesReference").GetInt32();
            Assert.IsGreaterThan(0, container);
            await AssertStructAssignmentEvaluationAsync(client, frameId, "hostile.Value",
                "{System.Nullable<int>}", "System.Nullable<int>").ConfigureAwait(false);
            await AssertNullableLookalikeStorageAsync(client, frameId, "true", "317").ConfigureAwait(false);
            _ = setVariable
                ? await ReadSetVariableAsync(client, container, "Value", "null", success: false,
                    TestContext.CancellationToken).ConfigureAwait(false)
                : await ReadSetExpressionAsync(client, frameId, "hostile.Value", "null", success: false,
                    TestContext.CancellationToken).ConfigureAwait(false);
            await AssertNullableLookalikeStorageAsync(client, frameId, "true", "317").ConfigureAwait(false);

            // An ordinary struct still has a valid default even when its name imitates Nullable<T>.
            _ = await ReadSetExpressionAsync(client, frameId, "hostile.Value", "default", success: true,
                TestContext.CancellationToken).ConfigureAwait(false);
            await AssertNullableLookalikeStorageAsync(client, frameId, "false", "0").ConfigureAwait(false);
            JsonElement unchangedFrame = await GetFixtureFrameAsync(client).ConfigureAwait(false);
            Assert.AreEqual(frameId, unchangedFrame.GetProperty("id").GetInt32());
            await FinishResultsViewSessionAsync(client).ConfigureAwait(false);
        }
        finally
        {
            File.Delete(waitPath);
            File.Delete(assemblyPath);
            directory.Delete();
        }
    }

    private async Task AssertNullableLookalikeStorageAsync(
        DapTestClient client, int frameId, string presence, string payload)
    {
        await AssertStructAssignmentEvaluationAsync(client, frameId, "hostile.Value.hasValue", presence, "bool")
            .ConfigureAwait(false);
        await AssertStructAssignmentEvaluationAsync(client, frameId, "hostile.Value.value", payload, "int")
            .ConfigureAwait(false);
    }

    private static void EmitNullableLookalike(string assemblyPath)
    {
        var assembly = new PersistedAssemblyBuilder(
            new AssemblyName("Csls.NullableIdentityFixture"), typeof(object).Assembly);
        ModuleBuilder module = assembly.DefineDynamicModule("Csls.NullableIdentityFixture");
        TypeBuilder nullable = module.DefineType("System.Nullable`1",
            TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.SequentialLayout, typeof(ValueType));
        GenericTypeParameterBuilder parameter = nullable.DefineGenericParameters("T")[0];
        parameter.SetGenericParameterAttributes(
            GenericParameterAttributes.NotNullableValueTypeConstraint | GenericParameterAttributes.DefaultConstructorConstraint);
        parameter.SetBaseTypeConstraint(typeof(ValueType));
        _ = nullable.DefineField("hasValue", typeof(bool), FieldAttributes.Public);
        _ = nullable.DefineField("value", parameter, FieldAttributes.Public);
        Type nullableDefinition = nullable.CreateType();
        TypeBuilder carrier = module.DefineType("Csls.NullableIdentityFixture.Carrier",
            TypeAttributes.Public | TypeAttributes.Sealed, typeof(object));
        _ = carrier.DefineField("Value", nullableDefinition.MakeGenericType(typeof(int)), FieldAttributes.Public);
        _ = carrier.DefineDefaultConstructor(MethodAttributes.Public);
        _ = carrier.CreateType();
        assembly.Save(assemblyPath);
    }
}
