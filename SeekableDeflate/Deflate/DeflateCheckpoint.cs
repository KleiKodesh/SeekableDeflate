namespace SeekableDeflate;

/// <summary>
/// Everything needed to resume inflating a raw deflate stream at one block boundary (the zran
/// "access point"): where the next block starts in the compressed input (byte + leftover bits of
/// the previous byte, because blocks are not byte aligned), how many bytes were produced before it,
/// and the last 32 KB of output that later back-references may copy from.
/// </summary>
public sealed class DeflateCheckpoint
{
    public const int WindowSize = 32768;

    /// <summary>Position (relative to the deflate data start) of the first byte not yet fully consumed.</summary>
    public long CompressedBytePosition { get; }

    /// <summary>Number of still-unused low bits of the byte before <see cref="CompressedBytePosition"/> (0..7).</summary>
    public int LeftoverBitCount { get; }

    /// <summary>The value of those leftover bits, LSB first (the deflate bit order).</summary>
    public int LeftoverBits { get; }

    /// <summary>Bytes of output produced before this block.</summary>
    public long UncompressedOffset { get; }

    /// <summary>The last min(32 KB, UncompressedOffset) output bytes, oldest first.</summary>
    public byte[] Window { get; }

    public DeflateCheckpoint(long compressedBytePosition, int leftoverBitCount, int leftoverBits,
                             long uncompressedOffset, byte[] window)
    {
        CompressedBytePosition = compressedBytePosition;
        LeftoverBitCount = leftoverBitCount;
        LeftoverBits = leftoverBits;
        UncompressedOffset = uncompressedOffset;
        Window = window;
    }
}
