namespace SeekableDeflate;

/// <summary>Where one entry's raw data sits inside a zip file, straight from the central directory.</summary>
public sealed record ZipEntryLocation(
    string Name,
    long DataOffset,
    long CompressedSize,
    long UncompressedSize,
    ushort CompressionMethod,
    uint Crc32)
{
    public const ushort MethodStored = 0;
    public const ushort MethodDeflate = 8;

    public bool IsStored => CompressionMethod == MethodStored;
    public bool IsDeflate => CompressionMethod == MethodDeflate;
}
