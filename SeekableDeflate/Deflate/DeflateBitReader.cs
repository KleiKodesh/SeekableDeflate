using System.Buffers.Binary;

namespace SeekableDeflate;

/// <summary>
/// LSB-first bit reader over a bounded region of a stream, with the one feature a plain reader
/// lacks: it can report its exact position (byte + leftover bits) and be re-seated there later —
/// what makes a <see cref="DeflateCheckpoint"/> possible. Refills seven bytes at a time from a
/// 64 KB buffer, so the hot decode loop touches the stream rarely and the bit buffer almost never
/// runs dry mid-symbol.
/// </summary>
internal sealed class DeflateBitReader
{
    private readonly Stream _input;
    private readonly long _regionStart;
    private readonly long _regionEnd;
    private readonly byte[] _buffer = new byte[1 << 16];
    private int _bufferPosition;
    private int _bufferLength;
    private long _nextLoadPosition;   // absolute stream position of _buffer[_bufferLength]

    private ulong _bitBuffer;
    private int _bitCount;            // valid low bits of _bitBuffer; bits above are always zero

    public DeflateBitReader(Stream input, long regionStart, long regionLength)
    {
        _input = input;
        _regionStart = regionStart;
        _regionEnd = regionStart + regionLength;
        SeekTo(0, 0, 0);
    }

    /// <summary>Re-seats the reader at a checkpoint position (relative to the region start).</summary>
    public void SeekTo(long compressedBytePosition, int leftoverBitCount, int leftoverBits)
    {
        _nextLoadPosition = _regionStart + compressedBytePosition;
        _bufferPosition = _bufferLength = 0;
        _bitBuffer = (ulong)leftoverBits & ((1UL << leftoverBitCount) - 1);
        _bitCount = leftoverBitCount;
    }

    /// <summary>Position of the first byte not fully consumed, relative to the region start.</summary>
    public long CompressedBytePosition
        => (_nextLoadPosition - _regionStart) - (_bufferLength - _bufferPosition) - (_bitCount / 8);

    public int LeftoverBitCount => _bitCount % 8;

    public int LeftoverBits => (int)(_bitBuffer & ((1UL << LeftoverBitCount) - 1));

    /// <summary>Tops the bit buffer up to at least 57 bits when input remains (seven bytes at once when they are in the buffer).</summary>
    public void Refill()
    {
        while (_bitCount <= 56)
        {
            int available = _bufferLength - _bufferPosition;
            if (available >= 8)
            {
                int bytes = (63 - _bitCount) >> 3;                                      // 1..7
                ulong value = BinaryPrimitives.ReadUInt64LittleEndian(_buffer.AsSpan(_bufferPosition)) & ((1UL << (bytes * 8)) - 1);
                _bitBuffer |= value << _bitCount;
                _bitCount += bytes * 8;
                _bufferPosition += bytes;
                return;
            }
            if (available == 0 && !Fill()) return;
            _bitBuffer |= (ulong)_buffer[_bufferPosition++] << _bitCount;
            _bitCount += 8;
        }
    }

    /// <summary>Makes at least <paramref name="count"/> (≤ 32) bits available, padding with zero bits past the end of the data.</summary>
    public void EnsureBits(int count)
    {
        if (_bitCount >= count) return;
        Refill();
        // Past the end of the data: zero bits let a final peek complete; a real code never needs them,
        // and a truncated stream errors out on the next symbol.
        if (_bitCount < count) _bitCount = count;
    }

    public int PeekBits(int count) => (int)(_bitBuffer & ((1UL << count) - 1));

    public void DropBits(int count)
    {
        _bitBuffer >>= count;
        _bitCount -= count;
    }

    public int ReadBits(int count)
    {
        if (count == 0) return 0;
        EnsureBits(count);
        int value = PeekBits(count);
        DropBits(count);
        return value;
    }

    /// <summary>Discards the bits up to the next byte boundary (stored blocks are byte aligned).</summary>
    public void AlignToByte() => DropBits(_bitCount % 8);

    public bool IsAtEnd => _bitCount == 0 && _bufferPosition == _bufferLength && _nextLoadPosition >= _regionEnd;

    private bool Fill()
    {
        long remaining = _regionEnd - _nextLoadPosition;
        if (remaining <= 0) return false;
        int toRead = (int)Math.Min(_buffer.Length, remaining);
        if (_input.Position != _nextLoadPosition) _input.Position = _nextLoadPosition;
        int read = _input.Read(_buffer, 0, toRead);
        if (read <= 0) return false;
        _bufferPosition = 0;
        _bufferLength = read;
        _nextLoadPosition += read;
        return true;
    }
}
