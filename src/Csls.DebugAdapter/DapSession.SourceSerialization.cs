using Csls.Debugger.Contracts;
using System.Text.Json;

namespace Csls.DebugAdapter;

/// <summary>
/// Serializes shared debugger source identities to DAP.
/// </summary>
internal sealed partial class DapSession
{
    private static void WriteSource(Utf8JsonWriter writer, DebugSourceInfo source)
    {
        writer.WriteStartObject();
        writer.WriteString("name", source.Name);
        if (source.Path is not null)
        {
            writer.WriteString("path", source.Path);
        }
        if (source.SourceReference > 0)
        {
            writer.WriteNumber("sourceReference", source.SourceReference);
        }

        if (source.Origin is not null)
        {
            writer.WriteString("origin", source.Origin);
        }

        if (source.Checksum is not null)
        {
            writer.WriteStartArray("checksums");
            writer.WriteStartObject();
            writer.WriteString("algorithm", source.Checksum.Algorithm);
            writer.WriteString("checksum", source.Checksum.Value);
            writer.WriteEndObject();
            writer.WriteEndArray();
        }

        writer.WriteEndObject();
    }
}
