using BenchmarkDotNet.Attributes;
using Csls.Protocol;

namespace Csls.Benchmarks;

/// <summary>
/// Measures filesystem and protocol conversions for document identifiers.
/// </summary>
[BenchmarkCategory("Protocol")]
[MemoryDiagnoser]
public class DocumentUriBenchmarks
{
    private string _fileSystemPath = null!;
    private DocumentUri _fileUri;
    private string _uriText = null!;

    /// <summary>
    /// Gets or sets the number of nested directory segments in the input path.
    /// </summary>
    [Params(1, 8)]
    public int Depth { get; set; }

    /// <summary>
    /// Creates equivalent filesystem and URI inputs outside measured operations.
    /// </summary>
    [GlobalSetup]
    public void Setup()
    {
        string[] segments =
        [
            .. Enumerable.Range(0, Depth).Select(static index => $"segment-{index}"),
            "Document.cs"
        ];
        _fileSystemPath = Path.Join([Path.GetTempPath(), .. segments]);
        _fileUri = DocumentUri.FromFileSystemPath(_fileSystemPath);
        _uriText = _fileUri.ToString();
    }

    /// <summary>
    /// Measures validation and normalization of an absolute document URI.
    /// </summary>
    [Benchmark]
    public DocumentUri ParseAbsoluteUri() => DocumentUri.Parse(_uriText);

    /// <summary>
    /// Measures creation of a document URI from a filesystem path.
    /// </summary>
    [Benchmark]
    public DocumentUri CreateFromFileSystemPath() =>
        DocumentUri.FromFileSystemPath(_fileSystemPath);

    /// <summary>
    /// Measures conversion of a file document URI back to a normalized path.
    /// </summary>
    [Benchmark]
    public string GetFileSystemPath() => _fileUri.GetFileSystemPath();
}
