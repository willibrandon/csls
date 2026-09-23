using Csls.Debugger.Interop;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace Csls.Debugger;

/// <summary>
/// Opens captured metadata and shares identity-checked local images with the native virtual process.
/// </summary>
internal sealed class CorDebugDumpModuleReader(ICorDebugDumpSource source, CorDebugDumpCallbacks callbacks,
    Func<ulong, CancellationToken, CorDebugDumpModuleInfo> describeModule)
{
    /// <summary>
    /// Opens metadata for one exact borrowed captured runtime module.
    /// </summary>
    internal unsafe PEReader Open(nint module)
    {
        ulong address = 0;
        CorDebugHResult.ThrowIfFailed(new ICorDebugModuleAbi(module).GetBaseAddress((nint)(&address)),
            "ICorDebugModule.GetBaseAddress");
        callbacks.Operation?.ThrowIfInterrupted();
        CorDebugDumpReadOperation operation = callbacks.Operation
            ?? throw new InvalidOperationException("Captured metadata requires an active inspection request.");
        CorDebugDumpModuleInfo info = describeModule(Volatile.Read(ref address), operation.CancellationToken);
        callbacks.Operation?.ThrowIfInterrupted();
        if (info.Address != Volatile.Read(ref address))
        {
            throw new InvalidDataException("The captured module description belongs to another address.");
        }

        using var owner = new DisposableOwner<PEReader>();
        try
        {
            owner.Acquire(() => OpenImage(() => new CorDebugDumpMemoryStream(source, callbacks, info.Address, info.Size),
                info.IsLoadedImage ? PEStreamOptions.IsLoadedImage : PEStreamOptions.Default));
            Validate(owner.Value ?? throw new InvalidOperationException("No captured image is owned."));
            return owner.Detach();
        }
        catch (Exception exception) when (exception is IOException or BadImageFormatException)
        {
            callbacks.Operation?.ThrowIfInterrupted();
        }

        string path = callbacks.ResolveMetadataImage(info.Path, info.Timestamp, info.ImageSize);
        using var snapshot = new DisposableOwner<PEReader>();
        snapshot.Acquire(() => OpenImage(() => File.OpenRead(path), PEStreamOptions.Default));
        Validate(snapshot.Value ?? throw new InvalidOperationException("No metadata snapshot is owned."));
        callbacks.Operation?.ThrowIfInterrupted();
        return snapshot.Detach();
    }

    private static PEReader OpenImage(Func<Stream> createStream, PEStreamOptions options)
    {
        using var stream = new DisposableOwner<Stream>();
        stream.Acquire(createStream);
        using var reader = new DisposableOwner<PEReader>();
        reader.Acquire(() => new PEReader(stream.Value ?? throw new InvalidOperationException("No module stream is owned."), options));
        _ = stream.Detach();
        return reader.Detach();
    }

    private static void Validate(PEReader image)
    {
        if (!image.HasMetadata || image.PEHeaders.MetadataSize is <= 0 or > 64 * 1024 * 1024)
        {
            throw new BadImageFormatException("The captured module has no bounded managed metadata.");
        }
        _ = image.GetMetadataReader();
    }
}
