using Csls.Debugger.Contracts;
using Csls.Debugger.Interop;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;

namespace Csls.Debugger;

/// <summary>
/// Binds static declarations and reacquires their selected-thread storage without executing target code.
/// </summary>
internal sealed class ManagedStaticFieldResolver
{
    private const int MaximumHierarchyDepth = 128;
    private readonly SourceBreakpointManager _modules;
    private readonly ManagedBoundTypeSystem _types;

    /// <summary>
    /// Creates a resolver over the stopped process's module catalog and exact type system.
    /// </summary>
    internal ManagedStaticFieldResolver(SourceBreakpointManager modules, ManagedBoundTypeSystem types)
    {
        ArgumentNullException.ThrowIfNull(modules);
        ArgumentNullException.ThrowIfNull(types);
        _modules = modules;
        _types = types;
    }

    /// <summary>
    /// Resolves the first matching declaration in the selected type's base hierarchy.
    /// </summary>
    internal ManagedStaticFieldBinding Resolve(string typeName, string name, DebugExpressionLanguage language, nint thread)
    {
        ManagedBoundType? current = _types.BindName(typeName, language, thread);
        StringComparison comparison = language == DebugExpressionLanguage.VisualBasic
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        for (int depth = 0; current is not null && depth < MaximumHierarchyDepth; depth++)
        {
            CorDebugLoadedModule module = GetModule(current);
            using PEReader pe = module.OpenPeReader()
                ?? throw new InvalidOperationException("The static field's declaring metadata is unavailable.");
            MetadataReader metadata = pe.GetMetadataReader();
            TypeDefinition type = metadata.GetTypeDefinition(
                (TypeDefinitionHandle)MetadataTokens.EntityHandle(checked((int)current.DefinitionToken)));
            FieldDefinitionHandle? match = null;
            foreach (FieldDefinitionHandle handle in type.GetFields())
            {
                if (!string.Equals(metadata.GetString(metadata.GetFieldDefinition(handle).Name), name, comparison))
                {
                    continue;
                }
                if (match is not null)
                {
                    throw new InvalidOperationException($"Field '{name}' is ambiguous on runtime type '{current.Name}'.");
                }
                match = handle;
            }

            if (match is FieldDefinitionHandle selected)
            {
                FieldDefinition field = metadata.GetFieldDefinition(selected);
                if ((field.Attributes & FieldAttributes.Static) == 0)
                {
                    throw new InvalidOperationException($"Field '{name}' requires an instance receiver.");
                }
                ManagedBoundType fieldType = _types.Bind(field.DecodeSignature(
                    new ManagedMetadataTypeSignatureProvider(module.Pointer), genericContext: null), current.TypeArguments, [], thread);
                object? constant = null;
                if ((field.Attributes & FieldAttributes.Literal) != 0)
                {
                    if (field.GetDefaultValue().IsNil)
                    {
                        throw new BadImageFormatException($"Literal field '{name}' has no metadata constant.");
                    }
                    Constant value = metadata.GetConstant(field.GetDefaultValue());
                    BlobReader blob = metadata.GetBlobReader(value.Value);
                    constant = blob.ReadConstant(value.TypeCode);
                }
                return new ManagedStaticFieldBinding(current, checked((uint)MetadataTokens.GetToken(selected)),
                    fieldType, field.Attributes, ManagedTupleElementNameReader.ReadAttribute(metadata, field.GetCustomAttributes()), constant);
            }

            current = _types.GetParents(current, thread).FirstOrDefault(parent =>
                (_types.GetAttributes(parent) & TypeAttributes.Interface) == 0);
        }
        if (current is not null)
        {
            throw new InvalidOperationException($"The static field hierarchy exceeds {MaximumHierarchyDepth} levels.");
        }
        throw new InvalidOperationException($"No loaded runtime type or static field named '{typeName}.{name}' is available.");
    }

    /// <summary>
    /// Returns an owned runtime value for the exact static storage in the selected frame.
    /// </summary>
    internal unsafe nint Read(ManagedBoundType declaringType, uint fieldToken, ManagedFrameHandle frame, nint thread)
    {
        nint type = _types.ResolveRuntimeType(declaringType, thread);
        nint value = 0;
        try
        {
            nint* address = &value;
            int hresult = new ICorDebugTypeAbi(type).GetStaticFieldValue(fieldToken, frame.Pointer, (nint)address);
            value = Volatile.Read(ref *address);
            if (hresult < 0)
            {
                throw new InvalidOperationException(
                    $"Static storage for '{declaringType.DisplayName}' is unavailable in the selected thread.",
                    Marshal.GetExceptionForHR(hresult));
            }
            if (value == 0)
            {
                throw new InvalidOperationException("The static field has no runtime storage in the selected frame.");
            }
            return value;
        }
        catch
        {
            if (value != 0)
            {
                _ = ComAbi.Release(value);
            }
            throw;
        }
        finally
        {
            _ = ComAbi.Release(type);
        }
    }

    private CorDebugLoadedModule GetModule(ManagedBoundType type) => type.ModuleId is int id
        ? _modules.FindModule(id) ?? throw new InvalidOperationException("The static field's module has unloaded.")
        : throw new InvalidOperationException("The static field has no declaring module.");
}
