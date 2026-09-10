using Csls.Debugger.Contracts;

namespace Csls.Debugger;

/// <summary>
/// Records runtime values and their published ownership graph for one synchronous debugger operation.
/// </summary>
internal sealed class ManagedOperationValues
{
    private readonly Dictionary<int, ManagedValueHandle> _created = [];
    private readonly HashSet<int> _published = [];
    private readonly List<(ulong Address, ManagedResultsViewLifetime? Lifetime)> _heapOrigins = [];

    /// <summary>
    /// Gets the runtime values acquired by this operation.
    /// </summary>
    internal IEnumerable<ManagedValueHandle> Created => _created.Values;

    /// <summary>
    /// Gets the values transferred to the owning stopped generation.
    /// </summary>
    internal IReadOnlySet<int> Published => _published;

    /// <summary>
    /// Gets newly cached heap identities whose owners may require retirement.
    /// </summary>
    internal IReadOnlyList<(ulong Address, ManagedResultsViewLifetime? Lifetime)> HeapOrigins => _heapOrigins;

    /// <summary>
    /// Registers a newly acquired value before it can escape the current operation.
    /// </summary>
    /// <param name="value">The generation-owned value acquired during this operation.</param>
    internal void Track(ManagedValueHandle value) => _created.Add(value.Id, value);

    /// <summary>
    /// Records a heap-origin cache entry created during this operation.
    /// </summary>
    /// <param name="key">The stopped heap address and optional enumeration lifetime.</param>
    internal void TrackHeapOrigin((ulong Address, ManagedResultsViewLifetime? Lifetime) key) => _heapOrigins.Add(key);

    /// <summary>
    /// Transfers a published value and its physical owners to the stopped generation.
    /// </summary>
    /// <param name="reference">The result's expansion handle, or zero for a scalar result.</param>
    internal void Preserve(int reference)
    {
        var pending = new Stack<int>();
        pending.Push(reference);
        while (pending.TryPop(out int current))
        {
            if (!_created.TryGetValue(current, out ManagedValueHandle? value) || !_published.Add(current))
            {
                continue;
            }

            AddOrigin(value.Origin, pending);
            pending.Push(value.ProxyRawValueReference);
            pending.Push(value.ProxyStaticValueReference);
            if (value.SyntheticVariables is { } variables)
            {
                AddVariables(variables, pending);
            }
            if (value.ProxyProperties is { } properties)
            {
                foreach (ManagedDebuggerTypeProxyPropertyPresentation property in properties)
                {
                    AddVariables(property.Variables, pending);
                }
            }
        }
    }

    /// <summary>
    /// Transfers binding values to an active function evaluation until its completion retires the generation.
    /// </summary>
    internal void PreserveAll() => _published.UnionWith(_created.Keys);

    private static void AddVariables(IReadOnlyList<DebugVariableInfo> variables, Stack<int> pending)
    {
        foreach (DebugVariableInfo variable in variables)
        {
            pending.Push(variable.VariablesReference);
        }
    }

    private static void AddOrigin(ManagedValueOrigin? origin, Stack<int> pending)
    {
        while (origin is not null)
        {
            switch (origin)
            {
                case ManagedHeapValueOrigin heap:
                    pending.Push(heap.ValueReference);
                    return;
                case ManagedFieldValueOrigin field:
                    origin = field.Parent;
                    break;
                case ManagedArrayElementValueOrigin element:
                    origin = element.Parent;
                    break;
                default:
                    return;
            }
        }
    }
}
