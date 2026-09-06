using Csls.Debugger.Contracts;
using Csls.Debugger.Interop;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace Csls.Debugger;

/// <summary>
/// Resolves immutable frame presentation once per exact runtime location within one stack request.
/// </summary>
/// <param name="sourceBreakpoints">The actor-owned module and source catalog.</param>
internal sealed class ManagedSymbolFrameResolver(SourceBreakpointManager sourceBreakpoints)
{
    private readonly Dictionary<(CorDebugLoadedModule Module, uint Method, uint Offset, int Generation), ManagedFrameLocation>
        _locations = [];
    private readonly Dictionary<(int Module, string Path), DebugSourceInfo> _sources = [];

    /// <summary>
    /// Resolves the best available display information for one IL frame.
    /// </summary>
    /// <param name="frame">The borrowed ICorDebugFrame pointer.</param>
    /// <param name="methodToken">The method-definition metadata token.</param>
    /// <param name="ilOffset">The current IL instruction offset.</param>
    /// <returns>The resolved method and optional source location.</returns>
    internal unsafe ManagedFrameLocation Resolve(
        nint frame,
        uint methodToken,
        uint ilOffset)
    {
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

            CorDebugLoadedModule? loadedModule = sourceBreakpoints.FindModule(module);
            if (loadedModule is null)
            {
                return Unknown(fallbackName);
            }

            int generation = GetFunctionGeneration(function, loadedModule);
            (CorDebugLoadedModule, uint, uint, int) key = (loadedModule, methodToken, ilOffset, generation);
            if (!_locations.TryGetValue(key, out ManagedFrameLocation? location))
            {
                location = ResolveModule(loadedModule, methodToken, ilOffset, generation, fallbackName,
                    sourceBreakpoints.GetUserCodeStatus(loadedModule, function));
                _locations.Add(key, location);
            }

            return location;
        }
        catch (Exception exception) when (DebugSymbolReader.IsReadFailure(exception))
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

    /// <summary>
    /// Shares a verified source snapshot within this request while preserving the catalog's stable identity.
    /// </summary>
    /// <param name="location">The exact frame location resolved from its runtime method version.</param>
    /// <returns>The request's source descriptor, or null when the frame has no source document.</returns>
    internal DebugSourceInfo? ResolveSource(ManagedFrameLocation location)
    {
        if (location.ModuleId is not int moduleId || location.SourcePath is not string sourcePath)
        {
            return null;
        }

        (int, string) key = (moduleId, sourcePath);
        if (!_sources.TryGetValue(key, out DebugSourceInfo? source))
        {
            source = sourceBreakpoints.GetSourceInfo(moduleId, sourcePath);
            _sources.Add(key, source);
        }

        return source;
    }

    private static ManagedFrameLocation ResolveModule(
        CorDebugLoadedModule module,
        uint methodToken,
        uint ilOffset,
        int generation,
        string fallbackName,
        bool? isUserCode)
    {
        string displayName = fallbackName;
        IReadOnlyList<byte[]> symbolDeltas = [.. module.SymbolDeltas.Take(generation)];
        IReadOnlyList<byte[]> metadataDeltas = [.. module.MetadataDeltas.Take(generation)];
        using PEReader? peReader = module.OpenPeReader();
        using ManagedMetadataImage? metadata = peReader is null ? null
            : new ManagedMetadataImage(peReader.GetMetadataReader(), metadataDeltas);
        if (metadata is not null)
        {
            int rowNumber = checked((int)(methodToken & 0x00ffffff));
            if (!metadata.ContainsMethod(MetadataTokens.MethodDefinitionHandle(rowNumber)))
            {
                return Unknown(fallbackName, module, symbolDeltas, metadataDeltas, isUserCode);
            }

            displayName = ResolveMethodName(metadata, methodToken, fallbackName);
        }

        using DebugSymbolReader? symbols = module.OpenSymbols(symbolDeltas);
        if (symbols is null)
        {
            return Unknown(displayName, module, symbolDeltas, metadataDeltas, isUserCode);
        }

        if (metadata is not null &&
            symbols.GetStateMachineKickoffMethod(methodToken) is uint kickoffMethod)
        {
            displayName = ResolveMethodName(metadata, kickoffMethod, displayName);
        }

        ManagedSequencePoint? selected = null;
        foreach (ManagedSequencePoint point in symbols.GetSequencePoints(methodToken, includeHidden: true))
        {
            if (point.IlOffset <= ilOffset)
            {
                selected = point;
            }

            if (point.IlOffset > ilOffset)
            {
                break;
            }
        }

        if (selected is null || selected.IsHidden)
        {
            return Unknown(displayName, module, symbolDeltas, metadataDeltas, isUserCode);
        }

        return new ManagedFrameLocation
        {
            Name = displayName,
            IsUserCode = isUserCode,
            ModulePath = module.Path,
            ModuleId = module.Id,
            ModuleImage = module.ModuleImage,
            SymbolImage = module.SymbolImage,
            SymbolDeltas = symbolDeltas,
            MetadataDeltas = metadataDeltas,
            SourcePath = selected.SourcePath,
            Line = selected.StartLine,
            Column = selected.StartColumn,
            ExpressionLanguage = ManagedExpressionLanguageResolver.Resolve(selected.LanguageId)
        };
    }

