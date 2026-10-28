using System.Text;

namespace SeekableDeflate;

/// <summary>
/// Reads a zip's central directory (Zip64 aware) and reports where an entry's raw data begins,
/// so the deflate layer can work on the original file without ZipArchive and without a copy.
/// Format reference: PKWARE APPNOTE.TXT §4.3 (local header 0x04034b50, central directory
/// 0x02014b50, end of central directory 0x06054b50, Zip64 records 0x06064b50 / 0x07064b50).
/// </summary>
public static class ZipEntryLocator
{
    private const uint EndOfCentralDirectorySignature = 0x06054b50;
    private const uint Zip64EndLocatorSignature = 0x07064b50;
    private const uint Zip64EndRecordSignature = 0x06064b50;
    private const uint CentralFileHeaderSignature = 0x02014b50;
    private const uint LocalFileHeaderSignature = 0x04034b50;
    private const int MaxCommentLength = 0xFFFF;

    public static ZipEntryLocation? Locate(Stream zip, string entryName)
    {
        foreach (ZipEntryLocation entry in EnumerateEntries(zip))
            if (string.Equals(entry.Name, entryName, StringComparison.OrdinalIgnoreCase)) return entry;
        return null;
    }

    public static IEnumerable<ZipEntryLocation> EnumerateEntries(Stream zip)
    {
        (long centralDirectoryOffset, long entryCount) = FindCentralDirectory(zip);
        var reader = new BinaryReader(zip, Encoding.UTF8, leaveOpen: true);
        zip.Position = centralDirectoryOffset;
        for (long i = 0; i < entryCount; i++)
        {
            if (reader.ReadUInt32() != CentralFileHeaderSignature) throw new InvalidDataException("Zip: bad central directory entry.");
            zip.Position += 6;                                     // version made by, version needed, flags
            ushort method = reader.ReadUInt16();
            zip.Position += 4;                                     // time, date
            uint crc = reader.ReadUInt32();
            long compressedSize = reader.ReadUInt32();
            long uncompressedSize = reader.ReadUInt32();
            int nameLength = reader.ReadUInt16();
            int extraLength = reader.ReadUInt16();
            int commentLength = reader.ReadUInt16();
            zip.Position += 8;                                     // disk start, internal/external attributes
            long localHeaderOffset = reader.ReadUInt32();
            string name = Encoding.UTF8.GetString(reader.ReadBytes(nameLength));
            byte[] extra = reader.ReadBytes(extraLength);
            zip.Position += commentLength;
            long afterEntry = zip.Position;

            ReadZip64Extra(extra, ref uncompressedSize, ref compressedSize, ref localHeaderOffset);
            long dataOffset = DataOffsetFromLocalHeader(zip, reader, localHeaderOffset);
            zip.Position = afterEntry;
            yield return new ZipEntryLocation(name, dataOffset, compressedSize, uncompressedSize, method, crc);
        }
    }

    private static long DataOffsetFromLocalHeader(Stream zip, BinaryReader reader, long localHeaderOffset)
    {
        zip.Position = localHeaderOffset;
        if (reader.ReadUInt32() != LocalFileHeaderSignature) throw new InvalidDataException("Zip: bad local file header.");
        zip.Position += 22;                                        // up to the name length field
        int nameLength = reader.ReadUInt16();
        int extraLength = reader.ReadUInt16();
        return localHeaderOffset + 30 + nameLength + extraLength;
    }

    /// <summary>Zip64 extra field (0x0001): the 0xFFFFFFFF placeholders are replaced in field order.</summary>
    private static void ReadZip64Extra(byte[] extra, ref long uncompressedSize, ref long compressedSize, ref long localHeaderOffset)
    {
        int position = 0;
        while (position + 4 <= extra.Length)
        {
            ushort id = BitConverter.ToUInt16(extra, position);
            ushort size = BitConverter.ToUInt16(extra, position + 2);
            position += 4;
            if (id == 0x0001)
            {
                int field = position;
                if (uncompressedSize == 0xFFFFFFFF && field + 8 <= position + size) { uncompressedSize = BitConverter.ToInt64(extra, field); field += 8; }
                if (compressedSize == 0xFFFFFFFF && field + 8 <= position + size) { compressedSize = BitConverter.ToInt64(extra, field); field += 8; }
                if (localHeaderOffset == 0xFFFFFFFF && field + 8 <= position + size) localHeaderOffset = BitConverter.ToInt64(extra, field);
                return;
            }
            position += size;
        }
    }

    private static (long offset, long entryCount) FindCentralDirectory(Stream zip)
    {
        long searchStart = Math.Max(0, zip.Length - MaxCommentLength - 22);
        var tail = new byte[zip.Length - searchStart];
        zip.Position = searchStart;
        ReadExactly(zip, tail);

        for (int i = tail.Length - 22; i >= 0; i--)
        {
            if (BitConverter.ToUInt32(tail, i) != EndOfCentralDirectorySignature) continue;
            long entryCount = BitConverter.ToUInt16(tail, i + 10);
            long offset = BitConverter.ToUInt32(tail, i + 16);
            if (entryCount == 0xFFFF || offset == 0xFFFFFFFF)
            {
                long endRecordPosition = searchStart + i;
                (offset, entryCount) = ReadZip64EndRecord(zip, endRecordPosition) ?? (offset, entryCount);
            }
            return (offset, entryCount);
        }
        throw new InvalidDataException("Zip: end of central directory not found.");
    }

    private static (long offset, long entryCount)? ReadZip64EndRecord(Stream zip, long endRecordPosition)
    {
        long locatorPosition = endRecordPosition - 20;
        if (locatorPosition < 0) return null;
        var reader = new BinaryReader(zip, Encoding.UTF8, leaveOpen: true);
        zip.Position = locatorPosition;
        if (reader.ReadUInt32() != Zip64EndLocatorSignature) return null;
        zip.Position += 4;                                         // disk with the zip64 end record
        long zip64EndPosition = reader.ReadInt64();
        zip.Position = zip64EndPosition;
        if (reader.ReadUInt32() != Zip64EndRecordSignature) return null;
        zip.Position += 8 + 2 + 2 + 4 + 4 + 8;                     // size, versions, disks, entries on this disk
        long entryCount = reader.ReadInt64();
        zip.Position += 8;                                         // central directory size
        long offset = reader.ReadInt64();
        return (offset, entryCount);
    }

    private static void ReadExactly(Stream stream, byte[] buffer)
    {
        int filled = 0;
        while (filled < buffer.Length)
        {
            int read = stream.Read(buffer, filled, buffer.Length - filled);
            if (read <= 0) throw new EndOfStreamException();
            filled += read;
        }
    }
}
