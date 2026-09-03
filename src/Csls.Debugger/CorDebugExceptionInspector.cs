using Csls.Debugger.Interop;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace Csls.Debugger;

/// <summary>
/// Reads managed exception identity without executing code in the target.
/// </summary>
internal static class CorDebugExceptionInspector
{
    /// <summary>
    /// Gets the fully qualified type name of the thread's current exception.
    /// </summary>
    /// <param name="thread">The borrowed ICorDebugThread pointer.</param>
    /// <returns>The managed exception type name.</returns>
    internal static unsafe string GetTypeName(nint thread)
    {
        ArgumentOutOfRangeException.ThrowIfZero(thread);
        nint value = 0;
        nint exactType = 0;
        nint @class = 0;
        nint module = 0;
        try
        {
            nint* valueAddress = &value;
            CorDebugHResult.ThrowIfFailed(
                new ICorDebugThreadAbi(thread).GetCurrentException((nint)valueAddress),
                "ICorDebugThread.GetCurrentException");
            value = Volatile.Read(ref *valueAddress);
            if (value == 0 ||
                !ComAbi.TryQueryInterface(value, ICorDebugValue2Abi.InterfaceId, out nint value2))
            {
                return "<unknown exception>";
            }

            try
            {
                nint* exactTypeAddress = &exactType;
                CorDebugHResult.ThrowIfFailed(
                    new ICorDebugValue2Abi(value2).GetExactType((nint)exactTypeAddress),
                    "ICorDebugValue2.GetExactType");
                exactType = Volatile.Read(ref *exactTypeAddress);
            }
            finally
            {
                _ = ComAbi.Release(value2);
            }

            nint* classAddress = &@class;
            CorDebugHResult.ThrowIfFailed(
                new ICorDebugTypeAbi(exactType).GetClass((nint)classAddress),
                "ICorDebugType.GetClass");
            @class = Volatile.Read(ref *classAddress);
            nint* moduleAddress = &module;
            CorDebugHResult.ThrowIfFailed(
                new ICorDebugClassAbi(@class).GetModule((nint)moduleAddress),
                "ICorDebugClass.GetModule");
            module = Volatile.Read(ref *moduleAddress);
            uint token = 0;
            uint* tokenAddress = &token;
            CorDebugHResult.ThrowIfFailed(
                new ICorDebugClassAbi(@class).GetToken((nint)tokenAddress),
                "ICorDebugClass.GetToken");
            token = Volatile.Read(ref *tokenAddress);
            return ReadTypeName(CorDebugModulePath.Get(module), token);
        }
        finally
        {
            Release(module);
            Release(@class);
            Release(exactType);
            Release(value);
        }
    }

    private static string ReadTypeName(string modulePath, uint token)
    {
        using FileStream stream = File.Open(
            modulePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read | FileShare.Delete);
        using var peReader = new PEReader(stream);
        MetadataReader reader = peReader.GetMetadataReader();
        EntityHandle handle = MetadataTokens.EntityHandle(checked((int)token));
        if (handle.Kind != HandleKind.TypeDefinition)
        {
            return "<unknown exception>";
        }

        TypeDefinition definition = reader.GetTypeDefinition((TypeDefinitionHandle)handle);
        string name = reader.GetString(definition.Name);
        string @namespace = reader.GetString(definition.Namespace);
        return string.IsNullOrEmpty(@namespace) ? name : $"{@namespace}.{name}";
    }

    private static void Release(nint value)
    {
        if (value != 0)
        {
            _ = ComAbi.Release(value);
        }
    }
}
