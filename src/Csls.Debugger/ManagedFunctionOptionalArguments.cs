using System.Reflection;
using System.Reflection.Metadata;

namespace Csls.Debugger;

/// <summary>
/// Materializes supported omitted arguments from the loaded declaration's metadata.
/// </summary>
internal static class ManagedFunctionOptionalArguments
{
    /// <summary>
    /// Reads defaults for unbound CLR parameters without executing target code.
    /// </summary>
    /// <returns>One entry per CLR parameter, or null when an omission is unsupported.</returns>
    internal static ManagedExpressionValue?[]? TryCreate(
        ManagedMetadataImage metadata,
        MethodDefinitionHandle method,
        IReadOnlyList<ManagedBoundType> parameters,
        IReadOnlyList<int> parameterSourceIndices,
        ManagedBoundTypeSystem types,
        nint thread)
    {
        var defaults = new ManagedExpressionValue?[parameters.Count];
        if (!parameterSourceIndices.Contains(-1))
        {
            return defaults;
        }

        var declarations = new Parameter?[parameters.Count];
        foreach (Parameter parameter in metadata.GetParameters(method).Select(metadata.GetParameter))
        {
            if (parameter.SequenceNumber == 0)
            {
                continue;
            }

            int position = parameter.SequenceNumber - 1;
            if ((uint)position >= (uint)declarations.Length || declarations[position] is not null)
            {
                throw new BadImageFormatException("A method parameter number is invalid or duplicated.");
            }

            declarations[position] = parameter;
        }

        for (int index = 0; index < defaults.Length; index++)
        {
            if (parameterSourceIndices[index] >= 0)
            {
                continue;
            }

            Parameter? declaration = declarations[index];
            if (declaration is not Parameter parameter ||
                (parameter.Attributes & (ParameterAttributes.Optional | ParameterAttributes.HasDefault)) !=
                    (ParameterAttributes.Optional | ParameterAttributes.HasDefault) ||
                parameter.GetDefaultValue().IsNil)
            {
                return null;
            }

            Constant constant = metadata.GetConstant(parameter.GetDefaultValue());
            object? value = metadata.GetBlobReader(constant.Value).ReadConstant(constant.TypeCode);
            string? typeName = GetSupportedTypeName(parameters[index], value);
            if (typeName is null && types.TryGetEnumUnderlyingType(parameters[index], thread) is
                ManagedBoundType underlying)
            {
                typeName = GetSupportedTypeName(underlying, value);
            }
            if (typeName is null)
            {
                return null;
            }

            defaults[index] = ManagedExpressionValueFactory.FromScalar(value, typeName);
        }

        return defaults;
    }

    private static string? GetSupportedTypeName(ManagedBoundType parameter, object? value) =>
        (parameter.ElementType, value) switch
        {
            (_, null) when parameter.IsReference => "object",
            (0x02, bool) => "bool",
            (0x03, char) => "char",
            (0x04, sbyte) => "sbyte",
            (0x05, byte) => "byte",
            (0x06, short) => "short",
            (0x07, ushort) => "ushort",
            (0x08, int) => "int",
            (0x09, uint) => "uint",
            (0x0a, long) => "long",
            (0x0b, ulong) => "ulong",
            (0x0c, float) => "float",
            (0x0d, double) => "double",
            (0x0e, string) => "string",
            _ => null
        };
}
