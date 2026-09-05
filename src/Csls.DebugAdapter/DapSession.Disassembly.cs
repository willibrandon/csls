using Csls.DebugAdapter.Protocol;
using Csls.Debugger.Contracts;
using System.Globalization;
using System.Text.Json;

namespace Csls.DebugAdapter;

/// <summary>
/// Handles bounded DAP managed-IL disassembly.
/// </summary>
internal sealed partial class DapSession
{
    private async ValueTask DisassembleAsync(
        Request request,
        CancellationToken cancellationToken)
    {
        if (_state != DapSessionState.Stopped)
        {
            await WriteStateFailureAsync(request, cancellationToken).ConfigureAwait(false);
            return;
        }

        try
        {
            DebugDisassemblyRequest arguments = ParseDisassemblyArguments(request.Arguments);
            DebugDisassembly result = await _engineSession
                .DisassembleAsync(arguments, cancellationToken)
                .ConfigureAwait(false);
            await _writer.WriteResponseAsync(
                request,
                success: true,
                message: null,
                writer => WriteDisassembly(writer, result),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException or
            IOException or UnauthorizedAccessException or BadImageFormatException or
            OverflowException)
        {
            await WriteRequestFailureAsync(request, exception.Message, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static DebugDisassemblyRequest ParseDisassemblyArguments(JsonElement arguments)
    {
        string reference = GetInstructionReference(arguments);
        long byteOffset = GetOptionalInt64(arguments, "offset");
        long instructionOffset = GetOptionalInt64(arguments, "instructionOffset");
        int count = GetRequiredInteger(arguments, "instructionCount", "disassemble");
        bool resolveSymbols = GetOptionalBoolean(arguments, "resolveSymbols");
        return new DebugDisassemblyRequest(
            reference,
            byteOffset,
            instructionOffset,
            count,
            resolveSymbols);
    }

    private void WriteDisassembly(Utf8JsonWriter writer, DebugDisassembly result)
    {
        writer.WriteStartObject();
        writer.WriteStartArray("instructions");
        foreach (DebugInstructionInfo instruction in result.Instructions)
        {
            writer.WriteStartObject();
            writer.WriteString(
                "address",
                string.Create(CultureInfo.InvariantCulture, $"0x{instruction.Address:X}"));
            writer.WriteString("instruction", instruction.Instruction);
            if (!instruction.Bytes.IsEmpty)
            {
                writer.WriteString("instructionBytes", Convert.ToHexString(instruction.Bytes.Span));
            }

            if (instruction.Symbol is not null)
            {
                writer.WriteString("symbol", instruction.Symbol);
            }

            WriteInstructionSource(writer, instruction);
            if (instruction.IsInvalid)
            {
                writer.WriteString("presentationHint", "invalid");
            }

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private void WriteInstructionSource(
        Utf8JsonWriter writer,
        DebugInstructionInfo instruction)
    {
        if (instruction.Source is null || instruction.Line <= 0)
        {
            return;
        }

        writer.WritePropertyName("location");
        WriteSource(writer, instruction.Source);
        writer.WriteNumber("line", ToClientLine(instruction.Line));
        writer.WriteNumber("column", ToClientColumn(instruction.Column));
    }

    private static string GetInstructionReference(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object ||
            !arguments.TryGetProperty("memoryReference", out JsonElement value) ||
            value.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new ArgumentException(
                "The disassemble request requires a non-empty string memoryReference.");
        }

        return value.GetString()!;
    }

    private static long GetOptionalInt64(JsonElement arguments, string propertyName)
    {
        if (!arguments.TryGetProperty(propertyName, out JsonElement value))
        {
            return 0;
        }

        return value.TryGetInt64(out long result)
            ? result
            : throw new ArgumentException($"The disassemble {propertyName} must be an integer.");
    }

    private static bool GetOptionalBoolean(JsonElement arguments, string propertyName)
    {
        if (!arguments.TryGetProperty(propertyName, out JsonElement value))
        {
            return false;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new ArgumentException(
                $"The disassemble {propertyName} must be a boolean.")
        };
    }
}