    private static unsafe int GetFunctionGeneration(nint function, CorDebugLoadedModule module)
    {
        if (module.HotReloadGeneration == 0)
        {
            return 0;
        }

        nint function2 = ComAbi.QueryInterface(function, ICorDebugFunction2Abi.InterfaceId);
        try
        {
            uint version = 0;
            uint* versionAddress = &version;
            CorDebugHResult.ThrowIfFailed(new ICorDebugFunction2Abi(function2).GetVersionNumber((nint)versionAddress),
                "ICorDebugFunction2.GetVersionNumber");
            version = Volatile.Read(ref *versionAddress);
            if (version == 0 || version - 1 > module.SymbolDeltas.Count || version - 1 > module.MetadataDeltas.Count)
            {
                throw new BadImageFormatException("The active method version is outside the retained Hot Reload history.");
            }

            return checked((int)(version - 1));
        }
        finally
        {
            _ = ComAbi.Release(function2);
        }
    }

    private static string ResolveMethodName(
        ManagedMetadataImage metadata,
        uint methodToken,
        string fallbackName)
    {
        int rowNumber = checked((int)(methodToken & 0x00ffffff));
        if (!metadata.ContainsMethod(MetadataTokens.MethodDefinitionHandle(rowNumber)))
        {
            return fallbackName;
        }

        MethodDefinitionHandle handle = MetadataTokens.MethodDefinitionHandle(rowNumber);
        MethodDefinition method = metadata.GetMethodDefinition(handle);
        TypeDefinition declaringType = metadata.GetTypeDefinition(metadata.GetDeclaringType(handle));
        string typeName = metadata.GetString(declaringType.Name);
        string typeNamespace = metadata.GetString(declaringType.Namespace);
        string methodName = metadata.GetString(method.Name);
        return string.IsNullOrEmpty(typeNamespace)
            ? $"{typeName}.{methodName}"
            : $"{typeNamespace}.{typeName}.{methodName}";
    }

    private static ManagedFrameLocation Unknown(
        string name,
        string? modulePath = null,
        int? moduleId = null) =>
        new()
        {
            Name = name,
            ModulePath = modulePath,
            ModuleId = moduleId,
            Line = 0,
            Column = 0
        };

    private static ManagedFrameLocation Unknown(
        string name,
        CorDebugLoadedModule module,
        IReadOnlyList<byte[]> symbolDeltas,
        IReadOnlyList<byte[]> metadataDeltas,
        bool? isUserCode) =>
        new()
        {
            Name = name,
            IsUserCode = isUserCode,
            ModulePath = module.Path,
            ModuleId = module.Id,
            ModuleImage = module.ModuleImage,
            SymbolImage = module.SymbolImage,
            SymbolDeltas = symbolDeltas,
            MetadataDeltas = metadataDeltas,
            Line = 0,
            Column = 0
        };
}
