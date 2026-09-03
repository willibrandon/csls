using Csls.Debugger.Interop;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace Csls.Debugger;

/// <summary>
/// Resolves managed frame names and source positions from PE metadata and Portable PDBs.
/// </summary>
internal static class PortablePdbFrameResolver
{
    private const int HiddenSequencePointLine = 0x00feefee;

    /// <summary>
    /// Resolves the best available display information for one IL frame.
    /// </summary>
    /// <param name="frame">The borrowed ICorDebugFrame pointer.</param>
    /// <param name="methodToken">The method-definition metadata token.</param>
    /// <param name="ilOffset">The current IL instruction offset.</param>
    /// <param name="symbolPathResolver">Resolves the selected associated PDB for a module.</param>
    /// <returns>The resolved method and optional source location.</returns>
    internal static unsafe ManagedFrameLocation Resolve(
        nint frame,
        uint methodToken,
        uint ilOffset,
        Func<string, string?> symbolPathResolver)
    {
        ArgumentNullException.ThrowIfNull(symbolPathResolver);
        string fallbackName = $"0x{methodToken:X8}";
        nint function = 0;
        nint module = 0;
        try
        {
            nint* functionAddress = &function;
            CorDebugHResult.ThrowIfFailed(
                new ICorDebugFrameAbi(frame).GetFunction((nint)functionAddress),
                "ICorDebugFrame.GetFunction");
            function = Volatile.Read(ref *functionAddress);
            if (function == 0)
            {
                return Unknown(fallbackName);
            }

            nint* moduleAddress = &module;
            CorDebugHResult.ThrowIfFailed(
                new ICorDebugFunctionAbi(function).GetModule((nint)moduleAddress),
                "ICorDebugFunction.GetModule");
            module = Volatile.Read(ref *moduleAddress);
            if (module == 0)
            {
                return Unknown(fallbackName);
            }

            string modulePath = CorDebugModulePath.Get(module);
            return ResolveFiles(
                modulePath,
                methodToken,
                ilOffset,
                fallbackName,
                symbolPathResolver(modulePath));
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or BadImageFormatException)
        {
            return Unknown(fallbackName);
        }
        finally
        {
            if (module != 0)
            {
                _ = ComAbi.Release(module);
            }

            if (function != 0)
            {
                _ = ComAbi.Release(function);
            }
        }
    }

    private static ManagedFrameLocation ResolveFiles(
        string modulePath,
        uint methodToken,
        uint ilOffset,
        string fallbackName,
        string? symbolPath)
    {
        using FileStream moduleStream = File.OpenRead(modulePath);
        using var peReader = new PEReader(moduleStream);
        MetadataReader metadata = peReader.GetMetadataReader();
        int rowNumber = checked((int)(methodToken & 0x00ffffff));
        if (rowNumber == 0 || rowNumber > metadata.MethodDefinitions.Count)
        {
            return Unknown(fallbackName);
        }

        MethodDefinitionHandle methodHandle = MetadataTokens.MethodDefinitionHandle(rowNumber);
        MethodDefinition method = metadata.GetMethodDefinition(methodHandle);
        TypeDefinition declaringType = metadata.GetTypeDefinition(method.GetDeclaringType());
        string typeName = metadata.GetString(declaringType.Name);
        string typeNamespace = metadata.GetString(declaringType.Namespace);
        string methodName = metadata.GetString(method.Name);
        string displayName = string.IsNullOrEmpty(typeNamespace)
            ? $"{typeName}.{methodName}"
            : $"{typeNamespace}.{typeName}.{methodName}";
        using var symbols = PortablePdbReader.TryOpen(modulePath, symbolPath);
        if (symbols is null)
        {
            return Unknown(displayName, modulePath);
        }

        MetadataReader pdb = symbols.Metadata;
        MethodDebugInformation debugInformation = pdb.GetMethodDebugInformation(
            MetadataTokens.MethodDebugInformationHandle(rowNumber));
        SequencePoint? selected = null;
        foreach (SequencePoint point in debugInformation.GetSequencePoints())
        {
            if (point.IsHidden || point.StartLine == HiddenSequencePointLine)
            {
                continue;
            }

            if (selected is null || point.Offset <= ilOffset)
            {
                selected = point;
            }

            if (point.Offset > ilOffset)
            {
                break;
            }
        }

        if (selected is null)
        {
            return Unknown(displayName, modulePath);
        }

        Document document = pdb.GetDocument(selected.Value.Document);
        return new ManagedFrameLocation
        {
            Name = displayName,
            ModulePath = modulePath,
            SourcePath = pdb.GetString(document.Name),
            Line = selected.Value.StartLine,
            Column = selected.Value.StartColumn
        };
    }

    private static ManagedFrameLocation Unknown(string name, string? modulePath = null) =>
        new()
        {
            Name = name,
            ModulePath = modulePath,
            Line = 0,
            Column = 0
        };
}
