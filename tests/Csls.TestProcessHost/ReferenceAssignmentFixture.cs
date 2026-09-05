using System.Runtime.CompilerServices;

namespace Csls.TestProcessHost;

/// <summary>
/// Keeps declared reference types and a managed by-reference alias available to debugger inspection.
/// </summary>
/// <typeparam name="TBase">The declared base reference type captured by the containing type.</typeparam>
internal static class ReferenceAssignmentFixture<TBase>
    where TBase : class
{
    /// <summary>
    /// Retains both reference shapes across a managed call without changing existing debugger fixture locals.
    /// </summary>
    /// <param name="path">The signal file consumed by the nested debugger fixture.</param>
    /// <param name="genericBase">A base-typed argument and field initializer.</param>
    /// <param name="genericDerived">A method-generic derived reference argument.</param>
    /// <typeparam name="TDerived">The declared derived type captured by the method.</typeparam>
    /// <returns>The nested fixture exit code.</returns>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static int Run<TDerived>(string path, TBase genericBase, TDerived genericDerived)
        where TDerived : TBase
    {
        TBase genericTarget = genericBase;
        TDerived genericSource = genericDerived;
        var genericHolder = new StrongBox<TBase>(genericBase);
        var factory = new ReferenceConversionFactory<Exception>(new ArgumentException("generic evaluated replacement"));
        string? target = "reference-assignment-value";
        ref string? alias = ref target;
        Exception baseTarget = new InvalidOperationException("original base");
        Exception? nullBaseTarget = null;
        object objectTarget = "original object";
        object boxedSource = 42;
        object[] objectArray = ["original element"];
        var derivedSource = new ArgumentException("replacement");
        Exception widenedSource = new ArgumentException("widened source");
        var derivedTarget = new ArgumentException("original derived");
        IEnumerable<char> interfaceTarget = new List<char>(['o', 'l', 'd']);
        string textSource = "interface replacement";
        Exception[] arrayTarget = [new InvalidOperationException("original array")];
        ArgumentException[] derivedArray = [new ArgumentException("replacement element")];
        IEnumerable<Exception> enumerableTarget = Array.Empty<Exception>();
        List<ArgumentException> enumerableSource = [new ArgumentException("covariant element")];
        List<Exception> invariantTarget = [new InvalidOperationException("original invariant")];
        int result = DebuggerFixture.WaitForSignal(
            path, "ready", 42, "answer", (ArgumentNumber: 42, ArgumentText: "argument"));
        GC.KeepAlive(target);
        GC.KeepAlive(alias);
        GC.KeepAlive(baseTarget);
        GC.KeepAlive(nullBaseTarget);
        GC.KeepAlive(objectTarget);
        GC.KeepAlive(boxedSource);
        GC.KeepAlive(objectArray);
        GC.KeepAlive(derivedSource);
        GC.KeepAlive(widenedSource);
        GC.KeepAlive(derivedTarget);
        GC.KeepAlive(interfaceTarget);
        GC.KeepAlive(textSource);
        GC.KeepAlive(arrayTarget);
        GC.KeepAlive(derivedArray);
        GC.KeepAlive(enumerableTarget);
        GC.KeepAlive(enumerableSource);
        GC.KeepAlive(invariantTarget);
        GC.KeepAlive(genericTarget);
        GC.KeepAlive(genericSource);
        GC.KeepAlive(genericHolder);
        GC.KeepAlive(factory);
        return result;
    }
}
