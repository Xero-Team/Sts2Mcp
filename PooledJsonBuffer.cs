using System;
using System.Buffers;
using System.Diagnostics;

namespace Sts2Mcp;

/// <summary>
/// An <see cref="IBufferWriter{T}"/> over a pooled array, so a response is encoded straight to
/// UTF-8 bytes instead of growing a string and then copying it into a second byte array.
///
/// Every state response used to allocate two full copies of its payload (the serialized string
/// and the <c>UTF8.GetBytes</c> buffer); both are gone. The buffer is rented per response and
/// returned on <see cref="Dispose"/>, so a repeated poll reuses the same array.
/// </summary>
internal sealed class PooledJsonBuffer : IBufferWriter<byte>, IDisposable
{
    private const int InitialSize = 16 * 1024;

    private byte[] _buffer;
    private int _written;
    private bool _disposed;

    internal PooledJsonBuffer(int initialSize = InitialSize)
    {
        _buffer = ArrayPool<byte>.Shared.Rent(Math.Max(1, initialSize));
    }

    /// <summary>The bytes written so far.</summary>
    internal int WrittenCount => _written;

    internal ReadOnlySpan<byte> WrittenSpan =>
        new(_buffer, 0, _written);

    public void Advance(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (_written + count > _buffer.Length)
            throw new InvalidOperationException("Advance pasted the rented buffer.");

        _written += count;
    }

    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        EnsureCapacity(sizeHint);
        return new Memory<byte>(_buffer, _written, _buffer.Length - _written);
    }

    public Span<byte> GetSpan(int sizeHint = 0)
    {
        EnsureCapacity(sizeHint);
        return new Span<byte>(_buffer, _written, _buffer.Length - _written);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = Array.Empty<byte>();
        _written = 0;
    }

    private void EnsureCapacity(int sizeHint)
    {
        Debug.Assert(!_disposed, "PooledJsonBuffer used after Dispose.");
        // IBufferWriter contract: a non-positive hint still has to return at least one byte.
        int needed = Math.Max(1, sizeHint);
        if (_buffer.Length - _written >= needed)
            return;

        int target = Math.Max(_buffer.Length * 2, _written + needed);
        byte[] grown = ArrayPool<byte>.Shared.Rent(target);
        Array.Copy(_buffer, grown, _written);
        ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = grown;
    }
}
