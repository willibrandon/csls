using Csls.Debugger.Contracts;

namespace Csls.Debugger;

/// <summary>
/// Creates debugger-owned target sessions through a stable engine boundary.
/// </summary>
public static class DebuggerEngine
{
    /// <summary>
    /// Creates a protocol-neutral debugger session with an explicit observer.
    /// </summary>
    /// <param name="observer">The target lifecycle and output observer.</param>
    /// <returns>A new debugger session with no target.</returns>
    public static DebuggerSession CreateSession(IDebuggerSessionObserver observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        return new DebuggerSession(observer);
    }

    /// <summary>
    /// Verifies that the native runtime-debugging shim supports this platform.
    /// </summary>
    public static void VerifyPlatformSupport() => DbgShimLibrary.VerifyPlatformSupport();

    /// <summary>
    /// Validates source mapping, Source Link, and symbol lookup policy without activation.
    /// </summary>
    /// <param name="mappings">The complete build-time source mapping.</param>
    /// <param name="sourceLinkOptions">The complete Source Link URL policy.</param>
    /// <param name="symbolOptions">The complete trusted symbol search policy.</param>
    public static void ValidateSourceOptions(
        IReadOnlyDictionary<string, string> mappings,
        IReadOnlyDictionary<string, bool> sourceLinkOptions,
        DebugSymbolOptions symbolOptions)
    {
        var mapper = new SourcePathMapper();
        mapper.Set(mappings);
        var sourceLink = new SourceLinkPolicy();
        sourceLink.Set(sourceLinkOptions);
        var symbols = new PortablePdbLocator();
        symbols.Set(symbolOptions);
    }

    /// <summary>
    /// Verifies suspended launch and CoreCLR debugger activation against a real target.
    /// </summary>
    /// <param name="options">The concrete managed target invocation to probe.</param>
    /// <param name="cancellationToken">Cancels activation and terminates the probe target.</param>
    /// <returns>The operating-system identifier of the successfully activated probe target.</returns>
    public static Task<uint> VerifyRuntimeActivationAsync(
        DebuggeeLaunchOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        return CorDebugRuntimeActivator.VerifyAsync(options, cancellationToken);
    }
}
