using Csls.Debugger.Contracts;
using Csls.Debugger.Interop;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;

namespace Csls.Debugger;

/// <summary>
/// Projects physical instance fields from captured runtime storage into bounded named pages.
/// </summary>
/// <param name="fields">The exact runtime type and field reader shared with live inspection.</param>
/// <param name="callbacks">The active captured-memory read observations.</param>
/// <param name="storage">The exact physical field storage reader.</param>
/// <param name="describe">Describes a borrowed field value within the current request.</param>
internal sealed unsafe class CorDebugDumpObjectReader(ManagedInstanceFieldReader fields,
    CorDebugDumpCallbacks callbacks, CorDebugDumpStorageReader storage,
    Func<CorDebugDumpStorage, string, CorDebugDumpValuePath, bool, DebugVariableInfo> describe)
{
    /// <summary>
    /// Counts physical instance fields in the bounded constructed type hierarchy.
    /// </summary>
    internal int GetCount(CorDebugDumpStorage instance)
    {
        int count = 0;
        VisitFields(instance, (_, _) =>
        {
            count++;
            return true;
        }, CancellationToken.None);
        return count;
    }

    /// <summary>
    /// Returns an owned field value selected by declaring module, type and field identity.
    /// </summary>
    internal CorDebugDumpStorage ReadField(CorDebugDumpStorage instance, CorDebugDumpFieldSelection selection, CancellationToken cancellationToken)
    {
        CorDebugDumpStorage? value = null;
        fields.VisitExactType(instance.Type, 0, declaration =>
        {
            if (declaration.TypeToken != selection.DeclaringTypeToken ||
                GetModuleAddress(declaration.Module) != selection.ModuleAddress)
            {
                return true;
            }
            FieldDefinitionHandle handle = MetadataTokens.FieldDefinitionHandle(checked((int)(selection.FieldToken & 0x00ffffff)));
            value = storage.ReadField(instance, declaration, handle, selection.FieldToken, selection.ModuleAddress);
            return false;
        }, cancellationToken);
        return value ?? throw new InvalidOperationException("The captured field's declaring type could not be resolved.");
    }

    /// <summary>
    /// Reads the requested physical field page and retains only logical paths for expandable children.
    /// </summary>
    internal IReadOnlyList<DebugVariableInfo> ReadPage(CorDebugDumpStorage instance, CorDebugDumpValuePath path,
        int reference, int start, int count, CancellationToken cancellationToken)
    {
        int total = GetCount(instance);
        int take = start >= total ? 0 : total - start;
        take = count == 0 ? take : Math.Min(count, take);
        if (take > 4096)
        {
            throw new InvalidDataException("The requested captured object page exceeds the response limit of 4096 values.");
        }
        List<DebugVariableInfo> result = new(take);
        if (take == 0)
        {
            return result;
        }
        int index = 0;
        VisitFields(instance, (declaration, handle) =>
        {
            if (index++ < start)
            {
                return true;
            }
            FieldDefinition field = declaration.Metadata.GetFieldDefinition(handle);
            string name = declaration.Metadata.GetString(field.Name);
            uint token = checked((uint)MetadataTokens.GetToken(handle));
            long missingMemory = callbacks.MissingMemoryReads;
            try
            {
                using CorDebugDumpStorage value = storage.ReadField(instance, declaration, handle, token, GetModuleAddress(declaration.Module));
                result.Add(describe(value, name, path with
                {
                    ParentId = reference,
                    ElementIndex = 0,
                    Field = new CorDebugDumpFieldSelection(GetModuleAddress(declaration.Module), declaration.TypeToken, token),
                    Depth = checked(path.Depth + 1)
                }, false));
            }
            catch (InvalidOperationException exception) when (CorDebugDumpValueReader.IsUnavailable(exception) ||
                callbacks.IsMissingMemoryFailure(exception, missingMemory))
            {
                result.Add(CorDebugDumpValueReader.Unavailable(name, exception));
            }
            catch (CorDebugDumpStorageUnavailableException exception)
            {
                result.Add(CorDebugDumpValueReader.Unavailable(name, exception));
            }
            return result.Count < take;
        }, cancellationToken);
        return result;
    }

    private void VisitFields(CorDebugDumpStorage instance, Func<ManagedRuntimeTypeDeclaration, FieldDefinitionHandle, bool> visitor,
        CancellationToken cancellationToken)
    {
        int visited = 0;
        fields.VisitExactType(instance.Type, 0, declaration =>
        {
            TypeDefinitionHandle handle = MetadataTokens.TypeDefinitionHandle(checked((int)(declaration.TypeToken & 0x00ffffff)));
            foreach (FieldDefinitionHandle field in declaration.Metadata.GetTypeDefinition(handle).GetFields())
            {
                cancellationToken.ThrowIfCancellationRequested();
                callbacks.Operation?.ThrowIfInterrupted();
                if ((declaration.Metadata.GetFieldDefinition(field).Attributes & FieldAttributes.Static) != 0)
                {
                    continue;
                }
                if (++visited > 65536)
                {
                    throw new InvalidDataException("The captured object exceeds the field limit of 65536.");
                }
                if (!visitor(declaration, field))
                {
                    return false;
                }
            }
            return true;
        }, cancellationToken);
    }

    private static ulong GetModuleAddress(nint module)
    {
        ulong address = 0;
        CorDebugHResult.ThrowIfFailed(new ICorDebugModuleAbi(module).GetBaseAddress((nint)(&address)), "ICorDebugModule.GetBaseAddress");
        return Volatile.Read(ref address);
    }

}
