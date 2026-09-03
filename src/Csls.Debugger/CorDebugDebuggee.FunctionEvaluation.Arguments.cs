using Csls.Debugger.Interop;

namespace Csls.Debugger;

/// <summary>
/// Materializes exact CoreCLR values for managed function-evaluation arguments.
/// </summary>
internal sealed partial class CorDebugDebuggee
{
    private unsafe nint CreateFunctionArgument(
        nint evaluation,
        ManagedExpressionValue argument,
        nint runtimeArgument,
        List<nint> temporaryArguments)
    {
        if (argument.Display.VariablesReference > 0 ||
            argument.HasScalar && argument.Scalar is string)
        {
            return runtimeArgument != 0
                ? runtimeArgument
                : throw new InvalidOperationException(
                    $"Function argument '{argument.Display.Name}' has no retained runtime value.");
        }

        object? scalar = ManagedExpressionValueFactory.RequireScalar(argument);
        uint elementType = GetFunctionArgumentElementType(argument.Display.Type, scalar);
        nint value = 0;
        nint* valueAddress = &value;
        CorDebugHResult.ThrowIfFailed(
            new ICorDebugEvalAbi(evaluation).CreateValue(
                elementType,
                pElementClass: 0,
                (nint)valueAddress),
            "ICorDebugEval.CreateValue");
        value = RequirePointer(
            Volatile.Read(ref *valueAddress),
            "ICorDebugEval.CreateValue");
        try
        {
            if (scalar is not null)
            {
                SetFunctionArgumentValue(value, argument.Display.Type, scalar);
            }

            temporaryArguments.Add(value);
            return value;
        }
        catch
        {
            _ = ComAbi.Release(value);
            throw;
        }
    }

    private static uint GetFunctionArgumentElementType(string type, object? scalar) =>
        scalar is null
            ? 0x12u
            : type switch
            {
                "bool" => 0x02u,
                "char" => 0x03u,
                "sbyte" => 0x04u,
                "byte" => 0x05u,
                "short" => 0x06u,
                "ushort" => 0x07u,
                "int" => 0x08u,
                "uint" => 0x09u,
                "long" => 0x0au,
                "ulong" => 0x0bu,
                "float" => 0x0cu,
                "double" => 0x0du,
                "nint" => 0x18u,
                "nuint" => 0x19u,
                _ => throw new NotSupportedException(
                    $"Managed function evaluation cannot materialize an argument of " +
                    $"type '{type}'.")
            };

    private static unsafe void SetFunctionArgumentValue(
        nint value,
        string type,
        object scalar)
    {
        nint generic = ComAbi.QueryInterface(value, ICorDebugGenericValueAbi.InterfaceId);
        try
        {
            switch (type)
            {
                case "bool":
                    byte boolean = (bool)scalar ? (byte)1 : (byte)0;
                    SetGenericValue(generic, &boolean);
                    break;
                case "char":
                    char character = (char)scalar;
                    SetGenericValue(generic, &character);
                    break;
                case "sbyte":
                    sbyte signedByte = (sbyte)scalar;
                    SetGenericValue(generic, &signedByte);
                    break;
                case "byte":
                    byte unsignedByte = (byte)scalar;
                    SetGenericValue(generic, &unsignedByte);
                    break;
                case "short":
                    short signedShort = (short)scalar;
                    SetGenericValue(generic, &signedShort);
                    break;
                case "ushort":
                    ushort unsignedShort = (ushort)scalar;
                    SetGenericValue(generic, &unsignedShort);
                    break;
                case "int":
                    int signedInteger = (int)scalar;
                    SetGenericValue(generic, &signedInteger);
                    break;
                case "uint":
                    uint unsignedInteger = (uint)scalar;
                    SetGenericValue(generic, &unsignedInteger);
                    break;
                case "long":
                    long signedLong = (long)scalar;
                    SetGenericValue(generic, &signedLong);
                    break;
                case "ulong":
                    ulong unsignedLong = (ulong)scalar;
                    SetGenericValue(generic, &unsignedLong);
                    break;
                case "float":
                    float single = (float)scalar;
                    SetGenericValue(generic, &single);
                    break;
                case "double":
                    double number = (double)scalar;
                    SetGenericValue(generic, &number);
                    break;
                case "nint":
                    nint signedNative = checked((nint)(long)scalar);
                    SetGenericValue(generic, &signedNative);
                    break;
                case "nuint":
                    nuint unsignedNative = checked((nuint)(ulong)scalar);
                    SetGenericValue(generic, &unsignedNative);
                    break;
                default:
                    throw new NotSupportedException(
                        $"Managed function evaluation cannot set an argument of type '{type}'.");
            }
        }
        finally
        {
            _ = ComAbi.Release(generic);
        }
    }

    private static unsafe void SetGenericValue<T>(nint generic, T* value)
        where T : unmanaged =>
        CorDebugHResult.ThrowIfFailed(
            new ICorDebugGenericValueAbi(generic).SetValue((nint)value),
            "ICorDebugGenericValue.SetValue");

    private static unsafe nint CreateFunctionEvaluationHandle(nint value)
    {
        nint dereferenced = 0;
        nint heapValue = 0;
        nint handle = 0;
        try
        {
            dereferenced = DereferenceValue(value);
            heapValue = ComAbi.QueryInterface(
                dereferenced,
                ICorDebugHeapValue2Abi.InterfaceId);
            nint* handleAddress = &handle;
            int createResult = new ICorDebugHeapValue2Abi(heapValue).CreateHandle(
                type: 1,
                (nint)handleAddress);
            handle = Volatile.Read(ref *handleAddress);
            if (createResult < 0)
            {
                if (handle != 0)
                {
                    ReleaseFunctionEvaluationHandle(handle);
                }

                CorDebugHResult.ThrowIfFailed(
                    createResult,
                    "ICorDebugHeapValue2.CreateHandle");
            }

            return RequirePointer(
                Volatile.Read(ref *handleAddress),
                "ICorDebugHeapValue2.CreateHandle");
        }
        finally
        {
            if (heapValue != 0)
            {
                _ = ComAbi.Release(heapValue);
            }

            if (dereferenced != 0)
            {
                _ = ComAbi.Release(dereferenced);
            }
        }
    }

    private static void ReleaseFunctionEvaluationHandle(nint handle)
    {
        if (handle == 0)
        {
            return;
        }

        _ = new ICorDebugHandleValueAbi(handle).Dispose();
        _ = ComAbi.Release(handle);
    }
}
