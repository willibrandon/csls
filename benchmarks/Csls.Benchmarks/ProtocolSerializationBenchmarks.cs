using BenchmarkDotNet.Attributes;
using Csls.Protocol;
using System.Text.Json;

namespace Csls.Benchmarks;

/// <summary>
/// Measures source-generated JSON serialization for representative LSP payloads.
/// </summary>
[BenchmarkCategory("Protocol")]
[MemoryDiagnoser]
public class ProtocolSerializationBenchmarks
{
    private CompletionList _completionList = null!;
    private byte[] _payload = null!;
    private JsonSerializerOptions _serializerOptions = null!;

    /// <summary>
    /// Gets or sets the number of completion candidates in the protocol payload.
    /// </summary>
    [Params(16, 128)]
    public int ItemCount { get; set; }

    /// <summary>
    /// Creates the immutable payload and serializer metadata outside measurement.
    /// </summary>
    [GlobalSetup]
    public void Setup()
    {
        _serializerOptions = LspJson.CreateSerializerOptions();
        _completionList = new CompletionList
        {
            Items = Enumerable
                .Range(0, ItemCount)
                .Select(static index => new CompletionItem
                {
                    Label = $"Candidate{index}",
                    Kind = CompletionItemKind.Method,
                    Detail = "void Fixture.Candidate()",
                    SortText = index.ToString("D4", System.Globalization.CultureInfo.InvariantCulture),
                    FilterText = $"Candidate{index}"
                })
                .ToArray()
        };
        _payload = JsonSerializer.SerializeToUtf8Bytes(_completionList, _serializerOptions);
    }

    /// <summary>
    /// Measures serialization of a bounded completion response to UTF-8 JSON.
    /// </summary>
    [Benchmark]
    public byte[] SerializeCompletionList() =>
        JsonSerializer.SerializeToUtf8Bytes(_completionList, _serializerOptions);

    /// <summary>
    /// Measures deserialization of a bounded completion response from UTF-8 JSON.
    /// </summary>
    [Benchmark]
    public CompletionList DeserializeCompletionList() =>
        JsonSerializer.Deserialize<CompletionList>(_payload, _serializerOptions)!;
}
