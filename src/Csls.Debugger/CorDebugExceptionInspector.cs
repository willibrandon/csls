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
    private const int MaximumHierarchyDepth = 256;

    /// <summary>
    /// Gets the exact managed exception type followed by its base types.
    /// </summary>
    /// <param name="thread">The borrowed ICorDebugThread pointer.</param>
    /// <returns>The ordered managed exception type hierarchy.</returns>
    internal static unsafe IReadOnlyList<string> GetTypeHierarchy(nint thread)
    {
        ArgumentOutOfRangeException.ThrowIfZero(thread);
        nint value = 0;
        nint currentType = 0;
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
                return ["<unknown exception>"];
            }

            try
            {
                nint* exactTypeAddress = &currentType;
                CorDebugHResult.ThrowIfFailed(
                    new ICorDebugValue2Abi(value2).GetExactType((nint)exactTypeAddress),
                    "ICorDebugValue2.GetExactType");
                currentType = Volatile.Read(ref *exactTypeAddress);
            }
            finally
            {
                _ = ComAbi.Release(value2);
            }

            var result = new List<string>();
            while (currentType != 0 && result.Count < MaximumHierarchyDepth)
            {
                result.Add(ReadTypeName(currentType));
                nint baseType = 0;
                nint* baseTypeAddress = &baseType;
                CorDebugHResult.ThrowIfFailed(
                    new ICorDebugTypeAbi(currentType).GetBase((nint)baseTypeAddress),
                    "ICorDebugType.GetBase");
                baseType = Volatile.Read(ref *baseTypeAddress);
                _ = ComAbi.Release(currentType);
                currentType = baseType;
            }

            return result.Count == 0 ? ["<unknown exception>"] : result;
        }
        finally
        {
            Release(currentType);
            Release(value);
        }
    }

    private static unsafe string ReadTypeName(nint type)
    {
        nint @class = 0;
        nint module = 0;
        try
        {
            nint* classAddress = &@class;
            CorDebugHResult.ThrowIfFailed(
                new ICorDebugTypeAbi(type).GetClass((nint)classAddress),
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
            return ReadMetadataTypeName(CorDebugModulePath.Get(module), token);
        }
        finally
        {
            Release(module);
            Release(@class);
        }
    }

    private static string ReadMetadataTypeName(string modulePath, uint token)
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
        return GetMetadataTypeName(reader, (TypeDefinitionHandle)handle);
    }

    private static string GetMetadataTypeName(MetadataReader reader, TypeDefinitionHandle handle)
    {
        TypeDefinition definition = reader.GetTypeDefinition(handle);
        string name = reader.GetString(definition.Name);
        TypeDefinitionHandle declaringType = definition.GetDeclaringType();
        if (!declaringType.IsNil)
        {
            return $"{GetMetadataTypeName(reader, declaringType)}+{name}";
        }

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
