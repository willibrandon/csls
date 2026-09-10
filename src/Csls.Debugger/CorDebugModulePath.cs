using Csls.Debugger.Interop;

namespace Csls.Debugger;

/// <summary>
/// Reads bounded module paths from the native managed-debugging interface.
/// </summary>
internal static class CorDebugModulePath
{
    private const uint MaximumCharacterCount = 32 * 1024;

    /// <summary>
    /// Gets the absolute path reported for a loaded managed module.
    /// </summary>
    /// <param name="module">The borrowed ICorDebugModule pointer.</param>
    /// <returns>The module path reported by CoreCLR.</returns>
    internal static unsafe string Get(nint module)
    {
        ArgumentOutOfRangeException.ThrowIfZero(module);
        uint characterCount = 0;
        uint* characterCountAddress = &characterCount;
        var api = new ICorDebugModuleAbi(module);
        CorDebugHResult.ThrowIfFailed(
            api.GetName(0, (nint)characterCountAddress, 0),
            "ICorDebugModule.GetName");
        characterCount = Volatile.Read(ref *characterCountAddress);
        if (characterCount <= 1 || characterCount > MaximumCharacterCount)
        {
            throw new InvalidOperationException(
                $"ICorDebugModule.GetName returned invalid length {characterCount}.");
        }

        char[] buffer = GC.AllocateUninitializedArray<char>(checked((int)characterCount));
        fixed (char* bufferAddress = buffer)
        {
            CorDebugHResult.ThrowIfFailed(
                api.GetName(characterCount, (nint)characterCountAddress, (nint)bufferAddress),
                "ICorDebugModule.GetName");
        }

        characterCount = Volatile.Read(ref *characterCountAddress);
        int length = checked((int)characterCount);
        if (length > 0 && buffer[length - 1] == '\0')
        {
            length--;
        }

        return new string(buffer, 0, length);
    }
}
