namespace Csls.Debugger;

/// <summary>
/// Resolves captured heap storage through the dump runtime's public data-access services.
/// </summary>
public interface ICorDebugDumpHeap
{
    /// <summary>
    /// Gets the exact captured heap object's size without reading its entire payload.
    /// </summary>
    ulong GetObjectSize(ulong address, CancellationToken cancellationToken);

    /// <summary>
    /// Gets the physical field address using exact module, declaring type and field identities.
    /// </summary>
    ulong GetFieldAddress(ulong address, ulong moduleAddress, uint declaringType, uint field, CancellationToken cancellationToken);

    /// <summary>
    /// Gets the array's dimensional bounds after its complete header has been read and validated.
    /// </summary>
    CorDebugDumpArrayBounds GetArrayBounds(ulong address, CancellationToken cancellationToken);

    /// <summary>
    /// Gets a runtime string's physical length and first-character storage addresses from its captured fields.
    /// </summary>
    (ulong LengthAddress, ulong CharacterAddress) GetStringStorage(ulong address, CancellationToken cancellationToken);
}
