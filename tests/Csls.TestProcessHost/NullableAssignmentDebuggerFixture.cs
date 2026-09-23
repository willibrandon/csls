using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;

namespace Csls.TestProcessHost;

/// <summary>
/// Retains nullable tuple storage and optional file-backed hostile types across a debugger stop.
/// </summary>
internal static class NullableAssignmentDebuggerFixture
{
    /// <summary>
    /// Prepares reference-containing values and an optional nullable lookalike in a separate loaded module.
    /// </summary>
    /// <param name="path">The signal file consumed by the nested debugger fixture.</param>
    /// <param name="assemblyPath">The optional assembly defining the hostile nullable carrier.</param>
    /// <returns>The nested fixture exit code.</returns>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static int Run(string path, string? assemblyPath = null) =>
        WaitForSignal(path, (211, "argument"), assemblyPath is null ? null : LoadHostileCarrier(assemblyPath));

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int WaitForSignal(
        string path,
        (int ArgumentNumber, string ArgumentText)? argument,
        object? hostile)
    {
        (int Number, string Text)? local = (212, "local");
        var field = new StrongBox<(int Code, string Label)?>((213, "field"));
        (int Index, string Element)?[] array = [(214, "array"), (215, "untouched")];
        var pair = new KeyValuePair<(int KeyNumber, string KeyText), (int ValueNumber, string ValueText)?>(
            (216, "key"), (217, "value"));
        object? typedNull = null;
        int result = DebuggerFixture.WaitForSignal(
            path, "ready", 42, "answer", (ArgumentNumber: 42, ArgumentText: "argument"));
        GC.KeepAlive(argument);
        GC.KeepAlive(hostile);
        GC.KeepAlive(local);
        GC.KeepAlive(field);
        GC.KeepAlive(array);
        GC.KeepAlive(pair);
        GC.KeepAlive(typedNull);
        return result;
    }

    private static object LoadHostileCarrier(string assemblyPath)
    {
        var context = new AssemblyLoadContext("NullableAssignmentIdentity");
        Type carrierType = context.LoadFromAssemblyPath(assemblyPath)
            .GetType("Csls.NullableIdentityFixture.Carrier", throwOnError: true)!;
        object carrier = Activator.CreateInstance(carrierType)
            ?? throw new InvalidOperationException("The hostile carrier could not be created.");
        FieldInfo storage = RequireField(carrierType, "Value");
        object payload = Activator.CreateInstance(storage.FieldType)
            ?? throw new InvalidOperationException("The hostile nullable value could not be created.");
        RequireField(storage.FieldType, "hasValue").SetValue(payload, true);
        RequireField(storage.FieldType, "value").SetValue(payload, 317);
        storage.SetValue(carrier, payload);
        return carrier;
    }

    private static FieldInfo RequireField(Type type, string name) =>
        type.GetField(name) ?? throw new InvalidOperationException($"The hostile field '{name}' is missing.");
}
