using System.Runtime.InteropServices;
using System.Text;

namespace Csls.Debugger;

/// <summary>
/// Owns the platform-specific environment block passed synchronously to dbgshim.
/// </summary>
internal sealed unsafe class DbgShimEnvironmentBlock : IDisposable
{
    private nint _buffer;

    private DbgShimEnvironmentBlock(nint buffer)
    {
        _buffer = buffer;
    }

    /// <summary>
    /// Gets the unmanaged environment-block address.
    /// </summary>
    internal nint Pointer => _buffer;

    /// <summary>
    /// Encodes the complete target environment for native process creation.
    /// </summary>
    /// <param name="values">The complete target environment.</param>
    /// <returns>An owned, double-null-terminated native environment block.</returns>
    internal static DbgShimEnvironmentBlock Create(
        IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        StringComparer comparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        var environment = new SortedDictionary<string, string>(comparer);
        foreach ((string name, string value) in values)
        {
            ValidateEntry(name, value);
            environment[name] = value;
        }

        var text = new StringBuilder();
        foreach ((string name, string value) in environment)
        {
            ValidateEntry(name, value);
            text.Append(name).Append('=').Append(value).Append('\0');
        }

        text.Append('\0');
        if (OperatingSystem.IsWindows())
        {
            string block = text.ToString();
            nuint byteCount = checked((nuint)(block.Length * sizeof(char)));
            void* buffer = NativeMemory.Alloc(byteCount);
            if (buffer is null)
            {
                throw new InvalidOperationException(
                    "The native UTF-16 environment-block allocation failed.");
            }

            block.AsSpan().CopyTo(new Span<char>(buffer, block.Length));
            return new DbgShimEnvironmentBlock((nint)buffer);
        }

        byte[] bytes = Encoding.UTF8.GetBytes(text.ToString());
        void* utf8Buffer = NativeMemory.Alloc((nuint)bytes.Length);
        if (utf8Buffer is null)
        {
            throw new InvalidOperationException(
                "The native UTF-8 environment-block allocation failed.");
        }

        bytes.CopyTo(new Span<byte>(utf8Buffer, bytes.Length));
        return new DbgShimEnvironmentBlock((nint)utf8Buffer);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        nint buffer = Interlocked.Exchange(ref _buffer, 0);
        if (buffer != 0)
        {
            NativeMemory.Free((void*)buffer);
        }
    }

    private static void ValidateEntry(string name, string? value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.Contains('=', StringComparison.Ordinal) ||
            name.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Environment variable name '{name}' is invalid.",
                nameof(name));
        }

        if (value?.Contains('\0', StringComparison.Ordinal) == true)
        {
            throw new ArgumentException(
                $"Environment variable '{name}' contains a null character.",
                nameof(value));
        }
    }
}
